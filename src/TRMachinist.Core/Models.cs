using System.Numerics;

namespace TRMachinist.Core;

public sealed record MachineComponent(
    string Name,
    string? Parent,
    string? GeometryPath,
    string? RuntimeMeshPath,
    string Role,
    Matrix4x4 GraphicsTransform);

public sealed record AxisDefinition(
    string Name,
    string Type,
    string Component,
    string Junction,
    Vector3 Vector,
    double InitialPosition,
    bool LimitEnabled,
    double Lower,
    double Upper,
    double MaximumVelocity)
{
    public bool IsRotary => Type.Contains("Rotary", StringComparison.OrdinalIgnoreCase) ||
                            Type.Contains("Spindle", StringComparison.OrdinalIgnoreCase);
}

public sealed record JunctionDefinition(
    string Name,
    string Owner,
    Vector3 Origin,
    Matrix4x4 Orientation);

public sealed record GeometryAsset(
    string CadPath,
    string? RuntimeMeshPath,
    string Format,
    long Size,
    string? Sha256);

public sealed class MachinePackage
{
    public required string SourcePath { get; init; }
    public required string CacheRoot { get; init; }
    public required string PackageSha256 { get; init; }
    public required string Schema { get; init; }
    public required string MachineName { get; init; }
    public required string ControllerFamily { get; init; }
    public IReadOnlySet<string> ControllerCommands { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public required string RootComponent { get; init; }
    public required IReadOnlyList<MachineComponent> Components { get; init; }
    public required IReadOnlyList<AxisDefinition> Axes { get; init; }
    public required IReadOnlyList<JunctionDefinition> Junctions { get; init; }
    public required IReadOnlyList<GeometryAsset> Geometry { get; init; }
    public required IReadOnlySet<string> CollidableComponents { get; init; }

    public bool HasRuntimeMeshes => Components.Any(x => !string.IsNullOrWhiteSpace(x.RuntimeMeshPath));
    public string Resolve(string packagePath) => Path.Combine(CacheRoot, packagePath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record CoordinateFrame(
    string Label,
    Vector3 Origin,
    Vector3 XAxis,
    Vector3 YAxis,
    Vector3 ZAxis,
    string Source)
{
    public static CoordinateFrame Identity(string label = "IDENTITY") =>
        new(label, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "fallback");
}

public sealed record JobModelAsset(string Role, string Path, string Title, string Color);

public sealed record ToolProfileSection(
    double Diameter,
    double Length,
    double TaperAngle,
    double CornerRadius);

public sealed record JobTool(
    string Id,
    string Name,
    string Type,
    double Diameter,
    double Radius,
    double Length,
    string Holder,
    string? ModelPath,
    string Subtype,
    double FluteLength,
    double ShankDiameter,
    double TipAngle,
    double TaperAngle,
    double ToolInsertion,
    string HolderLibraryReference,
    string GeometrySource,
    IReadOnlyList<ToolProfileSection> ShankSections,
    IReadOnlyList<ToolProfileSection> HolderSections,
    Vector3? MountPoint = null,
    Vector3? TipPoint = null);

public sealed record JobOperation(
    string Id,
    int Number,
    string Name,
    string Program,
    string Type,
    string ToolId,
    double Feed,
    double Spindle,
    string? ToolpathPath);

public sealed class JobPackage
{
    public required string SourcePath { get; init; }
    public required string CacheRoot { get; init; }
    public required string PackageSha256 { get; init; }
    public required string Format { get; init; }
    public required string PartNumber { get; init; }
    public required string PartName { get; init; }
    public required string Program { get; init; }
    public required bool HasExplicitMachineMount { get; init; }
    public required bool HasExplicitControllerWorkFrame { get; init; }
    public required CoordinateFrame Mcs { get; init; }
    public required CoordinateFrame MachineMount { get; init; }

    /// <summary>
    /// axisConvention declared for machineMountCsys.  Only "stl-wcs-local"
    /// guarantees that the CSYS values and the exported STL geometry live in
    /// the same space.  An "nx-csys" package carries the frames in the NX
    /// coordinate space instead - the NX2412 raw export proves the gap is real:
    /// it declares the mount at Z-100 while the exported fixture base is at
    /// STL Z0, and the MCS at Z94.5 while the part top is at Z194.5.  Both are
    /// off by exactly the same 100 mm, so the declared height is unusable.
    /// </summary>
    public required string MountAxisConvention { get; init; }

    public bool MountFrameSharesStlSpace =>
        MountAxisConvention.Contains("stl-wcs-local", StringComparison.OrdinalIgnoreCase);
    public required CoordinateFrame ControllerWorkFrame { get; init; }
    public required IReadOnlyList<JobModelAsset> Models { get; init; }
    public required IReadOnlyList<JobTool> Tools { get; init; }
    public required IReadOnlyList<JobOperation> Operations { get; init; }

    public string Resolve(string packagePath) => Path.Combine(CacheRoot, packagePath.Replace('/', Path.DirectorySeparatorChar));
}

public readonly record struct AxisState(double X, double Y, double Z, double A, double B, double C)
{
    public double Get(char axis) => char.ToUpperInvariant(axis) switch
    {
        'X' => X,
        'Y' => Y,
        'Z' => Z,
        'A' => A,
        'B' => B,
        'C' => C,
        _ => 0
    };

    public AxisState With(char axis, double value) => char.ToUpperInvariant(axis) switch
    {
        'X' => this with { X = value },
        'Y' => this with { Y = value },
        'Z' => this with { Z = value },
        'A' => this with { A = value },
        'B' => this with { B = value },
        'C' => this with { C = value },
        _ => this
    };

    public static AxisState Lerp(AxisState a, AxisState b, double t) => new(
        LerpValue(a.X, b.X, t), LerpValue(a.Y, b.Y, t), LerpValue(a.Z, b.Z, t),
        LerpValue(a.A, b.A, t), LerpValue(a.B, b.B, t), LerpValue(a.C, b.C, t));

    private static double LerpValue(double a, double b, double t) => a + ((b - a) * t);
}

public enum MotionKind
{
    None,
    Rapid,
    Linear,
    CircularClockwise,
    CircularCounterClockwise
}

public sealed record GCodeBlock(
    int SourceLine,
    string Raw,
    MotionKind Motion,
    AxisState Start,
    AxisState End,
    double Feed,
    double Spindle,
    int? Tool,
    string? Warning,
    string? Error,
    MotionPath? Path = null,
    GeneratedCycleMotion? CycleMotion = null,
    bool IsToolChange = false)
{
    /// <summary>
    /// A helical G2/G3 block can return to its own start point in every axis
    /// except the helix axis, and a full circle returns to it entirely, so the
    /// start/end comparison alone is not enough to decide whether the block
    /// moves.  An interpolated path is motion by definition.
    /// </summary>
    public bool HasMotion => Motion != MotionKind.None && (Start != End || Path is { Samples.Count: > 0 });
    /// <summary>An unresolved controller command makes this and subsequent motion unverified.</summary>
    public bool ExecutionBlocked { get; init; }
}

/// <summary>
/// Interpolated tool path of a single NC block: the sampled positions between
/// (excluding) Start and (including) End, plus the real travelled length.  G2/G3
/// blocks are sampled along the true arc - including the extra revolutions of a
/// SINUMERIK TURN= helix - instead of being drawn as a straight chord.
/// </summary>
public sealed record MotionPath(IReadOnlyList<AxisState> Samples, double Length, int Revolutions);

public enum CycleMotionPhaseKind
{
    RapidPosition,
    RapidApproach,
    FeedIn,
    PeckRetract,
    Dwell,
    FeedRetract,
    RapidRetract,
    /// <summary>
    /// Restores the pre-cycle programmed level after the generated drilling
    /// sub-motion.  Keeping this logical endpoint is what prevents an MCALL
    /// implementation from changing a later TRAORI section that Test-22-r2
    /// already resolved correctly.
    /// </summary>
    RapidReturnToProgrammedLevel
}

/// <summary>A generated sub-motion of one modal drilling-cycle invocation.</summary>
public sealed record CycleMotionPhase(
    CycleMotionPhaseKind Kind,
    AxisState Start,
    AxisState End,
    double Feed,
    double DwellSeconds = 0);

/// <summary>
/// Cycle metadata retained beside the sampled path so timeline, diagnostics and
/// later material-removal code do not lose the individual generated phases.
/// </summary>
public sealed record GeneratedCycleMotion(
    string CycleName,
    /// <summary>The plane-position block that actually triggered this hole.</summary>
    int SourceLine,
    /// <summary>The earlier MCALL block that activated the modal cycle.</summary>
    int ActivationSourceLine,
    IReadOnlyList<CycleMotionPhase> Phases,
    double DwellSeconds);

public sealed record GCodeSourceLine(
    int SourceLine,
    string Raw,
    int? BlockIndex,
    string? Warning,
    string? Error)
{
    public bool IsExecutable => BlockIndex.HasValue;
}

public sealed class GCodeProgram
{
    public required string SourcePath { get; init; }
    public required IReadOnlyList<GCodeBlock> Blocks { get; init; }
    public required IReadOnlyList<GCodeSourceLine> SourceLines { get; init; }
    public int MotionCount => Blocks.Count(x => x.HasMotion);
    public int ErrorCount => Blocks.Count(x => !string.IsNullOrWhiteSpace(x.Error));
    public int WarningCount => Blocks.Count(x => !string.IsNullOrWhiteSpace(x.Warning));
}

public sealed record GCodeSimulationContext(
    CoordinateFrame WorkFrame,
    IReadOnlyDictionary<int, double> ToolGaugeLengths,
    CoordinateFrame? ControllerWorkFrame = null,
    Matrix4x4? ControllerToVisualTransform = null,
    IReadOnlyDictionary<string, int>? ToolNumbersByName = null,
    IReadOnlyDictionary<int, double>? ToolRadii = null);

public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;
    public bool Intersects(Bounds3 other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
}

public sealed class TriangleMeshData
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required int[] Indices { get; init; }
    public required Bounds3 Bounds { get; init; }
    public int TriangleCount => Indices.Length / 3;
}

public sealed record TaggedTriangleMeshData(int Tag, TriangleMeshData Mesh);
