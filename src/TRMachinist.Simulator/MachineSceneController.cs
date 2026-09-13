using System.IO;
using System.Numerics;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

internal sealed class MachineSceneController
{
    private const double Alpha4StockPitchMm = 0.15;

    private sealed class SceneNode
    {
        public required string Name { get; init; }
        public string DisplayName { get; init; } = "";
        public required ModelVisual3D Visual { get; init; }
        public Model3D? Model { get; set; }
        public SceneNode? Parent { get; set; }
        public Bounds3? LocalBounds { get; set; }
        public IReadOnlyList<Bounds3> CollisionLeafBounds { get; set; } = Array.Empty<Bounds3>();
        public MeshCollisionGeometry? CollisionMesh { get; set; }
        public string Group { get; set; } = "machine";
        public string CollisionRole { get; set; } = "";
        public bool IsVisible { get; set; } = true;
        public Color Color { get; set; } = Colors.Gray;
        public double BaseOpacity { get; set; } = 1;
        public List<Visual3D> Overlays { get; } = new();
    }

    private sealed class CollisionProxy
    {
        public CollisionProxy(
            string key,
            string label,
            SceneNode node,
            Bounds3 localBounds,
            string kind,
            bool isDynamic)
        {
            Key = key;
            Label = label;
            Node = node;
            LocalBounds = localBounds;
            Kind = kind;
            IsDynamic = isDynamic;
            LocalShapes = kind.StartsWith("tool-", StringComparison.Ordinal)
                ? new[] { localBounds }
                : node.CollisionLeafBounds.Count > 0
                    ? node.CollisionLeafBounds
                    : new[] { localBounds };
            WorldShapes = new CollisionWorldShape[LocalShapes.Count];
            // Tool regions retain their separate cutter/shank/holder contract.
            // Machine and fixture STL bodies use their actual surface mesh.
            MeshInstance = kind.StartsWith("tool-", StringComparison.Ordinal)
                ? null : node.CollisionMesh?.CreateInstance();
        }

        public string Key { get; }
        public string Label { get; }
        public SceneNode Node { get; }
        public Bounds3 LocalBounds { get; }
        public string Kind { get; }
        public bool IsDynamic { get; }
        public IReadOnlyList<Bounds3> LocalShapes { get; }
        public CollisionWorldShape[] WorldShapes { get; }
        public OrientedBounds3 RootBox { get; private set; }
        public bool HasWorld => _hasWorld;
        public MeshCollisionGeometry.Instance? MeshInstance { get; }
        private Matrix4x4 _lastWorld;
        private bool _hasWorld;

        public bool UpdateWorld(Matrix4x4 matrix)
        {
            if (_hasWorld && matrix.Equals(_lastWorld)) return false;
            _hasWorld = true;
            _lastWorld = matrix;
            MeshInstance?.UpdateWorld(matrix);
            RootBox = OrientedBounds3.Transform(LocalBounds, matrix);
            for (var index = 0; index < LocalShapes.Count; index++)
            {
                var box = OrientedBounds3.Transform(LocalShapes[index], matrix);
                WorldShapes[index] = new CollisionWorldShape(box, box.ToAxisAlignedBounds());
            }
            return true;
        }
    }

    private readonly record struct CollisionWorldShape(OrientedBounds3 Box, Bounds3 AxisAlignedBounds);
    private readonly record struct CollisionPair(int First, int Second, string Key);

    public sealed record VisibilityItem(string Key, string Label, string Group, bool IsVisible);
    public sealed record Alpha4IpwTestReport(
        string Phase,
        Bounds3 Bounds,
        double PitchX,
        double PitchY,
        double PitchZ,
        int SamplesX,
        int SamplesY,
        int SamplesZ,
        TripleDexelVolume Initial,
        TripleDexelVolume Current,
        TripleDexelVolume Removed,
        double ExpectedRemoved,
        bool RapidNoCutPassed,
        int CutCount,
        int MeshTriangles,
        double GougeVolume,
        double ExcessStockVolume);
    public sealed record PreparedIpwVisualChunk(IpwSurfaceChunk Surface)
    {
        public IpwChunkKey Key => Surface.Key;
        public int TriangleCount => Surface.TriangleCount;
    }
    public sealed record PreparedJobAsset(
        JobModelAsset Asset,
        TriangleMeshData Mesh,
        MeshGeometry3D Geometry,
        Point3DCollection? FeatureEdgePoints,
        IReadOnlyList<Bounds3> CollisionLeafBounds,
        MeshCollisionGeometry CollisionMesh);
    public sealed record PreparedJobScene(
        JobPackage Job,
        IReadOnlyList<PreparedJobAsset> Assets);
    public sealed record CollisionHit(
        string Key,
        string First,
        string Second,
        bool IsClearanceOnly,
        double ClearanceMm);
    public readonly record struct CollisionScan(
        int ProxyCount,
        int CandidateCount,
        int BaselineSuppressedCount,
        IReadOnlyList<CollisionHit> Hits,
        double ElapsedMilliseconds,
        bool IsCached);
    private readonly HelixViewport3D _viewport;
    private readonly Dictionary<string, SceneNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AxisDefinition> _axisByComponent = new(StringComparer.OrdinalIgnoreCase);
    private ModelVisual3D? _machineRoot;
    private SceneNode? _jobRoot;
    private SceneNode? _toolRoot;
    private SceneNode? _stockNode;
    private SceneNode? _alpha4IpwNode;
    private MachinePackage? _machine;
    private JobPackage? _job;
    private TripleDexelStock? _alpha4Ipw;
    private readonly Dictionary<IpwChunkKey, int> _alpha4IpwChunkTriangles = new();
    private readonly StockMotionCache _alpha4StockMotionCache = new();
    // The exact interval stock has one writer and one regional surface reader.
    // Both use this short gate so no coarse duplicate stock is necessary.
    private readonly object _alpha4StockGate = new();
    private ZDexelTarget? _alpha4Target;
    private TripleDexelVolume _alpha4InitialVolume;
    private TripleDexelVolume _alpha4CurrentVolume;
    private int _alpha4CutCount;
    private int _alpha4MeshTriangles;
    private int _alpha4OperationTag;
    private readonly Dictionary<GCodeBlock, int> _alpha4OperationTags = new(ReferenceEqualityComparer.Instance);
    private bool _alpha4IpwDisplayVisible = true;

    public void ConfigureAlpha4NcProgram(GCodeProgram program)
    {
        _alpha4OperationTags.Clear();
        var operations = GCodeOperationCatalog.Build(program, _job?.Operations);
        for (var index = 0; index < operations.Count; index++)
            for (var block = operations[index].StartBlockIndex; block <= operations[index].EndBlockIndex; block++)
                _alpha4OperationTags[program.Blocks[block]] = index + 1;
    }
    private ZDexelTargetComparison _alpha4LastComparison;
    private readonly Dictionary<int, double> _toolGaugeLengths = new();
    private readonly Dictionary<string, JobTool> _toolOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Matrix3D _baseWorkpiecePlacement = Matrix3D.Identity;
    private double _machineOpacity = 1;
    private double _stockOpacity = 1.0;
    private int _jobTriangleCount;
    private readonly HashSet<string> _collisionBaselinePairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CollisionProxy> _collisionProxies = new();
    private readonly List<CollisionPair> _collisionPairs = new();
    private readonly Dictionary<SceneNode, Matrix4x4> _collisionTransformCache = new();
    private bool _collisionTopologyDirty = true;
    private bool _hasCollisionScan;
    private double _lastCollisionClearanceMm;
    private long _alpha4StockRevision;
    private long _lastCollisionStockRevision = -1;
    private CollisionScan _lastCollisionScan;
    private JobTool? _activeToolDefinition;
    private IReadOnlyList<ToolCollisionGeometry.Region> _activeToolCollisionRegions =
        Array.Empty<ToolCollisionGeometry.Region>();

    public event Action<IReadOnlyList<PreparedIpwVisualChunk>>? Alpha4VisualChunksPublished;
    public event Action? Alpha4VisualCleared;

    public MachineSceneController(HelixViewport3D viewport) => _viewport = viewport;

    public int MachineMeshCount { get; private set; }
    public int JobMeshCount { get; private set; }
    public int TriangleCount { get; private set; }
    public CoordinateFrame? LastTableFrame { get; private set; }
    public Matrix3D LastWorkpiecePlacement { get; private set; } = Matrix3D.Identity;
    public Vector3 WorkpieceCsysOffset { get; private set; }
    public IReadOnlyList<JobTool> EffectiveTools => _job is null
        ? Array.Empty<JobTool>()
        : _job.Tools.Select(ResolveTool).ToArray();
    public Matrix3D WorkpieceWorldTransform => _jobRoot is null
        ? Matrix3D.Identity
        : WorldTransformMatrix(_jobRoot);
    public Point3D? CurrentTableCenter
    {
        get
        {
            if (LastTableFrame is not { } frame) return null;
            var center = new Point3D(frame.Origin.X, frame.Origin.Y, frame.Origin.Z);
            if (_machine is null) return center;
            return WorldTransformMatrix(FindJobParent()).Transform(center);
        }
    }

    public void LoadMachine(MachinePackage machine, Action<string>? progress = null)
    {
        Clear();
        _machine = machine;
        _machineRoot = new ModelVisual3D();
        _viewport.Children.Add(_machineRoot);
        foreach (var axis in machine.Axes.Where(x => !string.IsNullOrWhiteSpace(x.Component)))
            _axisByComponent[axis.Component] = axis;

        foreach (var component in machine.Components)
        {
            var visual = new ModelVisual3D();
            var node = new SceneNode
            {
                Name = component.Name,
                DisplayName = component.Name,
                Visual = visual,
                Group = "machine",
                CollisionRole = component.Role
            };
            _nodes[component.Name] = node;
            if (!string.IsNullOrWhiteSpace(component.RuntimeMeshPath))
            {
                var file = machine.Resolve(component.RuntimeMeshPath);
                if (File.Exists(file))
                {
                    progress?.Invoke($"Makine mesh: {component.Name}");
                    var data = StlMeshReader.Load(file);
                    var color = ColorFor(component.Name);
                    node.LocalBounds = data.Bounds;
                    node.CollisionLeafBounds = BuildSpatialCollisionLeaves(data);
                    if (MachineCollisionPolicy.ShouldMonitorMachineComponent(
                            component.Name, component.Role, machine.CollidableComponents, machine.Axes))
                        node.CollisionMesh = new MeshCollisionGeometry(data);
                    node.Color = color;
                    node.BaseOpacity = 1;
                    node.Model = WpfMeshFactory.Create(data, color, 1);
                    var graphicsTransform = new MatrixTransform3D(ToMatrix3D(component.GraphicsTransform));
                    node.Model.Transform = graphicsTransform;
                    visual.Content = node.Model;
                    var edges = WpfMeshFactory.CreateFeatureEdges(data, Color.FromRgb(42, 50, 55), 0.66, 24);
                    if (edges is not null)
                    {
                        edges.Transform = graphicsTransform;
                        visual.Children.Add(edges);
                        node.Overlays.Add(edges);
                    }
                    MachineMeshCount++;
                    TriangleCount += data.TriangleCount;
                }
            }
        }

        foreach (var component in machine.Components)
        {
            var node = _nodes[component.Name];
            if (!string.IsNullOrWhiteSpace(component.Parent) && _nodes.TryGetValue(component.Parent, out var parent))
            {
                node.Parent = parent;
                parent.Visual.Children.Add(node.Visual);
            }
            else
            {
                _machineRoot.Children.Add(node.Visual);
            }
        }

        if (Environment.GetEnvironmentVariable("TRMACHINIST_SHOW_PIVOTS") == "1")
            AddDiagnosticPivotVisuals(machine);

        LastTableFrame = GetTableFrame(machine);
        ApplyAxes(InitialState(machine));
        CaptureCollisionBaseline();
        _viewport.ZoomExtents(300);
    }

    /// <summary>
    /// Reads and converts every workpiece STL without touching the live visual
    /// tree.  The returned WPF resources are frozen, so this entire expensive
    /// phase can safely run away from the dispatcher.
    /// </summary>
    public PreparedJobScene PrepareJob(JobPackage job, CancellationToken cancellationToken = default)
    {
        var loadedAssets = new List<PreparedJobAsset>();
        foreach (var asset in job.Models.Where(x => x.Path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = job.Resolve(asset.Path);
            if (!File.Exists(path)) continue;

            var mesh = StlMeshReader.Load(path);
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = WpfMeshFactory.PrepareGeometry(mesh);
            var roleOpacity = IsStockRole(asset.Role) ? _stockOpacity : OpacityForJobRole(asset.Role);
            var edgePoints = roleOpacity > 0.5
                ? WpfMeshFactory.PrepareFeatureEdgePoints(mesh, 24)
                : null;
            loadedAssets.Add(new PreparedJobAsset(
                asset,
                mesh,
                geometry,
                edgePoints,
                BuildSpatialCollisionLeaves(mesh),
                new MeshCollisionGeometry(mesh)));
        }
        return new PreparedJobScene(job, loadedAssets);
    }

    /// <summary>
    /// Attaches an already prepared job to the live WPF scene.  This phase is
    /// deliberately small and must run on the dispatcher.
    /// </summary>
    public void LoadJob(PreparedJobScene prepared, Action<string>? progress = null)
    {
        if (_machine is null) throw new InvalidOperationException("Önce .trmac makine paketi açılmalı.");
        RemoveJob();
        var job = prepared.Job;
        _job = job;

        var parent = FindJobParent();
        var rootVisual = new ModelVisual3D();
        var root = new SceneNode { Name = "NX_JOB", DisplayName = "NX işi", Visual = rootVisual, Parent = parent, Group = "workpiece" };
        parent.Visual.Children.Add(rootVisual);
        _jobRoot = root;

        var tableFrame = GetTableFrame(_machine);
        LastTableFrame = tableFrame;
        WorkpieceCsysOffset = Vector3.Zero;
        _baseWorkpiecePlacement = BuildPlacement(job.MachineMount, tableFrame);
        LastWorkpiecePlacement = _baseWorkpiecePlacement;
        rootVisual.Transform = new MatrixTransform3D(LastWorkpiecePlacement);

        foreach (var loaded in prepared.Assets)
        {
            var asset = loaded.Asset;
            var data = loaded.Mesh;
            progress?.Invoke($"İş modeli: {asset.Title}");
            var roleColor = ColorForJobRole(asset.Role, asset.Color);
            var roleOpacity = IsStockRole(asset.Role) ? _stockOpacity : OpacityForJobRole(asset.Role);
            var model = WpfMeshFactory.Create(loaded.Geometry, roleColor, roleOpacity);
            var visual = new ModelVisual3D { Content = model };
            rootVisual.Children.Add(visual);
            var node = new SceneNode
            {
                Name = $"JOB:{asset.Role}:{JobMeshCount + 1}",
                DisplayName = JobRoleLabel(asset.Role, asset.Title),
                Visual = visual,
                Model = model,
                Parent = root,
                LocalBounds = data.Bounds,
                CollisionLeafBounds = loaded.CollisionLeafBounds,
                CollisionMesh = loaded.CollisionMesh,
                Group = "workpiece",
                CollisionRole = asset.Role,
                Color = roleColor,
                BaseOpacity = roleOpacity
            };
            _nodes[node.Name] = node;
            if (IsStockRole(asset.Role) && _stockNode is null) _stockNode = node;
            if (roleOpacity > 0.5)
            {
                var edgeColor = asset.Role.Contains("fixture", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromRgb(28, 82, 42)
                    : Color.FromRgb(49, 57, 61);
                var edges = loaded.FeatureEdgePoints is null
                    ? null
                    : WpfMeshFactory.CreateFeatureEdges(loaded.FeatureEdgePoints, edgeColor, 0.78);
                if (edges is not null)
                {
                    visual.Children.Add(edges);
                    node.Overlays.Add(edges);
                }
            }
            JobMeshCount++;
            TriangleCount += data.TriangleCount;
            _jobTriangleCount += data.TriangleCount;
        }

        SetActiveTool(job.Tools.FirstOrDefault()?.Id);
        CaptureCollisionBaseline();
    }

    /// <summary>
    /// Tool currently mounted in the scene.  The window compares against this
    /// instead of its own cached copy, so a seek into the middle of a program
    /// can never be skipped because the two drifted apart.
    /// </summary>
    public string? ActiveToolId { get; private set; }

    public void SetActiveTool(string? toolId)
    {
        _collisionTopologyDirty = true;
        ActiveToolId = null;
        _activeToolDefinition = null;
        _activeToolCollisionRegions = Array.Empty<ToolCollisionGeometry.Region>();
        if (_toolRoot is not null)
        {
            _toolRoot.Parent?.Visual.Children.Remove(_toolRoot.Visual);
            _nodes.Remove(_toolRoot.Name);
            _toolRoot = null;
        }
        if (_job is null || _machine is null || string.IsNullOrWhiteSpace(toolId)) return;
        var tool = EffectiveTools.FirstOrDefault(x => NormalizeTool(x.Id) == NormalizeTool(toolId));
        if (tool is null) return;
        var pocketJunction = _machine.Junctions.FirstOrDefault(x =>
            x.Name.Equals("POCKET_JCT", StringComparison.OrdinalIgnoreCase))
            ?? _machine.Junctions.FirstOrDefault(x => x.Name.Equals("S", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(".trmac içinde spindle takım bağlama junction'ı (POCKET_JCT/S) yok.");
        var parent = _nodes.TryGetValue(pocketJunction.Owner, out var junctionOwner)
            ? junctionOwner
            : (_nodes.TryGetValue("POCKET", out var pocket) ? pocket :
                (_nodes.TryGetValue("SPINDLE", out var spindle) ? spindle : null));
        if (parent is null)
            throw new InvalidDataException($"Takım bağlama sahibi sahnede bulunamadı: {pocketJunction.Owner}");

        Model3D model;
        TriangleMeshData toolEdgeMesh;
        Bounds3 toolBounds;
        if (!string.IsNullOrWhiteSpace(tool.ModelPath))
        {
            var path = _job.Resolve(tool.ModelPath);
            if (File.Exists(path))
            {
                var mesh = StlMeshReader.Load(path);
                model = BuildNxToolAssemblyModel(mesh, tool);
                toolEdgeMesh = mesh;
                toolBounds = mesh.Bounds;
                _activeToolCollisionRegions = ToolCollisionGeometry.FromNxAssemblyMesh(mesh, tool);
            }
            else
            {
                (model, toolBounds, toolEdgeMesh, _activeToolCollisionRegions) = BuildParametricToolModel(tool);
            }
        }
        else
        {
            (model, toolBounds, toolEdgeMesh, _activeToolCollisionRegions) = BuildParametricToolModel(tool);
        }
        var pocketFrame = new CoordinateFrame(
            pocketJunction.Name,
            pocketJunction.Origin,
            new Vector3(pocketJunction.Orientation.M11, pocketJunction.Orientation.M12, pocketJunction.Orientation.M13),
            new Vector3(pocketJunction.Orientation.M21, pocketJunction.Orientation.M22, pocketJunction.Orientation.M23),
            new Vector3(pocketJunction.Orientation.M31, pocketJunction.Orientation.M32, pocketJunction.Orientation.M33),
            "trmac:canonical-tool-mount");
        // TRJOB 0.2+ may carry the exact local SIM_TOOL_MOUNT/SIM_TOOL_TIP
        // contract.  Older packages retain the documented NX Tool.ExportPart
        // convention (tip at minimum X, holder back at maximum X).
        var mountPoint = tool.MountPoint ?? new Vector3(toolBounds.Max.X, 0, 0);
        var sourceMount = new CoordinateFrame(
            "SIM_TOOL_MOUNT",
            mountPoint,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            tool.MountPoint.HasValue ? "trjob:explicit-tool-mount" : "nx-tool-exportpart-fallback");
        var toolPlacement = CoordinateTransforms.BuildPlacement(sourceMount, pocketFrame);
        model.Transform = new MatrixTransform3D(ToMatrix3D(toolPlacement));
        var visual = new ModelVisual3D { Content = model };
        var toolEdges = WpfMeshFactory.CreateFeatureEdges(toolEdgeMesh, Color.FromRgb(66, 47, 37), 0.82, 24);
        if (toolEdges is not null)
        {
            toolEdges.Transform = new MatrixTransform3D(ToMatrix3D(toolPlacement));
            visual.Children.Add(toolEdges);
        }
        parent.Visual.Children.Add(visual);
        _toolRoot = new SceneNode
        {
            Name = "ACTIVE_TOOL",
            DisplayName = "Aktif takım",
            Visual = visual,
            Model = model,
            Parent = parent,
            LocalBounds = toolBounds,
            Group = "tool",
            Color = Color.FromRgb(198, 205, 209),
            BaseOpacity = 1
        };
        if (toolEdges is not null) _toolRoot.Overlays.Add(toolEdges);
        _nodes[_toolRoot.Name] = _toolRoot;
        ActiveToolId = tool.Id;
        _activeToolDefinition = tool;
    }

    /// <summary>
    /// Replaces only the holder side of an NX tool.  Cutter and shank dimensions
    /// remain those exported by NX.  A parametric assembly is intentionally used
    /// after the override because the original STL contains the old holder.
    /// </summary>
    public JobTool ApplyHolderPreset(string toolId, ToolHolderPreset preset)
    {
        if (_job is null) throw new InvalidOperationException("Önce NX .trjob paketini açın.");
        var original = _job.Tools.FirstOrDefault(tool => NormalizeTool(tool.Id) == NormalizeTool(toolId))
            ?? throw new InvalidOperationException($"İş paketinde takım bulunamadı: {toolId}");
        if (preset.Sections.Count == 0)
            throw new InvalidOperationException("Seçilen tutucu profilinde kademe yok.");

        var updated = original with
        {
            Holder = preset.Name,
            HolderLibraryReference = preset.Id,
            ToolInsertion = Math.Max(0, preset.ToolInsertion),
            HolderSections = preset.Sections.ToArray(),
            ModelPath = null,
            GeometrySource = $"trmachinist-holder-library:{preset.Source}",
            MountPoint = null,
            TipPoint = null
        };
        _toolOverrides[NormalizeTool(original.Id)] = updated;
        InvalidateToolGeometry(original.Id);
        if (ActiveToolId is not null && NormalizeTool(ActiveToolId) == NormalizeTool(original.Id))
            SetActiveTool(original.Id);
        return updated;
    }

    public JobTool ResetHolderOverride(string toolId)
    {
        if (_job is null) throw new InvalidOperationException("Önce NX .trjob paketini açın.");
        var original = _job.Tools.FirstOrDefault(tool => NormalizeTool(tool.Id) == NormalizeTool(toolId))
            ?? throw new InvalidOperationException($"İş paketinde takım bulunamadı: {toolId}");
        _toolOverrides.Remove(NormalizeTool(original.Id));
        InvalidateToolGeometry(original.Id);
        if (ActiveToolId is not null && NormalizeTool(ActiveToolId) == NormalizeTool(original.Id))
            SetActiveTool(original.Id);
        return original;
    }

    /// <summary>
    /// Moves the complete NX work group in the selected machineMountCsys axes.
    /// The caller reparses NC with the new placement, so model, stock, fixture,
    /// controller frame and tool path continue to share one coordinate contract.
    /// </summary>
    public void SetWorkpieceCsysOffset(Vector3 offset)
    {
        if (_jobRoot is null || LastTableFrame is null)
            throw new InvalidOperationException("CSYS ofseti için önce makine ve NX işi açılmalı.");
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z))
            throw new InvalidOperationException("CSYS ofseti geçerli bir sayı olmalı.");
        if (Math.Max(Math.Abs(offset.X), Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z))) > 5000)
            throw new InvalidOperationException("CSYS ofseti ±5000 mm aralığında olmalı.");

        WorkpieceCsysOffset = offset;
        var shifted = CoordinateTransforms.ApplyTargetFrameOffset(
            ToMatrix4x4(_baseWorkpiecePlacement),
            LastTableFrame,
            offset);
        LastWorkpiecePlacement = ToMatrix3D(shifted);
        _jobRoot.Visual.Transform = new MatrixTransform3D(LastWorkpiecePlacement);
        _alpha4StockMotionCache.Clear();
        _collisionTransformCache.Clear();
        _collisionTopologyDirty = true;
        _hasCollisionScan = false;
    }

    private void InvalidateToolGeometry(string toolId)
    {
        var digits = new string(toolId.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var number)) _toolGaugeLengths.Remove(number);
        _alpha4StockMotionCache.Clear();
        _collisionTransformCache.Clear();
        _collisionTopologyDirty = true;
        _hasCollisionScan = false;
    }

    private JobTool ResolveTool(JobTool original) =>
        _toolOverrides.TryGetValue(NormalizeTool(original.Id), out var overridden)
            ? overridden
            : original;

    /// <summary>
    /// Creates the collision engine's accepted initial-contact set.  The U630
    /// package explicitly asks for this because adjacent machine meshes and a
    /// fixture seated on its table legitimately touch at the neutral pose.
    /// </summary>
    public void CaptureCollisionBaseline()
    {
        _collisionBaselinePairs.Clear();
        EnsureCollisionTopology();
        UpdateCollisionWorlds();
        foreach (var pair in _collisionPairs)
        {
            if (DetailedCollisionIntersects(_collisionProxies[pair.First], _collisionProxies[pair.Second], 0))
                _collisionBaselinePairs.Add(pair.Key);
        }
        _hasCollisionScan = false;
    }

    /// <summary>
    /// Fast broad/narrow collision pass over the live machine pose.  Cutting
    /// edge contact with stock/target is intentionally legal; shank, holder,
    /// spindle, fixture and machine contacts are not.
    /// </summary>
    public CollisionScan CheckCollisions(double clearanceMm)
    {
        var started = Stopwatch.GetTimestamp();
        clearanceMm = Math.Clamp(clearanceMm, 0, 5);
        var topologyChanged = EnsureCollisionTopology();
        var worldChanged = UpdateCollisionWorlds();
        var stockRevision = Volatile.Read(ref _alpha4StockRevision);
        if (!topologyChanged && !worldChanged && _hasCollisionScan &&
            stockRevision == _lastCollisionStockRevision &&
            Math.Abs(clearanceMm - _lastCollisionClearanceMm) <= 1e-9)
            return _lastCollisionScan with { ElapsedMilliseconds = 0, IsCached = true };

        List<CollisionHit>? hits = null;
        var suppressed = 0;
        foreach (var pair in _collisionPairs)
        {
            if (_collisionBaselinePairs.Contains(pair.Key))
            {
                suppressed++;
                continue;
            }

            var first = _collisionProxies[pair.First];
            var second = _collisionProxies[pair.Second];
            if (!DetailedCollisionIntersects(first, second, clearanceMm)) continue;
            var actualCollision = clearanceMm <= 1e-9 || DetailedCollisionIntersects(first, second, 0);
            hits ??= new List<CollisionHit>(4);
            hits.Add(new CollisionHit(
                pair.Key,
                first.Label,
                second.Label,
                !actualCollision,
                clearanceMm));
            if (hits.Count >= 32) break;
        }

        AppendLiveIpwToolHits(clearanceMm, ref hits);

        var scan = new CollisionScan(
            _collisionProxies.Count + (_alpha4Ipw is null ? 0 : 1),
            _collisionPairs.Count + LiveIpwToolCandidateCount(),
            suppressed,
            hits is null ? Array.Empty<CollisionHit>() : hits,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            false);
        _lastCollisionClearanceMm = clearanceMm;
        _lastCollisionStockRevision = stockRevision;
        _lastCollisionScan = scan;
        _hasCollisionScan = true;
        return scan;
    }

    public IReadOnlyList<VisibilityItem> GetVisibilityItems() => _nodes.Values
        .Where(x => x.Model is not null)
        .OrderBy(x => GroupOrder(x.Group))
        .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .Select(x => new VisibilityItem(x.Name, VisibilityLabel(x) +
            (_alpha4Ipw is not null && _alpha4IpwDisplayVisible && CollisionWorkpieceKind(x.CollisionRole) == "part"
                ? " · referans kenarları" : ""), x.Group, x.IsVisible))
        .ToArray();

    public void SetComponentVisible(string key, bool visible)
    {
        if (!_nodes.TryGetValue(key, out var node) || node.Model is null) return;
        SetNodeVisible(node, visible);
    }

    public void SetGroupVisible(string group, bool visible)
    {
        foreach (var node in _nodes.Values.Where(x => x.Model is not null && x.Group.Equals(group, StringComparison.OrdinalIgnoreCase)))
            SetNodeVisible(node, visible);
    }

    public void ShowAllComponents()
    {
        foreach (var node in _nodes.Values.Where(x => x.Model is not null)) SetNodeVisible(node, true);
        KeepOriginalStockHiddenDuringAlpha4Test();
    }

    public void HideExteriorComponents()
    {
        foreach (var node in _nodes.Values.Where(x => x.Group == "machine" && IsExterior(x.Name))) SetNodeVisible(node, false);
    }

    public void HideSimulationObstructions()
    {
        foreach (var node in _nodes.Values.Where(x =>
                     x.Group == "machine" && (IsExterior(x.Name) || IsAuxiliarySimulationComponent(x.Name))))
            SetNodeVisible(node, false);
    }

    public void FocusMachiningArea()
    {
        foreach (var node in _nodes.Values.Where(x => x.Model is not null))
        {
            var visible = node.Group != "machine" || IsMachiningAreaComponent(node.Name);
            SetNodeVisible(node, visible);
        }
        ZoomToWorkEnvelope();
    }

    public void FocusJob()
    {
        SetGroupVisible("machine", false);
        SetGroupVisible("workpiece", true);
        SetGroupVisible("tool", true);
        KeepOriginalStockHiddenDuringAlpha4Test();
        ZoomToWorkEnvelope();
    }

    public bool IsAlpha4Test2Active => _alpha4Ipw is not null;
    public int Alpha4PendingVisualChunkCount
    {
        get
        {
            lock (_alpha4StockGate)
                return _alpha4Ipw?.DirtySurfaceChunkCount ?? 0;
        }
    }
    public int Alpha4PendingDisplayCutCount => 0;
    public double StockOpacity => _stockOpacity;
    public Matrix3D Alpha4IpwWorldTransform => _alpha4IpwNode is null
        ? Matrix3D.Identity
        : WorldTransformMatrix(_alpha4IpwNode);

    public Alpha4IpwTestReport InitializeAlpha4Test2()
    {
        if (_jobRoot is null || _job is null)
            throw new InvalidOperationException("Önce NX .trjob iş paketini açın.");
        if (_stockNode?.LocalBounds is not Bounds3 bounds)
            throw new InvalidOperationException("İş paketinde blank/stock STL modeli bulunamadı.");

        EndAlpha4Test2();
        _stockNode = _nodes.Values.FirstOrDefault(node =>
            node.Group == "workpiece" && node.LocalBounds.HasValue &&
            (node.Name.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
             node.Name.Contains("blank", StringComparison.OrdinalIgnoreCase)));
        if (_stockNode?.LocalBounds is not Bounds3 resolvedBounds)
            throw new InvalidOperationException("İş paketinde blank/stock STL modeli bulunamadı.");

        var stockAsset = _job.Models.FirstOrDefault(model =>
            IsStockRole(model.Role) && model.Path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase));
        if (stockAsset is null || !File.Exists(_job.Resolve(stockAsset.Path)))
            throw new InvalidOperationException("İş paketinde okunabilir blank/stock STL modeli bulunamadı.");
        var stockMesh = StlMeshReader.Load(_job.Resolve(stockAsset.Path));
        resolvedBounds = stockMesh.Bounds;
        // Stock accuracy is a fixed engine contract. Playback speed is handled
        // only by SimulationPlayer and can never coarsen this 0.15 mm grid.
        _alpha4Ipw = new TripleDexelStock(
            resolvedBounds,
            Alpha4StockPitchMm,
            stockMesh,
            surfaceCacheEnabled: true);
        Interlocked.Increment(ref _alpha4StockRevision);
        _collisionTopologyDirty = true;
        _hasCollisionScan = false;
        var partAsset = _job.Models.FirstOrDefault(model =>
            model.Role.Contains("part", StringComparison.OrdinalIgnoreCase));
        if (partAsset is not null && File.Exists(_job.Resolve(partAsset.Path)))
        {
            var targetMesh = StlMeshReader.Load(_job.Resolve(partAsset.Path));
            _alpha4Target = _alpha4Ipw.CreateZTarget(targetMesh);
        }
        _alpha4InitialVolume = _alpha4Ipw.Volume();
        _alpha4CurrentVolume = _alpha4InitialVolume;
        _alpha4CutCount = 0;
        _alpha4MeshTriangles = 0;
        _alpha4OperationTag = 0;
        _alpha4StockMotionCache.Clear();
        _alpha4LastComparison = new ZDexelTargetComparison(0, 0, 0);

        var visual = new ModelVisual3D();
        var node = new SceneNode
        {
            Name = "ALPHA4:IPW:TEST9-GPU-R4",
            DisplayName = "Alpha4 IPW Test9 GPU R25 (0,15 mm stok / nüfuz doğrulamalı çarpışma)",
            Visual = visual,
            Parent = _jobRoot,
            LocalBounds = resolvedBounds,
            Group = "workpiece",
            Color = Color.FromRgb(31, 157, 211),
            BaseOpacity = _stockOpacity
        };
        _jobRoot.Visual.Children.Add(visual);
        _nodes[node.Name] = node;
        _alpha4IpwNode = node;
        _alpha4IpwDisplayVisible = true;
        RefreshAlpha4ReferenceParts();
        SetNodeVisible(_stockNode, false);
        var triangles = UpdateAlpha4IpwVisual();
        // Exact 0.15 mm stock and target construction use large temporary
        // raster buffers. They are dead once the first visual is prepared;
        // compact them once while the initialization overlay is still active
        // instead of leaving that memory to pressure live playback later.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return CreateAlpha4Report("BAŞLANGIÇ — GERÇEK BLANK STL", new TripleDexelVolume(), 0, true, triangles);
    }

    public Alpha4IpwTestReport RunAlpha4Test2Cut()
    {
        if (_alpha4Ipw is null)
            throw new InvalidOperationException("Önce 'TESTİ BAŞLAT' düğmesine basın.");

        var bounds = _alpha4Ipw.Bounds;
        var radius = Math.Min(5.0, Math.Min(bounds.Size.X, bounds.Size.Y) / 8.0);
        var margin = radius + (2 * _alpha4Ipw.XPitch);
        var pathLength = Math.Min(30.0, bounds.Size.X - (2 * margin));
        if (radius <= 0.25 || pathLength <= _alpha4Ipw.XPitch)
            throw new InvalidOperationException("Blank, Test2 düz kesme geometrisi için çok küçük.");

        var nominalDepth = Math.Min(5.0, bounds.Size.Z / 4.0);
        var completeZCells = Math.Max(0, (int)Math.Round((nominalDepth / _alpha4Ipw.ZPitch) - 0.5));
        var depth = Math.Min(bounds.Size.Z * 0.8, (completeZCells + 0.5) * _alpha4Ipw.ZPitch);
        var tipZ = bounds.Max.Z - (float)depth;
        var center = bounds.Center;
        var start = new Vector3(center.X - (float)(pathLength * 0.5), center.Y, tipZ);
        var end = new Vector3(center.X + (float)(pathLength * 0.5), center.Y, tipZ);
        var fluteLength = bounds.Size.Z + _alpha4Ipw.ZPitch;

        TripleDexelCutResult rapid;
        TripleDexelCutResult cut;
        lock (_alpha4StockGate)
        {
            rapid = _alpha4Ipw.ApplyFlatEndMillMove(start, end, radius, fluteLength, isRapid: true);
            cut = _alpha4Ipw.ApplyFlatEndMillMove(start, end, radius, fluteLength, isRapid: false);
        }
        var expected = _alpha4CutCount == 0
            ? ((2 * radius * pathLength) + (Math.PI * radius * radius)) * depth
            : 0;
        _alpha4CutCount++;
        SubtractFromCurrentVolume(cut.Removed);
        if (cut.Removed.Mean > 1e-9)
        {
            Interlocked.Increment(ref _alpha4StockRevision);
            _hasCollisionScan = false;
        }
        var triangles = UpdateAlpha4IpwVisual();
        KeepOriginalStockHiddenDuringAlpha4Test();
        return CreateAlpha4Report(
            _alpha4CutCount == 1 ? "G1 DÜZ KESME UYGULANDI" : "AYNI KESME TEKRARLANDI",
            cut.Removed,
            expected,
            rapid.RemovedNothing,
            triangles);
    }

    /// <summary>
    /// Applies exactly the visible progress interval of one NC block to the
    /// triple-dexel stock.  Normal G0 blocks never enter the geometry kernel.
    /// Generated drilling cycles retain their individual rapid/feed phases;
    /// only feed phases are allowed to remove material.
    /// </summary>
    public Alpha4IpwTestReport ApplyAlpha4NcBlockProgress(
        GCodeBlock block,
        double fromProgress,
        double toProgress,
        bool refreshVisual,
        int maximumVisualChunks = int.MaxValue)
    {
        if (_alpha4Ipw is null || _machine is null || _job is null)
            throw new InvalidOperationException("Önce Alpha4 NC IPW testini başlatın.");

        fromProgress = Math.Clamp(fromProgress, 0, 1);
        toProgress = Math.Clamp(toProgress, 0, 1);
        if (toProgress <= fromProgress + 1e-9)
            return CreateAlpha4Report(
                $"NC SATIR {block.SourceLine} — BEKLİYOR",
                new TripleDexelVolume(), 0, true,
                refreshVisual ? UpdateAlpha4IpwVisual(maximumVisualChunks) : _alpha4MeshTriangles,
                refreshVisual);

        if (_alpha4OperationTags.TryGetValue(block, out var operationTag))
            _alpha4OperationTag = operationTag;
        else if (fromProgress <= 1e-9 && block.Raw.Contains("MSG(\"", StringComparison.OrdinalIgnoreCase) &&
            block.Raw.Contains("TOOL", StringComparison.OrdinalIgnoreCase))
            _alpha4OperationTag++;
        else if (_alpha4OperationTag == 0 && block.HasMotion && block.Motion != MotionKind.Rapid)
            _alpha4OperationTag = 1;

        var tool = FindTool(block.Tool);
        if (tool is null || tool.Diameter <= 0)
            return CreateAlpha4Report(
                $"NC SATIR {block.SourceLine} — KESİCİ GEOMETRİSİ YOK",
                new TripleDexelVolume(), 0, block.Motion == MotionKind.Rapid,
                refreshVisual ? UpdateAlpha4IpwVisual(maximumVisualChunks) : _alpha4MeshTriangles,
                refreshVisual);

        var cuttingProfile = ToolCuttingProfileFactory.Create(tool);
        if (!cuttingProfile.IsSupported)
            return CreateAlpha4Report(
                $"NC SATIR {block.SourceLine} — {cuttingProfile.Family} DESTEKLENMİYOR / STOK KORUNDU",
                new TripleDexelVolume(), 0, true,
                refreshVisual ? UpdateAlpha4IpwVisual(maximumVisualChunks) : _alpha4MeshTriangles,
                refreshVisual);

        var removed = new TripleDexelVolume();
        var rapidPassed = true;

        if (block.CycleMotion is { } cycle)
        {
            // Follow the player's phase clock. Formerly every peck was held
            // until the entire hole ended, producing a multi-second stock job
            // and a hole that appeared all at once after the tool retracted.
            var gauge = ToolGaugeLength(tool);
            foreach (var slice in CycleStockProgress.Select(cycle, fromProgress, toProgress))
            {
                var start = ToJobLocalCutterPose(slice.Phase.Start, gauge);
                var end = ToJobLocalCutterPose(slice.Phase.End, gauge);
                lock (_alpha4StockGate)
                    Add(_alpha4Ipw.ApplyLayeredCutterMoveProgress(start.Tip, end.Tip, start.Axis, end.Axis,
                        cuttingProfile.Layers, slice.From, slice.To, _alpha4OperationTag, calculateRemainingVolume: false));
            }
        }
        else if (block.Motion == MotionKind.Rapid)
        {
            // Audit the classification through the same public kernel contract.
            var states = SampleBlockRange(block, fromProgress, toProgress);
            for (var i = 1; i < states.Count; i++)
            {
                var result = ApplyNcCutterSegment(states[i - 1], states[i], tool, cuttingProfile, isRapid: true);
                rapidPassed &= result.RemovedNothing;
            }
        }
        else if (block.HasMotion)
        {
            var stockSegments = GetDeterministicStockSegments(block, tool, cuttingProfile);
            var completedSegments = DeterministicStockMotion.SelectCompleted(
                stockSegments, fromProgress, toProgress).ToArray();
            var coalesced = CoalesceStraightStockSegments(completedSegments, tool);
            if (coalesced.Count > 0)
                Add(ApplyNcCutterPath(coalesced, tool, cuttingProfile));
        }

        if (removed.Mean > 1e-9)
        {
            _alpha4CutCount++;
            SubtractFromCurrentVolume(removed);
            Interlocked.Increment(ref _alpha4StockRevision);
            _hasCollisionScan = false;
        }
        var triangles = refreshVisual ? UpdateAlpha4IpwVisual(maximumVisualChunks) : _alpha4MeshTriangles;
        if (refreshVisual)
            KeepOriginalStockHiddenDuringAlpha4Test();
        return CreateAlpha4Report(
            block.Motion == MotionKind.Rapid
                ? $"NC SATIR {block.SourceLine} — G0 / STOK KORUNDU"
                : $"NC SATIR {block.SourceLine} — {cuttingProfile.Family}",
            removed,
            0,
            rapidPassed,
            triangles,
            refreshVisual);

        void Add(TripleDexelCutResult result)
        {
            removed = new TripleDexelVolume(
                removed.X + result.Removed.X,
                removed.Y + result.Removed.Y,
                removed.Z + result.Removed.Z);
        }
    }

    /// <summary>
    /// Commits a short run of complete, contiguous cutting blocks as one
    /// geometric path. CAM posts commonly linearize one smooth pass into many
    /// tiny G1/G2/G3 records; scanning the same local dexel neighbourhood once
    /// per record is pure overhead. Straight runs collapse to one exact swept
    /// segment. Curved/non-contiguous runs automatically fall back to the
    /// proven per-block kernel, so batching cannot invent a cutting bridge.
    /// </summary>
    public Alpha4IpwTestReport ApplyAlpha4NcBlockBatch(IReadOnlyList<GCodeBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
            throw new ArgumentException("En az bir NC bloğu gerekli.", nameof(blocks));
        if (blocks.Count == 1)
            return ApplyAlpha4NcBlockProgress(blocks[0], 0, 1, refreshVisual: false);

        var first = blocks[0];
        if (_alpha4OperationTags.TryGetValue(first, out var firstTag))
        {
            if (blocks.Any(block => _alpha4OperationTags.GetValueOrDefault(block) != firstTag))
                return ApplyIndividually();
            _alpha4OperationTag = firstTag;
        }
        var tool = FindTool(first.Tool);
        if (tool is null || tool.Diameter <= 0 ||
            blocks.Any(block => !CanBatchAlpha4NcBlock(block, first.Tool)))
            return ApplyIndividually();

        var profile = ToolCuttingProfileFactory.Create(tool);
        if (!profile.IsSupported) return ApplyIndividually();

        var sourceSegments = new List<StockMotionSegment>(blocks.Count * 2);
        foreach (var block in blocks)
            sourceSegments.AddRange(GetDeterministicStockSegments(block, tool, profile));
        if (sourceSegments.Count == 0) return ApplyIndividually();

        var coalesced = CoalesceStraightStockSegments(sourceSegments, tool);
        // A useful batch must remove repeated scans. If the toolpath bends at
        // nearly every source block, the bounded per-block kernel is faster
        // and has the same deterministic result.
        if (coalesced.Count > Math.Max(2, blocks.Count / 2))
            return ApplyIndividually();

        var result = ApplyNcCutterPath(coalesced, tool, profile);
        if (result.Removed.Mean > 1e-9)
        {
            _alpha4CutCount++;
            SubtractFromCurrentVolume(result.Removed);
            Interlocked.Increment(ref _alpha4StockRevision);
            _hasCollisionScan = false;
        }
        return CreateAlpha4Report(
            $"NC SATIR {blocks[^1].SourceLine} — {profile.Family} / {blocks.Count} BLOK TEK SÜPÜRME",
            result.Removed,
            0,
            true,
            _alpha4MeshTriangles,
            refreshMeasurements: false);

        Alpha4IpwTestReport ApplyIndividually()
        {
            Alpha4IpwTestReport? report = null;
            foreach (var block in blocks)
                report = ApplyAlpha4NcBlockProgress(block, 0, 1, refreshVisual: false);
            return report!;
        }
    }

    public static bool CanBatchAlpha4NcBlock(GCodeBlock block, int? toolNumber) =>
        block.HasMotion &&
        block.Motion != MotionKind.Rapid &&
        block.CycleMotion is null &&
        block.Tool == toolNumber &&
        !(block.Raw.Contains("MSG(\"", StringComparison.OrdinalIgnoreCase) &&
          block.Raw.Contains("TOOL", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The deterministic sampler divides a long move so partial playback can
    /// commit stock at stable positions. Once several adjacent samples are in
    /// the background queue, a truly straight, constant-orientation run is the
    /// same swept cylinder as one start/end command. Collapsing only those
    /// collinear samples preserves the exact swept volume while avoiding the
    /// repeated overlapping ray scans that dominate high-speed playback.
    /// Curves and simultaneous orientation changes remain untouched.
    /// </summary>
    private IReadOnlyList<StockMotionSegment> CoalesceStraightStockSegments(
        IReadOnlyList<StockMotionSegment> segments,
        JobTool tool)
    {
        if (segments.Count <= 1) return segments;
        var gauge = ToolGaugeLength(tool);
        var result = new List<StockMotionSegment>(segments.Count);
        var current = segments[0];
        var currentStart = ToJobLocalCutterPose(current.Start, gauge);
        var currentEnd = ToJobLocalCutterPose(current.End, gauge);

        for (var index = 1; index < segments.Count; index++)
        {
            var next = segments[index];
            var nextStart = ToJobLocalCutterPose(next.Start, gauge);
            var nextEnd = ToJobLocalCutterPose(next.End, gauge);
            if (CanMerge(currentStart, currentEnd, nextStart, nextEnd))
            {
                current = new StockMotionSegment(
                    current.StartProgress,
                    next.EndProgress,
                    current.Start,
                    next.End);
                currentEnd = nextEnd;
                continue;
            }

            result.Add(current);
            current = next;
            currentStart = nextStart;
            currentEnd = nextEnd;
        }
        result.Add(current);
        return result;

        static bool CanMerge(
            CutterPose firstStart,
            CutterPose firstEnd,
            CutterPose secondStart,
            CutterPose secondEnd)
        {
            const float continuityTolerance = 1e-4f;
            const float orientationDotTolerance = 0.9999995f;
            if (Vector3.DistanceSquared(firstEnd.Tip, secondStart.Tip) >
                continuityTolerance * continuityTolerance)
                return false;
            if (Vector3.Dot(firstStart.Axis, firstEnd.Axis) < orientationDotTolerance ||
                Vector3.Dot(firstEnd.Axis, secondStart.Axis) < orientationDotTolerance ||
                Vector3.Dot(secondStart.Axis, secondEnd.Axis) < orientationDotTolerance)
                return false;

            var firstTravel = firstEnd.Tip - firstStart.Tip;
            var secondTravel = secondEnd.Tip - secondStart.Tip;
            var firstLength = firstTravel.Length();
            var secondLength = secondTravel.Length();
            if (firstLength <= continuityTolerance || secondLength <= continuityTolerance)
                return true;
            if (Vector3.Dot(firstTravel, secondTravel) <= 0)
                return false;
            return Vector3.Cross(firstTravel, secondTravel).Length() <=
                   firstLength * secondLength * 1e-6f;
        }
    }

    private IReadOnlyList<StockMotionSegment> GetDeterministicStockSegments(
        GCodeBlock block,
        JobTool tool,
        ToolCuttingProfile cuttingProfile)
    {
        if (_alpha4StockMotionCache.TryGetValue(block, out var cached)) return cached;
        var stock = _alpha4Ipw ?? throw new InvalidOperationException("Alpha4 IPW etkin değil.");
        var gauge = ToolGaugeLength(tool);
        var linearStep = cuttingProfile.Layers.Count == 0
            ? stock.TargetPitch * 1.5
            : cuttingProfile.Layers.Min(layer =>
                layer.MotionStep(stock.TargetPitch));
        var orientationReach = cuttingProfile.Layers.Count == 0
            ? 0
            : cuttingProfile.Layers.Max(layer => layer.AxialOffset + layer.Length);
        cached = DeterministicStockMotion.Build(
            block,
            state =>
            {
                var pose = ToJobLocalCutterPose(state, gauge);
                return new StockMotionPose(pose.Tip, pose.Axis);
            },
            linearStep,
            orientationReach);
        _alpha4StockMotionCache[block] = cached;
        return cached;
    }

    private TripleDexelCutResult ApplyNcCutterSegment(
        AxisState start,
        AxisState end,
        JobTool tool,
        ToolCuttingProfile cuttingProfile,
        bool isRapid)
    {
        var stock = _alpha4Ipw ?? throw new InvalidOperationException("Alpha4 IPW etkin değil.");
        var gauge = ToolGaugeLength(tool);
        var startPose = ToJobLocalCutterPose(start, gauge);
        var endPose = ToJobLocalCutterPose(end, gauge);
        lock (_alpha4StockGate)
            return stock.ApplyLayeredCutterMove(
                startPose.Tip,
                endPose.Tip,
                startPose.Axis,
                endPose.Axis,
                cuttingProfile.Layers,
                isRapid,
                _alpha4OperationTag,
                calculateRemainingVolume: false);
    }

    private TripleDexelCutResult ApplyNcCutterPath(
        IReadOnlyList<StockMotionSegment> segments,
        JobTool tool,
        ToolCuttingProfile cuttingProfile)
    {
        var stock = _alpha4Ipw ?? throw new InvalidOperationException("Alpha4 IPW etkin değil.");
        var gauge = ToolGaugeLength(tool);
        var poses = new List<LayeredCutterPose>(segments.Count + 1);
        var first = ToJobLocalCutterPose(segments[0].Start, gauge);
        poses.Add(new LayeredCutterPose(first.Tip, first.Axis));
        var previous = first;
        foreach (var segment in segments)
        {
            var start = ToJobLocalCutterPose(segment.Start, gauge);
            var end = ToJobLocalCutterPose(segment.End, gauge);
            // Completed deterministic segments of one block are contiguous.
            // If malformed source data breaks that contract, retain the exact
            // segment path instead of inventing a cutting bridge.
            if (Vector3.DistanceSquared(previous.Tip, start.Tip) > 1e-8f ||
                Vector3.Dot(previous.Axis, start.Axis) < 0.999999f)
            {
                var removed = new TripleDexelVolume();
                foreach (var item in segments)
                {
                    var result = ApplyNcCutterSegment(
                        item.Start, item.End, tool, cuttingProfile, isRapid: false);
                    removed = new TripleDexelVolume(
                        removed.X + result.Removed.X,
                        removed.Y + result.Removed.Y,
                        removed.Z + result.Removed.Z);
                }
                return new TripleDexelCutResult(removed, new TripleDexelVolume(), false);
            }
            poses.Add(new LayeredCutterPose(end.Tip, end.Axis));
            previous = end;
        }

        lock (_alpha4StockGate)
            return stock.ApplyLayeredCutterPath(
                poses,
                cuttingProfile.Layers,
                isRapid: false,
                _alpha4OperationTag,
                calculateRemainingVolume: false);
    }

    public int ApplyPendingAlpha4DisplayCuts(int maximumCommands) => 0;

    private CutterPose ToJobLocalCutterPose(AxisState state, double gauge)
    {
        if (_machine is null) throw new InvalidOperationException("Makine yüklenmedi.");
        var coordinates = MachineToolCoordinates.For(_machine);
        var tipWorld = coordinates.TipWorld(state, gauge);
        var neutralTip = MachineCoordinateResolver.InverseTransformWorkpiecePoint(
            _machine, tipWorld, state.B, state.C);
        var table = MachineCoordinateResolver.BuildWorkpieceTransform(_machine, state.B, state.C);
        if (!Matrix4x4.Invert(table, out var inverseTable))
            throw new InvalidOperationException("İş parçası dönüş matrisi terslenemiyor.");
        // A direction has no origin. Subtracting two nearby transformed float
        // positions made a fixed axis depend on XYZ position and gauge length.
        var neutralAxis = Vector3.TransformNormal(coordinates.ToolAxis, inverseTable);

        var inversePlacement = LastWorkpiecePlacement;
        if (!inversePlacement.HasInverse)
            throw new InvalidOperationException("İş parçası yerleştirme matrisi terslenemiyor.");
        inversePlacement.Invert();
        var localTip = inversePlacement.Transform(new Point3D(neutralTip.X, neutralTip.Y, neutralTip.Z));
        var localAxis = inversePlacement.Transform(new Vector3D(neutralAxis.X, neutralAxis.Y, neutralAxis.Z));
        var tip = new Vector3((float)localTip.X, (float)localTip.Y, (float)localTip.Z);
        var axis = new Vector3(
            (float)localAxis.X,
            (float)localAxis.Y,
            (float)localAxis.Z);
        return new CutterPose(tip, axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitZ);
    }

    private double ToolGaugeLength(JobTool tool)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        var number = int.TryParse(digits, out var parsed) ? parsed : -1;
        if (_toolGaugeLengths.TryGetValue(number, out var cached)) return cached;

        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && _job is not null && File.Exists(_job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(_job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        var mountPoint = tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0);
        var tipPoint = tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0);
        var gauge = Vector3.Distance(mountPoint, tipPoint);
        _toolGaugeLengths[number] = gauge;
        return gauge;
    }

    private JobTool? FindTool(int? number)
    {
        if (!number.HasValue || _job is null) return null;
        return EffectiveTools.FirstOrDefault(tool =>
        {
            var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var parsed) && parsed == number.Value;
        });
    }

    private static IReadOnlyList<AxisState> SampleBlockRange(
        GCodeBlock block,
        double fromProgress,
        double toProgress)
    {
        var result = new List<AxisState> { SampleNormalBlock(block, fromProgress) };
        if (block.Path is { Samples.Count: > 0 } path)
        {
            var firstBoundary = (int)Math.Floor((fromProgress * path.Samples.Count) + 1e-9) + 1;
            var lastBoundary = (int)Math.Floor((toProgress * path.Samples.Count) + 1e-9);
            for (var boundary = firstBoundary; boundary <= lastBoundary && boundary <= path.Samples.Count; boundary++)
                result.Add(path.Samples[boundary - 1]);
        }
        result.Add(SampleNormalBlock(block, toProgress));
        return result;
    }

    private static AxisState SampleNormalBlock(GCodeBlock block, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (block.Path is not { Samples.Count: > 0 } path)
            return AxisState.Lerp(block.Start, block.End, progress);
        if (progress <= 0) return block.Start;
        if (progress >= 1) return block.End;
        var scaled = progress * path.Samples.Count;
        var index = (int)Math.Floor(scaled);
        var local = scaled - index;
        var from = index <= 0 ? block.Start : path.Samples[index - 1];
        var to = path.Samples[Math.Min(index, path.Samples.Count - 1)];
        return AxisState.Lerp(from, to, local);
    }

    private static bool IsCuttingCyclePhase(CycleMotionPhase phase) =>
        phase.Kind is CycleMotionPhaseKind.FeedIn or CycleMotionPhaseKind.FeedRetract;

    private readonly record struct CutterPose(Vector3 Tip, Vector3 Axis);

    public Alpha4IpwTestReport ResetAlpha4Test2()
    {
        ResetAlpha4IpwKernel();
        var triangles = UpdateAlpha4IpwVisual();
        KeepOriginalStockHiddenDuringAlpha4Test();
        return CreateAlpha4Report("IPW SIFIRLANDI", new TripleDexelVolume(), 0, true, triangles);
    }

    /// <summary>
    /// Resets only the authoritative interval stock. It deliberately does not
    /// touch any WPF visual and is therefore safe for the single background
    /// IPW worker.
    /// </summary>
    public Alpha4IpwTestReport ResetAlpha4IpwKernel()
    {
        if (_alpha4Ipw is null)
            throw new InvalidOperationException("Önce 'TESTİ BAŞLAT' düğmesine basın.");
        lock (_alpha4StockGate)
            _alpha4Ipw.Reset();
        Interlocked.Increment(ref _alpha4StockRevision);
        _hasCollisionScan = false;
        _alpha4CurrentVolume = _alpha4InitialVolume;
        _alpha4CutCount = 0;
        _alpha4OperationTag = 0;
        _alpha4LastComparison = new ZDexelTargetComparison(0, 0, 0);
        return CreateAlpha4Report(
            "IPW ÇEKİRDEĞİ SIFIRLANDI",
            new TripleDexelVolume(),
            0,
            true,
            _alpha4MeshTriangles,
            refreshMeasurements: false);
    }

    public Alpha4IpwTestReport RefreshAlpha4IpwVisual(string phase)
    {
        if (_alpha4Ipw is null)
            throw new InvalidOperationException("Önce Alpha4 NC IPW testini başlatın.");
        var triangles = UpdateAlpha4IpwVisual();
        KeepOriginalStockHiddenDuringAlpha4Test();
        return CreateAlpha4Report(phase, new TripleDexelVolume(), 0, true, triangles);
    }

    /// <summary>
    /// Publishes a bounded amount of pending stock geometry. This is used by
    /// the live UI so one large dirty region cannot monopolize the dispatcher.
    /// Exact dexel subtraction has already been committed; only its visual
    /// cache is allowed to trail by a few small batches.
    /// </summary>
    public void DrainAlpha4IpwVisual(int maximumVisualChunks)
    {
        if (_alpha4Ipw is null || maximumVisualChunks <= 0) return;
        UpdateAlpha4IpwVisual(maximumVisualChunks);
        KeepOriginalStockHiddenDuringAlpha4Test();
    }

    public void EndAlpha4Test2()
    {
        if (_alpha4IpwNode is not null)
        {
            _alpha4IpwNode.Parent?.Visual.Children.Remove(_alpha4IpwNode.Visual);
            _nodes.Remove(_alpha4IpwNode.Name);
        }
        _alpha4IpwNode = null;
        _alpha4Ipw = null;
        _alpha4OperationTags.Clear();
        RefreshAlpha4ReferenceParts();
        Interlocked.Increment(ref _alpha4StockRevision);
        _collisionTopologyDirty = true;
        _hasCollisionScan = false;
        _alpha4IpwChunkTriangles.Clear();
        _alpha4StockMotionCache.Clear();
        _alpha4Target = null;
        _alpha4CutCount = 0;
        _alpha4OperationTag = 0;
        _alpha4LastComparison = new ZDexelTargetComparison(0, 0, 0);
        _alpha4MeshTriangles = 0;
        if (_stockNode is not null) SetNodeVisible(_stockNode, true);
        Alpha4VisualCleared?.Invoke();
    }

    private int UpdateAlpha4IpwVisual(int maximumVisualChunks = int.MaxValue)
    {
        if (_alpha4Ipw is null || _alpha4IpwNode is null) return 0;
        var prepared = PrepareAlpha4IpwVisualChunks(maximumVisualChunks);
        PublishAlpha4IpwVisualChunks(prepared);
        return _alpha4MeshTriangles;
    }

    /// <summary>
    /// Builds dirty regions directly from the authoritative 0.15 mm stock.
    /// The raw numeric buffers are dispatcher-independent and are uploaded to
    /// persistent DirectX chunk buffers only when published on the UI thread.
    /// </summary>
    public IReadOnlyList<PreparedIpwVisualChunk> PrepareAlpha4IpwVisualChunks(
        int maximumVisualChunks = int.MaxValue)
    {
        if (_alpha4Ipw is null || _alpha4IpwNode is null || maximumVisualChunks <= 0)
            return Array.Empty<PreparedIpwVisualChunk>();

        IReadOnlyList<IpwSurfaceChunk> chunks;
        lock (_alpha4StockGate)
            chunks = _alpha4Ipw.BuildDirtySurfaceChunks(_alpha4Target, maximumVisualChunks);
        return chunks.Select(chunk => new PreparedIpwVisualChunk(chunk)).ToArray();
    }

    /// <summary>
    /// Updates accounting and hands raw regional buffers to the DirectX scene.
    /// </summary>
    public void PublishAlpha4IpwVisualChunks(IReadOnlyList<PreparedIpwVisualChunk> chunks)
    {
        if (_alpha4Ipw is null || _alpha4IpwNode is null || chunks.Count == 0) return;
        foreach (var chunk in chunks)
        {
            if (_alpha4IpwChunkTriangles.Remove(chunk.Key, out var previousTriangles))
                _alpha4MeshTriangles -= previousTriangles;
            if (chunk.TriangleCount > 0)
            {
                _alpha4IpwChunkTriangles[chunk.Key] = chunk.TriangleCount;
                _alpha4MeshTriangles += chunk.TriangleCount;
            }
        }
        _alpha4IpwNode.Model = null;
        _alpha4IpwNode.LocalBounds = _alpha4Ipw.Bounds;
        _alpha4IpwNode.Visual.Content = null;
        _alpha4IpwNode.IsVisible = true;
        _alpha4MeshTriangles = Math.Max(0, _alpha4MeshTriangles);
        KeepOriginalStockHiddenDuringAlpha4Test();
        Alpha4VisualChunksPublished?.Invoke(chunks);
    }

    private Alpha4IpwTestReport CreateAlpha4Report(
        string phase,
        TripleDexelVolume removed,
        double expected,
        bool rapidPassed,
        int triangles,
        bool refreshMeasurements = true)
    {
        var stock = _alpha4Ipw ?? throw new InvalidOperationException("Alpha4 IPW etkin değil.");
        // Incremental verification reads only changed Z rays. NC reports must
        // describe this cut, including when a full scan was not requested.
        _alpha4LastComparison = _alpha4Target is null
            ? new ZDexelTargetComparison(0, 0, 0)
            : stock.CompareWithTargetIncremental(_alpha4Target);
        return new Alpha4IpwTestReport(
            phase,
            stock.Bounds,
            stock.XPitch,
            stock.YPitch,
            stock.ZPitch,
            stock.XSampleCount,
            stock.YSampleCount,
            stock.ZSampleCount,
            _alpha4InitialVolume,
            _alpha4CurrentVolume,
            removed,
            expected,
            rapidPassed,
            _alpha4CutCount,
            triangles,
            _alpha4LastComparison.MissingTargetVolume,
            _alpha4LastComparison.ExcessStockVolume);
    }

    private void SubtractFromCurrentVolume(TripleDexelVolume removed)
    {
        _alpha4CurrentVolume = new TripleDexelVolume(
            Math.Max(0, _alpha4CurrentVolume.X - removed.X),
            Math.Max(0, _alpha4CurrentVolume.Y - removed.Y),
            Math.Max(0, _alpha4CurrentVolume.Z - removed.Z));
    }

    private void KeepOriginalStockHiddenDuringAlpha4Test()
    {
        if (_alpha4Ipw is not null && _stockNode is not null) SetNodeVisible(_stockNode, false);
    }

    public void SetMachineOpacity(double opacity)
    {
        _machineOpacity = Math.Clamp(opacity, 0.08, 1);
        foreach (var node in _nodes.Values.Where(x => x.Group == "machine" && x.Model is GeometryModel3D))
            ApplyNodeMaterial(node, node.BaseOpacity * _machineOpacity);
    }

    public void SetStockOpacity(double opacity)
    {
        // Keep the NX interaction rule: opacity is selected before material
        // removal begins, so thousands of active GPU chunk materials are not
        // rewritten in the middle of a frame.
        // Match NX: choose stock opacity before starting material removal.
        if (_alpha4Ipw is not null) return;
        _stockOpacity = Math.Clamp(opacity, 0.10, 1.0);
        foreach (var node in _nodes.Values.Where(node => ReferenceEquals(node, _stockNode)))
        {
            node.BaseOpacity = _stockOpacity;
            ApplyNodeMaterial(node, _stockOpacity);
        }
    }

    public void ZoomExtents() => _viewport.ZoomExtents(300);

    public void ApplyAxes(AxisState state)
    {
        if (_machine is null) return;
        foreach (var component in _machine.Components)
        {
            if (!_nodes.TryGetValue(component.Name, out var node) || !_axisByComponent.TryGetValue(component.Name, out var axis)) continue;
            var address = char.ToUpperInvariant(axis.Name[0]);
            var transform = MachineCoordinateResolver.BuildAxisDeltaTransform(_machine, axis, state.Get(address));
            node.Visual.Transform = new MatrixTransform3D(ToMatrix3D(transform));
        }
    }

    public static Matrix3D BuildPlacement(CoordinateFrame sourceMount, CoordinateFrame tableFrame)
    {
        var matrix = CoordinateTransforms.BuildPlacement(sourceMount, tableFrame);
        return new Matrix3D(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    }

    private CoordinateFrame GetTableFrame(MachinePackage machine)
    {
        var bounds = _nodes.TryGetValue("C-AXIS", out var tableNode) ? tableNode.LocalBounds : null;
        return MachineCoordinateResolver.ResolveTableFrame(machine, bounds);
    }

    private void AddDiagnosticPivotVisuals(MachinePackage machine)
    {
        if (_machineRoot is null) return;

        var bAxis = machine.Axes.FirstOrDefault(x =>
            x.IsRotary && x.Name.StartsWith('B'));
        if (bAxis is not null)
        {
            var pivot = MachineCoordinateResolver.ResolveAxisPivot(machine, bAxis);
            AddPivotVisual(_machineRoot, pivot, bAxis.Vector, Colors.Red);
        }

        var cAxis = machine.Axes.FirstOrDefault(x =>
            x.IsRotary && x.Name.StartsWith('C'));
        if (cAxis is not null && _nodes.TryGetValue("B_AXIS", out var bNode))
        {
            var pivot = MachineCoordinateResolver.ResolveAxisPivot(machine, cAxis);
            AddPivotVisual(bNode.Visual, pivot, cAxis.Vector, Colors.DodgerBlue);
        }
    }

    private static void AddPivotVisual(ModelVisual3D parent, Vector3 pivot, Vector3 axis, Color color)
    {
        var center = new Point3D(pivot.X, pivot.Y, pivot.Z);
        parent.Children.Add(new SphereVisual3D
        {
            Center = center,
            Radius = 7,
            Fill = new SolidColorBrush(color)
        });

        if (axis.LengthSquared() <= 1e-12f) return;
        axis = Vector3.Normalize(axis) * 170;
        var line = new LinesVisual3D { Color = color, Thickness = 4 };
        line.Points.Add(new Point3D(pivot.X - axis.X, pivot.Y - axis.Y, pivot.Z - axis.Z));
        line.Points.Add(new Point3D(pivot.X + axis.X, pivot.Y + axis.Y, pivot.Z + axis.Z));
        parent.Children.Add(line);
    }

    private static (
        Model3D Model,
        Bounds3 Bounds,
        TriangleMeshData EdgeMesh,
        IReadOnlyList<ToolCollisionGeometry.Region> CollisionRegions) BuildParametricToolModel(JobTool tool)
    {
        var parts = ParametricToolMeshBuilder.BuildParts(tool);
        if (parts.Count == 0)
        {
            var fallback = ParametricToolMeshBuilder.Build(tool);
            return (
                WpfMeshFactory.Create(fallback, Color.FromRgb(211, 178, 43)),
                fallback.Bounds,
                fallback,
                ToolCollisionGeometry.FromNxAssemblyMesh(fallback, tool));
        }

        var group = new Model3DGroup();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var part in parts)
        {
            group.Children.Add(WpfMeshFactory.Create(part.Mesh, ColorForToolPart(part.Role)));
            min = Vector3.Min(min, part.Mesh.Bounds.Min);
            max = Vector3.Max(max, part.Mesh.Bounds.Max);
        }
        var edgeMesh = ParametricToolMeshBuilder.Build(tool);
        return (group, new Bounds3(min, max), edgeMesh, ToolCollisionGeometry.FromParametricParts(parts));
    }

    /// <summary>
    /// NX Tool.ExportPart supplies the authoritative cutter, shank and holder
    /// silhouette as one STL.  Keep that exact geometry, but divide its axial
    /// triangles into ShopDoc-style material bands so the active tool remains
    /// readable against the spindle and machine body.
    /// </summary>
    private static Model3D BuildNxToolAssemblyModel(TriangleMeshData mesh, JobTool tool)
    {
        var extent = Math.Max(0.001f, mesh.Bounds.Max.X - mesh.Bounds.Min.X);
        var fluteEnd = mesh.Bounds.Min.X + (float)Math.Clamp(
            tool.FluteLength > 0.001 ? tool.FluteLength : extent * 0.22,
            0.5,
            extent * 0.65);
        var holderStart = mesh.Bounds.Min.X + (float)Math.Clamp(
            tool.Length > 0.001 ? tool.Length : extent * 0.72,
            fluteEnd - mesh.Bounds.Min.X + 0.5,
            extent * 0.94);

        var group = new Model3DGroup();
        AddBand(float.NegativeInfinity, fluteEnd, "cutter");
        AddBand(fluteEnd, holderStart, "shank");
        AddBand(holderStart, float.PositiveInfinity, "holder_body");
        return group.Children.Count == 0
            ? WpfMeshFactory.Create(mesh, ColorForToolPart("shank"))
            : group;

        void AddBand(float lowerExclusive, float upperInclusive, string role)
        {
            var band = ExtractAxialBand(mesh, lowerExclusive, upperInclusive);
            if (band is not null) group.Children.Add(WpfMeshFactory.Create(band, ColorForToolPart(role)));
        }
    }

    private static TriangleMeshData? ExtractAxialBand(
        TriangleMeshData source,
        float lowerExclusive,
        float upperInclusive)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<int>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var triangle = 0; triangle + 2 < source.Indices.Length; triangle += 3)
        {
            var i0 = source.Indices[triangle];
            var i1 = source.Indices[triangle + 1];
            var i2 = source.Indices[triangle + 2];
            var centroidX = (PositionX(i0) + PositionX(i1) + PositionX(i2)) / 3f;
            if (centroidX <= lowerExclusive || centroidX > upperInclusive) continue;

            AppendVertex(i0);
            AppendVertex(i1);
            AppendVertex(i2);
        }

        return indices.Count == 0
            ? null
            : new TriangleMeshData
            {
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                Indices = indices.ToArray(),
                Bounds = new Bounds3(min, max)
            };

        float PositionX(int index) => source.Positions[index * 3];

        void AppendVertex(int sourceIndex)
        {
            var sourceOffset = sourceIndex * 3;
            var x = source.Positions[sourceOffset];
            var y = source.Positions[sourceOffset + 1];
            var z = source.Positions[sourceOffset + 2];
            positions.Add(x);
            positions.Add(y);
            positions.Add(z);
            normals.Add(source.Normals[sourceOffset]);
            normals.Add(source.Normals[sourceOffset + 1]);
            normals.Add(source.Normals[sourceOffset + 2]);
            indices.Add(indices.Count);
            var point = new Vector3(x, y, z);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    private static Color ColorForToolPart(string role) => role switch
    {
        "cutter" => Color.FromRgb(236, 193, 24),
        "shank" => Color.FromRgb(172, 181, 185),
        "holder_taper" => Color.FromRgb(183, 119, 82),
        "holder_body" => Color.FromRgb(166, 101, 66),
        "holder_flange" => Color.FromRgb(139, 79, 49),
        _ => Color.FromRgb(198, 205, 209)
    };

    private static Color Alpha4SurfaceColor(int tag)
    {
        if (tag < 0) return Color.FromRgb(225, 45, 45); // target gouge
        if (tag == 0) return Color.FromRgb(54, 188, 218); // untouched stock
        var palette = new[]
        {
            Color.FromRgb(255, 193, 7), Color.FromRgb(76, 175, 80),
            Color.FromRgb(156, 110, 205), Color.FromRgb(255, 112, 67),
            Color.FromRgb(38, 166, 154), Color.FromRgb(92, 107, 192),
            Color.FromRgb(205, 220, 57), Color.FromRgb(236, 64, 122)
        };
        return palette[(tag - 1) % palette.Length];
    }

    private IReadOnlyList<CollisionProxy> BuildCollisionProxies()
    {
        var result = new List<CollisionProxy>();
        foreach (var node in _nodes.Values)
        {
            if (node.LocalBounds is not Bounds3 bounds || node.Model is null || ReferenceEquals(node, _alpha4IpwNode))
                continue;

            if (node.Group.Equals("machine", StringComparison.OrdinalIgnoreCase))
            {
                // collision.json may contain an empty authoritative pair list
                // and only an auto-broadphase component inventory.  Treating
                // that inventory as an all-to-all collision matrix makes
                // doors/tool changers collide with a cutter on the opposite
                // side of a hollow enclosure.  In fallback mode only NC-axis
                // and spindle bodies belong to the machining collision graph.
                if (_machine is null || !MachineCollisionPolicy.ShouldMonitorMachineComponent(
                        node.Name,
                        node.CollisionRole,
                        _machine.CollidableComponents,
                        _machine.Axes))
                    continue;
                result.Add(new CollisionProxy(
                    "MACHINE:" + node.Name,
                    "Makine · " + node.DisplayName,
                    node,
                    bounds,
                    "machine",
                    IsDynamicCollisionNode(node)));
                continue;
            }

            if (node.Group.Equals("workpiece", StringComparison.OrdinalIgnoreCase))
            {
                var kind = CollisionWorkpieceKind(node.CollisionRole);
                if (kind is null) continue;
                // While IPW is active neither the initial blank mesh nor the
                // design/target mesh is the physical workpiece.  The live
                // interval stock already contains both the final-part material
                // and every uncut remainder.  Keeping either reference mesh in
                // the generic OBB graph double-counts the workpiece, reports
                // contacts in already removed material and adds a second broad
                // collision pass over a stale volume.
                if (_alpha4Ipw is not null && kind is "stock" or "part") continue;
                result.Add(new CollisionProxy(
                    node.Name,
                    node.DisplayName,
                    node,
                    bounds,
                    kind,
                    true));
            }
        }

        if (_toolRoot is not null && _activeToolDefinition is { } tool)
            AddToolCollisionProxies(result, _toolRoot, tool, _activeToolCollisionRegions);

        return result;
    }

    private static void AddToolCollisionProxies(
        ICollection<CollisionProxy> result,
        SceneNode node,
        JobTool tool,
        IReadOnlyList<ToolCollisionGeometry.Region> regions)
    {
        var toolKey = string.IsNullOrWhiteSpace(tool.Id) ? "ACTIVE" : tool.Id.Trim();
        foreach (var region in regions)
        {
            var label = region.Kind switch
            {
                "cutter" => "Kesici",
                "shank" => "Takım sapı",
                _ => "Takım tutucu"
            };
            result.Add(new CollisionProxy(
                $"TOOL:{toolKey}:{region.Kind}",
                $"{label} · {tool.Id}",
                node,
                region.Bounds,
                "tool-" + region.Kind,
                true));
        }
    }

    private static IReadOnlyList<Bounds3> BuildSpatialCollisionLeaves(TriangleMeshData mesh)
    {
        if (mesh.Indices.Length < 3) return Array.Empty<Bounds3>();
        var divisions = mesh.TriangleCount >= 100_000 ? 6 : mesh.TriangleCount >= 10_000 ? 5 : 4;
        var bucketCount = divisions * divisions * divisions;
        var minimums = Enumerable.Repeat(new Vector3(float.PositiveInfinity), bucketCount).ToArray();
        var maximums = Enumerable.Repeat(new Vector3(float.NegativeInfinity), bucketCount).ToArray();
        var used = new bool[bucketCount];
        var size = mesh.Bounds.Size;

        for (var offset = 0; offset + 2 < mesh.Indices.Length; offset += 3)
        {
            var first = Position(mesh.Indices[offset]);
            var second = Position(mesh.Indices[offset + 1]);
            var third = Position(mesh.Indices[offset + 2]);
            var centroid = (first + second + third) / 3f;
            var x = Cell(centroid.X, mesh.Bounds.Min.X, size.X);
            var y = Cell(centroid.Y, mesh.Bounds.Min.Y, size.Y);
            var z = Cell(centroid.Z, mesh.Bounds.Min.Z, size.Z);
            var bucket = x + divisions * (y + divisions * z);
            used[bucket] = true;
            minimums[bucket] = Vector3.Min(minimums[bucket], Vector3.Min(first, Vector3.Min(second, third)));
            maximums[bucket] = Vector3.Max(maximums[bucket], Vector3.Max(first, Vector3.Max(second, third)));
        }

        const float surfaceEpsilon = 0.0025f;
        var padding = new Vector3(surfaceEpsilon);
        return Enumerable.Range(0, bucketCount)
            .Where(index => used[index])
            .Select(index => new Bounds3(minimums[index] - padding, maximums[index] + padding))
            .ToArray();

        Vector3 Position(int index)
        {
            var start = index * 3;
            return new Vector3(mesh.Positions[start], mesh.Positions[start + 1], mesh.Positions[start + 2]);
        }

        int Cell(float value, float minimum, float extent)
        {
            if (extent <= 1e-9f) return 0;
            return Math.Clamp((int)((value - minimum) / extent * divisions), 0, divisions - 1);
        }
    }

    private bool EnsureCollisionTopology()
    {
        if (!_collisionTopologyDirty) return false;
        _collisionProxies.Clear();
        _collisionProxies.AddRange(BuildCollisionProxies());
        _collisionPairs.Clear();
        for (var firstIndex = 0; firstIndex < _collisionProxies.Count; firstIndex++)
        for (var secondIndex = firstIndex + 1; secondIndex < _collisionProxies.Count; secondIndex++)
        {
            var first = _collisionProxies[firstIndex];
            var second = _collisionProxies[secondIndex];
            if (!IsCollisionCandidate(first, second)) continue;
            _collisionPairs.Add(new CollisionPair(firstIndex, secondIndex, PairKey(first.Key, second.Key)));
        }
        _collisionTransformCache.Clear();
        _collisionTopologyDirty = false;
        _hasCollisionScan = false;
        return true;
    }

    private bool UpdateCollisionWorlds()
    {
        var changed = false;
        _collisionTransformCache.Clear();
        foreach (var proxy in _collisionProxies)
        {
            if (!proxy.IsDynamic && proxy.HasWorld) continue;
            if (!_collisionTransformCache.TryGetValue(proxy.Node, out var matrix))
            {
                matrix = ToMatrix4x4(WorldTransformMatrix(proxy.Node));
                _collisionTransformCache[proxy.Node] = matrix;
            }
            changed |= proxy.UpdateWorld(matrix);
        }
        return changed;
    }

    private static bool IsCollisionCandidate(CollisionProxy first, CollisionProxy second)
    {
        if (ReferenceEquals(first.Node, second.Node)) return false;
        if (!first.IsDynamic && !second.IsDynamic) return false;
        if (IsWorkpieceKind(first.Kind) && IsWorkpieceKind(second.Kind)) return false;
        if (first.Kind.StartsWith("tool-", StringComparison.Ordinal) &&
            second.Kind.StartsWith("tool-", StringComparison.Ordinal)) return false;
        // The initial blank and design-part meshes do not know which material
        // has already been removed.  They therefore cannot prove cutter,
        // shank or holder contact during playback.  When IPW is active those
        // reference meshes are replaced by the authoritative interval stock,
        // which is checked separately below.
        if (ToolStockCollisionPolicy.IsStaticReferencePair(first.Kind, second.Kind)) return false;

        var related = IsAncestor(first.Node, second.Node) || IsAncestor(second.Node, first.Node);
        if (!related) return true;
        // A mounted tool and a seated workpiece necessarily live below the
        // spindle/table branches. Machine-to-machine filtering stays at the
        // package's narrower direct-parent rule.
        if (first.Node.Group != "machine" || second.Node.Group != "machine") return false;
        return !ReferenceEquals(first.Node.Parent, second.Node) &&
               !ReferenceEquals(second.Node.Parent, first.Node);
    }

    private bool IsDynamicCollisionNode(SceneNode node)
    {
        SceneNode? current = node;
        while (current is not null)
        {
            if (_axisByComponent.ContainsKey(current.Name)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsAncestor(SceneNode possibleAncestor, SceneNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (ReferenceEquals(current, possibleAncestor)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsWorkpieceKind(string kind) => kind is "fixture" or "stock" or "part";

    private static string? CollisionWorkpieceKind(string role)
    {
        if (role.Contains("fixture", StringComparison.OrdinalIgnoreCase)) return "fixture";
        if (role.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
            role.Contains("blank", StringComparison.OrdinalIgnoreCase)) return "stock";
        if (role.Contains("part", StringComparison.OrdinalIgnoreCase) ||
            role.Contains("model", StringComparison.OrdinalIgnoreCase)) return "part";
        return null;
    }

    private static bool DetailedCollisionIntersects(
        CollisionProxy first,
        CollisionProxy second,
        double clearanceMm)
    {
        if (!first.RootBox.Intersects(second.RootBox, clearanceMm)) return false;
        // Leaf boxes can overlap across holes, curved sides or concave empty
        // space. They are never proof of contact between two mesh-backed bodies.
        // Query the BVH directly after the root broad phase; this also preserves
        // complete solid containment, which disjoint surface leaves would miss.
        if (first.MeshInstance is { } firstMesh && second.MeshInstance is { } secondMesh)
            return firstMesh.Intersects(secondMesh, clearanceMm);
        var firstShapes = first.WorldShapes;
        var secondShapes = second.WorldShapes;
        var halfClearance = (float)Math.Max(0, clearanceMm) * 0.5f;
        var padding = new Vector3(halfClearance);
        for (var firstIndex = 0; firstIndex < firstShapes.Length; firstIndex++)
        for (var secondIndex = 0; secondIndex < secondShapes.Length; secondIndex++)
        {
            var firstShape = firstShapes[firstIndex];
            var secondShape = secondShapes[secondIndex];
            var firstAabb = halfClearance <= 0
                ? firstShape.AxisAlignedBounds
                : new Bounds3(firstShape.AxisAlignedBounds.Min - padding, firstShape.AxisAlignedBounds.Max + padding);
            var secondAabb = halfClearance <= 0
                ? secondShape.AxisAlignedBounds
                : new Bounds3(secondShape.AxisAlignedBounds.Min - padding, secondShape.AxisAlignedBounds.Max + padding);
            if (!firstAabb.Intersects(secondAabb)) continue;
            if (firstShape.Box.Intersects(secondShape.Box, clearanceMm)) return true;
        }
        return false;
    }

    private int LiveIpwToolCandidateCount() =>
        _alpha4Ipw is null || _toolRoot is null
            ? 0
            : _activeToolCollisionRegions.Count(region => region.Kind is "shank" or "holder");

    /// <summary>
    /// Checks non-cutting tool regions against the current interval stock.
    /// The display mesh and the original blank are deliberately not involved.
    /// Each region is represented by its axial cylinder in tool-local space,
    /// transformed into stock-local coordinates, then tested only against
    /// occupied dexel spans.  This remains valid for indexed B/C poses and for
    /// any workpiece package using the same scene contract.
    /// </summary>
    private void AppendLiveIpwToolHits(
        double clearanceMm,
        ref List<CollisionHit>? hits)
    {
        var stock = _alpha4Ipw;
        var toolNode = _toolRoot;
        var stockNode = _alpha4IpwNode;
        if (stock is null || toolNode is null || stockNode is null || _activeToolDefinition is null)
            return;

        var toolWorld = ToMatrix4x4(WorldTransformMatrix(toolNode));
        var stockWorld = ToMatrix4x4(WorldTransformMatrix(stockNode));
        if (!Matrix4x4.Invert(stockWorld, out var worldToStock)) return;
        var toolKey = string.IsNullOrWhiteSpace(_activeToolDefinition.Id)
            ? "ACTIVE"
            : _activeToolDefinition.Id.Trim();

        foreach (var kind in new[] { "shank", "holder" })
        {
            var actual = false;
            var withinClearance = false;
            var penetrationTolerance = ToolStockCollisionPolicy.MinimumLivePenetrationMm(
                kind,
                Alpha4StockPitchMm);
            foreach (var region in _activeToolCollisionRegions.Where(candidate => candidate.Kind == kind))
            {
                var centerY = (region.Bounds.Min.Y + region.Bounds.Max.Y) * 0.5f;
                var centerZ = (region.Bounds.Min.Z + region.Bounds.Max.Z) * 0.5f;
                var localStart = new Vector3(region.Bounds.Min.X, centerY, centerZ);
                var localEnd = new Vector3(region.Bounds.Max.X, centerY, centerZ);
                var localRadius = Math.Max(
                    (region.Bounds.Max.Y - region.Bounds.Min.Y) * 0.5f,
                    (region.Bounds.Max.Z - region.Bounds.Min.Z) * 0.5f);
                if (localRadius <= 1e-6f) continue;

                var start = ToStock(localStart);
                var end = ToStock(localEnd);
                var radial = ToStock(localStart + new Vector3(0, localRadius, 0));
                var axisVector = end - start;
                var length = axisVector.Length();
                var radius = Vector3.Distance(start, radial);
                if (length <= 1e-6f || radius <= 1e-6f) continue;
                var axis = axisVector / length;

                if (Penetrates(start, axis, radius, length, penetrationTolerance))
                {
                    actual = true;
                    withinClearance = true;
                    break;
                }
                if (!withinClearance)
                    withinClearance = Intersects(start, axis, radius, length, 0);
                if (!withinClearance && clearanceMm > 1e-9)
                    withinClearance = Intersects(start, axis, radius, length, clearanceMm);
            }
            if (!withinClearance) continue;

            var label = kind == "shank" ? "Takım sapı" : "Takım tutucu";
            var regionKey = $"TOOL:{toolKey}:{kind}";
            hits ??= new List<CollisionHit>(4);
            hits.Add(new CollisionHit(
                PairKey(regionKey, "LIVE-IPW"),
                "Blank / Stok (canlı IPW)",
                $"{label} · {_activeToolDefinition.Id}",
                !actual,
                clearanceMm));
            if (hits.Count >= 32) return;

            Vector3 ToStock(Vector3 local) =>
                Vector3.Transform(Vector3.Transform(local, toolWorld), worldToStock);

            bool Intersects(
                Vector3 cylinderStart,
                Vector3 cylinderAxis,
                double cylinderRadius,
                double cylinderLength,
                double margin)
            {
                var expandedStart = cylinderStart - (cylinderAxis * (float)margin);
                lock (_alpha4StockGate)
                    return stock.IntersectsFiniteCylinder(
                        expandedStart,
                        cylinderAxis,
                        cylinderRadius + margin,
                        cylinderLength + (2 * margin));
            }

            bool Penetrates(
                Vector3 cylinderStart,
                Vector3 cylinderAxis,
                double cylinderRadius,
                double cylinderLength,
                double minimumPenetration)
            {
                lock (_alpha4StockGate)
                    return stock.PenetratesFiniteCylinder(
                        cylinderStart,
                        cylinderAxis,
                        cylinderRadius,
                        cylinderLength,
                        minimumPenetration);
            }
        }
    }

    private static string PairKey(string first, string second) =>
        string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
            ? first + "|" + second
            : second + "|" + first;

    private SceneNode FindJobParent()
    {
        if (_machine is not null)
        {
            var mount = _machine.Junctions.FirstOrDefault(x =>
                x.Name.Equals("PART_MOUNT_JCT", StringComparison.OrdinalIgnoreCase));
            if (mount is not null && _nodes.TryGetValue(mount.Owner, out var owner)) return owner;
        }
        if (_nodes.TryGetValue("SETUP", out var setup)) return setup;
        if (_nodes.TryGetValue("C-AXIS", out var c)) return c;
        if (_machine is not null && _nodes.TryGetValue(_machine.RootComponent, out var root)) return root;
        throw new InvalidOperationException("İş parçasının bağlanacağı makine bileşeni bulunamadı.");
    }

    private static Bounds3 WorldBounds(SceneNode node)
    {
        var bounds = node.LocalBounds!.Value;
        var corners = new[]
        {
            new Point3D(bounds.Min.X,bounds.Min.Y,bounds.Min.Z), new Point3D(bounds.Max.X,bounds.Min.Y,bounds.Min.Z),
            new Point3D(bounds.Min.X,bounds.Max.Y,bounds.Min.Z), new Point3D(bounds.Max.X,bounds.Max.Y,bounds.Min.Z),
            new Point3D(bounds.Min.X,bounds.Min.Y,bounds.Max.Z), new Point3D(bounds.Max.X,bounds.Min.Y,bounds.Max.Z),
            new Point3D(bounds.Min.X,bounds.Max.Y,bounds.Max.Z), new Point3D(bounds.Max.X,bounds.Max.Y,bounds.Max.Z)
        };
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var corner in corners)
        {
            var point = corner;
            if (node.Model?.Transform is not null)
                point = node.Model.Transform.Transform(point);
            SceneNode? current = node;
            while (current is not null)
            {
                point = current.Visual.Transform.Transform(point);
                current = current.Parent;
            }
            var value = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
            min = Vector3.Min(min, value); max = Vector3.Max(max, value);
        }
        return new Bounds3(min, max);
    }

    private static Matrix3D WorldTransformMatrix(SceneNode node)
    {
        var result = Matrix3D.Identity;
        if (node.Model?.Transform is not null)
            result.Append(node.Model.Transform.Value);
        SceneNode? current = node;
        while (current is not null)
        {
            result.Append(current.Visual.Transform.Value);
            current = current.Parent;
        }
        return result;
    }

    private void RemoveJob()
    {
        EndAlpha4Test2();
        if (_jobRoot is null) return;
        _jobRoot.Parent?.Visual.Children.Remove(_jobRoot.Visual);
        foreach (var key in _nodes.Where(x => x.Value.Group != "machine").Select(x => x.Key).ToArray()) _nodes.Remove(key);
        TriangleCount = Math.Max(0, TriangleCount - _jobTriangleCount);
        _jobTriangleCount = 0;
        _jobRoot = null; _toolRoot = null; _stockNode = null; _job = null; JobMeshCount = 0;
        _toolOverrides.Clear();
        _baseWorkpiecePlacement = Matrix3D.Identity;
        LastWorkpiecePlacement = Matrix3D.Identity;
        WorkpieceCsysOffset = Vector3.Zero;
        _activeToolDefinition = null;
        _collisionBaselinePairs.Clear();
        _collisionTopologyDirty = true;
        _collisionProxies.Clear();
        _collisionPairs.Clear();
        _collisionTransformCache.Clear();
        _hasCollisionScan = false;
        _toolGaugeLengths.Clear();
    }

    private void Clear()
    {
        EndAlpha4Test2();
        if (_machineRoot is not null) _viewport.Children.Remove(_machineRoot);
        _nodes.Clear(); _axisByComponent.Clear();
        _machineRoot = null; _jobRoot = null; _toolRoot = null; _stockNode = null; _machine = null; _job = null;
        _toolOverrides.Clear();
        _baseWorkpiecePlacement = Matrix3D.Identity;
        LastWorkpiecePlacement = Matrix3D.Identity;
        WorkpieceCsysOffset = Vector3.Zero;
        _activeToolDefinition = null;
        _collisionBaselinePairs.Clear();
        _collisionTopologyDirty = true;
        _collisionProxies.Clear();
        _collisionPairs.Clear();
        _collisionTransformCache.Clear();
        _hasCollisionScan = false;
        MachineMeshCount = 0; JobMeshCount = 0; TriangleCount = 0;
        _jobTriangleCount = 0;
        _machineOpacity = 1;
        _toolGaugeLengths.Clear();
    }

    private static AxisState InitialState(MachinePackage machine)
    {
        double Get(char name) => machine.Axes.FirstOrDefault(x =>
            x.Name.Length > 0 && char.ToUpperInvariant(x.Name[0]) == char.ToUpperInvariant(name))?.InitialPosition ?? 0;
        return new AxisState(Get('X'), Get('Y'), Get('Z'), Get('A'), Get('B'), Get('C'));
    }

    private static string NormalizeTool(string value) => value.Trim().TrimStart('T', 't').TrimStart('0');
    private static Matrix3D ToMatrix3D(Matrix4x4 matrix) => new(
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    private static Matrix4x4 ToMatrix4x4(Matrix3D matrix) => new(
        (float)matrix.M11, (float)matrix.M12, (float)matrix.M13, (float)matrix.M14,
        (float)matrix.M21, (float)matrix.M22, (float)matrix.M23, (float)matrix.M24,
        (float)matrix.M31, (float)matrix.M32, (float)matrix.M33, (float)matrix.M34,
        (float)matrix.OffsetX, (float)matrix.OffsetY, (float)matrix.OffsetZ, (float)matrix.M44);
    private void SetNodeVisible(SceneNode node, bool visible)
    {
        node.IsVisible = visible;
        // The design solid can coincide with a freshly cut IPW face. Keep its
        // selected reference edges, but draw the solid only when IPW is hidden.
        // This also leaves an overcut visible instead of filling it with CAD.
        var referenceOnly = _alpha4Ipw is not null && _alpha4IpwDisplayVisible &&
            node.Group == "workpiece" && CollisionWorkpieceKind(node.CollisionRole) == "part";
        node.Visual.Content = visible && !referenceOnly ? node.Model : null;
        foreach (var overlay in node.Overlays)
        {
            if (visible && !node.Visual.Children.Contains(overlay)) node.Visual.Children.Add(overlay);
            else if (!visible) node.Visual.Children.Remove(overlay);
        }
    }

    public void BeginAlpha4IpwVisualReplacement()
    {
        _alpha4IpwChunkTriangles.Clear();
        _alpha4MeshTriangles = 0;
    }

    public void SetAlpha4IpwDisplayVisible(bool visible)
    {
        _alpha4IpwDisplayVisible = visible;
        RefreshAlpha4ReferenceParts();
    }

    private void RefreshAlpha4ReferenceParts()
    {
        foreach (var node in _nodes.Values.Where(node => node.Group == "workpiece" &&
                     CollisionWorkpieceKind(node.CollisionRole) == "part"))
            SetNodeVisible(node, node.IsVisible);
    }

    private static void ApplyNodeMaterial(SceneNode node, double opacity)
    {
        if (node.Model is Model3DGroup group)
        {
            foreach (var child in GeometryChildren(group))
            {
                var color = WpfMeshFactory.GetDiffuseColor(child.Material, node.Color);
                var taggedMaterial = WpfMeshFactory.CreateMaterial(color, opacity);
                child.Material = taggedMaterial;
                child.BackMaterial = taggedMaterial;
            }
            return;
        }
        if (node.Model is GeometryModel3D geometry)
        {
            var material = WpfMeshFactory.CreateMaterial(node.Color, opacity);
            geometry.Material = material;
            geometry.BackMaterial = material;
        }

        static IEnumerable<GeometryModel3D> GeometryChildren(Model3D model)
        {
            if (model is GeometryModel3D geometry)
            {
                yield return geometry;
                yield break;
            }
            if (model is not Model3DGroup nested) yield break;
            foreach (var child in nested.Children)
            foreach (var descendant in GeometryChildren(child))
                yield return descendant;
        }
    }

    private static int GroupOrder(string group) => group switch { "workpiece" => 0, "tool" => 1, _ => 2 };
    private static string VisibilityLabel(SceneNode node) => node.Group switch
    {
        "workpiece" => "İŞ  •  " + node.DisplayName,
        "tool" => "TAKIM  •  " + node.DisplayName,
        _ => "MAKİNE  •  " + node.DisplayName
    };

    private static bool IsExterior(string name) =>
        name.Contains("DOOR", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ENCLOSURE", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuxiliarySimulationComponent(string name) =>
        name.Contains("ATC", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("GRIPPER", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("TOOL_MAGAZINE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MAGAZINE", StringComparison.OrdinalIgnoreCase);

    private static bool IsMachiningAreaComponent(string name) =>
        name.Equals("MACHINE_BASE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("B_AXIS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("C-AXIS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Z_AXIS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SPINDLE", StringComparison.OrdinalIgnoreCase);

    private void ZoomToWorkEnvelope()
    {
        var targets = _nodes.Values
            .Where(x => x.IsVisible && x.LocalBounds.HasValue && (x.Group == "workpiece" || x.Group == "tool"))
            .Select(WorldBounds)
            .ToArray();
        if (targets.Length == 0)
        {
            _viewport.ZoomExtents(300);
            return;
        }

        var min = targets[0].Min;
        var max = targets[0].Max;
        foreach (var bounds in targets.Skip(1))
        {
            min = Vector3.Min(min, bounds.Min);
            max = Vector3.Max(max, bounds.Max);
        }

        var size = max - min;
        var horizontalPad = MathF.Max(35, MathF.Max(size.X, size.Y) * 0.12f);
        var verticalPad = MathF.Max(45, size.Z * 0.10f);
        var rect = new Rect3D(
            min.X - horizontalPad,
            min.Y - horizontalPad,
            min.Z - verticalPad,
            size.X + (2 * horizontalPad),
            size.Y + (2 * horizontalPad),
            size.Z + (2 * verticalPad));
        _viewport.ZoomExtents(rect, 350);
    }

    private static string JobRoleLabel(string role, string title)
    {
        if (role.Contains("stock", StringComparison.OrdinalIgnoreCase) || role.Contains("blank", StringComparison.OrdinalIgnoreCase)) return "Blank / Stock";
        if (role.Contains("fixture", StringComparison.OrdinalIgnoreCase)) return "Fikstür";
        if (role.Contains("part", StringComparison.OrdinalIgnoreCase) || role.Contains("model", StringComparison.OrdinalIgnoreCase)) return "Parça / Model";
        return string.IsNullOrWhiteSpace(title) ? role : title;
    }

    private static bool IsStockRole(string role) =>
        role.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("blank", StringComparison.OrdinalIgnoreCase);

    private static Color ColorForJobRole(string role, string fallback)
    {
        if (role.Contains("stock", StringComparison.OrdinalIgnoreCase) || role.Contains("blank", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(135, 176, 187);
        if (role.Contains("fixture", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(63, 174, 82);
        if (role.Contains("part", StringComparison.OrdinalIgnoreCase) || role.Contains("model", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(183, 189, 192);
        return WpfMeshFactory.ParseColor(fallback, Colors.SlateGray);
    }

    private static double OpacityForJobRole(string role) =>
        role.Contains("stock", StringComparison.OrdinalIgnoreCase) || role.Contains("blank", StringComparison.OrdinalIgnoreCase) ? 0.2 : 1;
    private static Color ColorFor(string name)
    {
        if (name.Contains("ENCLOSURE", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(179, 184, 187);
        if (name.Contains("BASE", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(139, 147, 152);
        if (name.Contains("DOOR", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(151, 174, 183);
        if (name.Contains("X_AXIS", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(191, 196, 199);
        if (name.Contains("Y-AXIS", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(174, 181, 185);
        if (name.Contains("Z_AXIS", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(207, 211, 213);
        if (name.Contains("B_AXIS", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(158, 166, 171);
        if (name.Contains("C-AXIS", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(146, 155, 160);
        if (name.Contains("SPINDLE", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(220, 223, 224);
        if (name.Contains("GRIPPER", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(202, 128, 44);
        if (name.Contains("ATC", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(135, 144, 150);
        return Color.FromRgb(176, 183, 187);
    }
}
