using System.Numerics;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using TRMachinist.Core;

var failures = new List<string>();

if (args.Any(a => a.Equals("--operation-path-audit", StringComparison.OrdinalIgnoreCase)))
{
    var auditFiles = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var machinePath = auditFiles.First(x => x.EndsWith(".trmac", StringComparison.OrdinalIgnoreCase));
    var jobPath = auditFiles.First(x => x.EndsWith(".trjob", StringComparison.OrdinalIgnoreCase));
    var ncPath = auditFiles.First(x => new[] { ".nc", ".mpf", ".spf", ".tap", ".h" }
        .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase));
    AuditOperationPaths(machinePath, jobPath, ncPath);
    return 0;
}

// --dump=<line>[,<line>...] resolves any .trmac + .trjob + NC triple and prints
// the machine state, the active tool and the tool tip both in machine space and
// back in the work frame.  This is the tool for settling "the tool is in the
// wrong place" without arguing over screenshots.
var dumpArgument = args.FirstOrDefault(a => a.StartsWith("--dump", StringComparison.OrdinalIgnoreCase));
if (dumpArgument is not null)
{
    var wanted = dumpArgument.Split('=', 2).ElementAtOrDefault(1)?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x.Trim(), out var v) ? v : -1).Where(x => x > 0).ToHashSet() ?? new HashSet<int>();
    var dumpFiles = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var dumpMachinePath = dumpFiles.First(x => x.EndsWith(".trmac", StringComparison.OrdinalIgnoreCase));
    var dumpJobPath = dumpFiles.First(x => x.EndsWith(".trjob", StringComparison.OrdinalIgnoreCase));
    var dumpNcPath = dumpFiles.First(x => new[] { ".nc", ".mpf", ".spf", ".tap", ".h" }
        .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase));
    DumpContext(dumpMachinePath, dumpJobPath, dumpNcPath, wanted);
    return 0;
}

// --ipw-audit replays the complete accepted machine/job/NC triple without a
// window and reports exactly where each tool starts removing stock.  It uses
// the same coordinate and cylindrical-cutter contract as Alpha4 Test2, so it
// is a diagnostic for the current implementation rather than a second model.
if (args.Any(a => a.Equals("--ipw-audit", StringComparison.OrdinalIgnoreCase)))
{
    var auditFiles = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var auditMachinePath = auditFiles.First(x => x.EndsWith(".trmac", StringComparison.OrdinalIgnoreCase));
    var auditJobPath = auditFiles.First(x => x.EndsWith(".trjob", StringComparison.OrdinalIgnoreCase));
    var auditNcPath = auditFiles.First(x => new[] { ".nc", ".mpf", ".spf", ".tap", ".h" }
        .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase));
    var pitchArgument = args.FirstOrDefault(a => a.StartsWith("--ipw-pitch=", StringComparison.OrdinalIgnoreCase));
    var auditPitch = pitchArgument is not null &&
                     double.TryParse(pitchArgument.Split('=', 2)[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPitch)
        ? parsedPitch
        : 0.50;
    var auditSurface = args.Any(a => a.Equals("--ipw-surface", StringComparison.OrdinalIgnoreCase));
    var sourceLimitArgument = args.FirstOrDefault(a => a.StartsWith("--ipw-source-limit=", StringComparison.OrdinalIgnoreCase));
    var sourceLimit = sourceLimitArgument is not null &&
                      int.TryParse(sourceLimitArgument.Split('=', 2)[1], out var parsedSourceLimit)
        ? parsedSourceLimit
        : int.MaxValue;
    AuditCurrentAlpha4Ipw(auditMachinePath, auditJobPath, auditNcPath, auditPitch, auditSurface, sourceLimit);
    return 0;
}

Run("CSYS origin alignment", TestCoordinateOrigin);
Run("CSYS axis alignment", TestCoordinateAxes);
Run("CSYS-local user setup offset", TestCoordinateUserOffset);
Run("G90/G91 and axis limits", TestGCode);
Run("Fanuc inline comments, T preselection and H registers", FanucControllerTests.CommentsAndRegisters);
Run("Physical slide zero and spindle gauge coordinates", FanucControllerTests.SlidesAndGauge);
Run("Haas DWO versus TCPC and intermediate rotary feed", FanucControllerTests.DwoAndTcp);
Run("Fanuc G53, G28 and incremental work coordinates", FanucControllerTests.HomeAndIncremental);
Run("Fanuc G81-G86, G98/G99 and modal hole positions", FanucControllerTests.DrillingCycles);
Run("Fanuc R arcs", FanucControllerTests.RadiusArcs);
Run("Fanuc centre-only full circles and invalid arc stop", FanucControllerTests.CentreOnlyCirclesAndInvalidArcs);
Run("Fanuc Type A compensation entry follows contour", FanucControllerTests.CompensationEntry);
Run("Fanuc cancelled setup and mismatched length diagnostics", FanucControllerTests.CancelledSetupDiagnostics);
Run("Parametric tool faces point outward", FanucControllerTests.OutwardToolFaces);
Run("SINUMERIK variables, SUPA and tool preselection", TestSinumerikGCode);
Run("Lossless NC source-line display", TestLosslessSourceLines);
Run("TRJOB/NC operation boundary catalog", TestOperationBoundaryCatalog);
Run("Surface ray masks match dense boundary occupancy", SurfaceMaskTests.ExactBoundaryOccupancy);
Run("Tile-bounded surface search preserves both boundary edges", SurfaceMaskTests.TileBoundarySearch);
Run("Continuous tool noses stay within independent geometry", ProfileQualityTests.CurvedNoses);
Run("Inclined conical sweep matches continuous geometric envelope", ProfileQualityTests.InclinedSweep);
Run("Incremental target report equals complete scan after cuts and reset", ProfileQualityTests.IncrementalComparison);
Run("Target coloring preserves cavities and detects gouges on all faces", ProfileQualityTests.TargetClassification);
Run("Progressive drilling preserves exact phase samples and rapid stock", CycleStockTests.ProgressivePecks);
Run("NX operation preview is not a machine-motion trace", TestOperationPathPreview);
Run("Progressive path respects arc, cycle, dwell and reverse cursor", TestProgressivePath);
Run("Oriented cutter rejection preserves cavities and empty rays", TestOrientedCutterCavities);
Run("G54 work frame and tool gauge mapping", TestWorkFrameMapping);
Run("B/C rotating work-frame mapping", TestRotaryWorkFrameMapping);
Run("Generic B/C pivot and rigid attachment", TestRotaryPivotRigidity);
Run("Generic B/C CYCLE800 indexed B/C mapping", TestCycle800IndexedMapping);
Run("Tool holder-to-pocket placement", TestToolPocketPlacement);
Run("Collision OBB separation, rotation and safety clearance", TestCollisionObb);
Run("Mesh collision preserves concave empty space", MeshCollisionTests.ConcaveGap);
Run("Mesh collision uses Euclidean clearance and real surfaces", MeshCollisionTests.Clearance);
Run("Mesh collision detects full and disconnected containment", MeshCollisionTests.Containment);
Run("Mesh collision does not fill open surfaces", MeshCollisionTests.OpenSurfaces);
Run("Mesh collision transformed box oracle and cache invalidation", MeshCollisionTests.RigidTransforms);
Run("Mesh collision crossing, coplanar and degenerate facets", MeshCollisionTests.CrossingTriangles);
Run("Tool collision regions and cutter-boundary contact", TestToolCollisionRegions);
Run("Static stock authority and live IPW penetration", TestToolStockCollisionPolicy);
Run("Fallback collision matrix excludes service mechanisms", TestMachineCollisionPolicy);
Run("Clearance warns while only real collision stops", TestCollisionAlertPolicy);
Run("Simulation player", TestPlayer);
Run("IPW completed-block cursor canonicalization", TestSimulationCursorCanonicalization);
Run("Adaptive playback back-pressure and no catch-up", TestAdaptivePlaybackGovernor);
Run("Synchronized playback recovers after transient heavy cutting", TestSynchronizedPlaybackRecovery);
Run("Stock motion cache has bounded history and reset", TestStockMotionCache);
Run("Ordered dexel search preserves cavities, tangencies and dirty bounds", IntervalSearchTests.OrderedCavities);
Run("Indexed B/C positioning and DC shortest path", TestIndexedRotaryPositioning);
Run("Parametric NX cutter/shank/holder mesh", TestParametricToolMesh);
Run("Job-aware tool holder library", TestToolHolderLibrary);
Run("NX cutter profile families and fail-closed tools", TestToolCuttingProfiles);
Run("Machine-independent conical cutter geometry and stock", TestConicalCuttingProfile);
Run("TRJOB 0.2 parametric tool round-trip", TestParametricJobRoundTrip);
Run("SINUMERIK T=\"NAME\" tool call", TestNamedToolCall);
Run("G2/G3 arc and TURN= helix interpolation", TestArcAndHelixInterpolation);
Run("SINUMERIK G41/G42 cutter-radius compensation", TestCutterRadiusCompensation);
Run("Modal tool after seeking into the middle", TestModalToolAfterSeek);
Run("SINUMERIK MCALL modal drilling cycles", TestModalDrillingCycles);
Run("Alpha4 triple-dexel box volume", TestTripleDexelBoxVolume);
Run("Alpha4 live-stock collision query", TestLiveStockCollisionQuery);
Run("Alpha4 bounded live surface batches", TestBoundedSurfaceBatches);
Run("Alpha4 surface workspace isolation, seams and reset", TestSurfaceWorkspaceIsolation);
Run("Alpha4 flat-end-mill cut, G0 and reset", TestTripleDexelCutRapidAndReset);
Run("Alpha4 oriented NC cylinder and G0", TestTripleDexelOrientedNcCylinder);
Run("Alpha4 layered cutter single-pass parity", TestLayeredCutterSinglePassParity);
Run("Alpha4 layered polyline union parity", TestLayeredCutterPathParity);
Run("Alpha4 playback-speed invariant stock", TestPlaybackSpeedInvariantStock);
Run("Alpha4 arbitrary STL stock and regional 3D surface", TestArbitraryStlStockAndRegionalSurface);

// Option arguments such as --rawjob=<path> also end in .trjob, so the file
// arguments are filtered before anything is loaded from them.
var fileArguments = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
foreach (var path in fileArguments)
{
    if (path.EndsWith(".trmac", StringComparison.OrdinalIgnoreCase))
        Run("Real .trmac package", () => TestMachinePackage(path));
    else if (path.EndsWith(".shopdocv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".trjob", StringComparison.OrdinalIgnoreCase))
        Run("Real NX job package", () => TestJobPackage(path));
}

var realMachinePath = fileArguments.FirstOrDefault(path => path.EndsWith(".trmac", StringComparison.OrdinalIgnoreCase));
var realJobPath = fileArguments.FirstOrDefault(path => path.EndsWith(".shopdocv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".trjob", StringComparison.OrdinalIgnoreCase));
if (realMachinePath is not null && realJobPath is not null)
{
    Run("Real machineMountCsys to PART_MOUNT_JCT", () => TestRealPlacement(realMachinePath, realJobPath));
    Run("Real NX tool to spindle junction", () => TestRealToolMount(realMachinePath, realJobPath));
    Run("Real Test4 STL stock and regional surface", () => TestRealTest4StockSurface(realJobPath));
}
var realNcPath = fileArguments.FirstOrDefault(path => new[] { ".nc", ".mpf", ".spf", ".tap", ".h" }
    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
if (realMachinePath is not null && realNcPath is not null)
    Run("Real SINUMERIK NC parse", () => TestRealNc(realMachinePath, realNcPath));
var rawJobArgument = args.FirstOrDefault(a => a.StartsWith("--rawjob=", StringComparison.OrdinalIgnoreCase));
if (realMachinePath is not null && realJobPath is not null && rawJobArgument is not null)
    Run("nx-csys mount frame seating policy",
        () => TestSeatingPolicy(realMachinePath, realJobPath, rawJobArgument.Split('=', 2)[1].Trim('"')));

if (realMachinePath is not null && realJobPath is not null && realNcPath is not null)
{
    Run("Real U630 simulation-context NC parse", () => TestRealContextNc(realMachinePath, realJobPath, realNcPath));
    Run("Real R2 NC-driven triple-dexel removal", () => TestRealAlpha4MaterialRemoval(realMachinePath, realJobPath, realNcPath));
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} test");
    foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
    return 1;
}

Console.WriteLine("PASS: all smoke tests");
return 0;

void Run(string name, Action test)
{
    try { test(); Console.WriteLine("PASS  " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.Error.WriteLine("FAIL  " + name); }
}

static void TestTripleDexelBoxVolume()
{
    var bounds = new Bounds3(new Vector3(-25, -20, 0), new Vector3(25, 20, 20));
    var stock = new TripleDexelStock(bounds, 0.5);
    var expected = 50.0 * 40.0 * 20.0;
    var volume = stock.Volume();
    AssertRelative(volume.X, expected, 1e-10, "X bundle box volume");
    AssertRelative(volume.Y, expected, 1e-10, "Y bundle box volume");
    AssertRelative(volume.Z, expected, 1e-10, "Z bundle box volume");
    if (stock.XSampleCount != 101 || stock.YSampleCount != 81 || stock.ZSampleCount != 41)
        throw new Exception($"Unexpected dexel grid: {stock.XSampleCount}x{stock.YSampleCount}x{stock.ZSampleCount}.");
    var compacted = stock.BuildDirtySurfaceChunks();
    var compactedTriangles = compacted.Sum(chunk => chunk.TriangleCount);
    var uniformFaceTriangles = 2 * (((stock.XSampleCount - 1) * (stock.YSampleCount - 1)) +
                                    ((stock.XSampleCount - 1) * (stock.ZSampleCount - 1)) +
                                    ((stock.YSampleCount - 1) * (stock.ZSampleCount - 1)));
    if (compactedTriangles >= uniformFaceTriangles / 10)
        throw new Exception($"Coplanar render cache was not compacted: {compactedTriangles}/{uniformFaceTriangles} triangles.");
}

static void TestLiveStockCollisionQuery()
{
    var stock = new TripleDexelStock(
        new Bounds3(new Vector3(-10, -10, 0), new Vector3(10, 10, 20)),
        0.25);
    if (!stock.IntersectsFiniteCylinder(
            new Vector3(0, 0, 2), Vector3.UnitZ, radius: 2, length: 8))
        throw new Exception("A cylinder inside remaining stock was missed.");

    stock.ApplyFlatEndMillMove(
        new Vector3(0, 0, 0),
        new Vector3(0, 0, 0),
        radius: 3,
        fluteLength: 20,
        isRapid: false);
    if (stock.IntersectsFiniteCylinder(
            new Vector3(0, 0, 2), Vector3.UnitZ, radius: 2, length: 8))
        throw new Exception("Removed material was still reported as live stock.");
    if (!stock.IntersectsFiniteCylinder(
            new Vector3(5, 0, 2), Vector3.UnitZ, radius: 1, length: 8))
        throw new Exception("Remaining stock beside the removed pocket was missed.");
    if (stock.IntersectsFiniteCylinder(
            new Vector3(0, 0, 24), Vector3.UnitZ, radius: 5, length: 4))
        throw new Exception("A separated holder failed the stock bounds broad phase.");

    var tangent = new Vector3(11, 0, 2);
    if (!stock.IntersectsFiniteCylinder(tangent, Vector3.UnitZ, radius: 1, length: 8))
        throw new Exception("A nominal boundary touch was not available for warning classification.");
    if (stock.PenetratesFiniteCylinder(
            tangent, Vector3.UnitZ, radius: 1, length: 8, minimumPenetrationMm: 0.075))
        throw new Exception("A tangent shank touch was promoted to material penetration.");
    if (!stock.PenetratesFiniteCylinder(
            new Vector3(10.85f, 0, 2), Vector3.UnitZ, radius: 1, length: 8,
            minimumPenetrationMm: 0.075))
        throw new Exception("Measurable shank penetration into live stock was missed.");
}

static void TestToolStockCollisionPolicy()
{
    if (!ToolStockCollisionPolicy.IsStaticReferencePair("tool-shank", "stock") ||
        !ToolStockCollisionPolicy.IsStaticReferencePair("part", "tool-holder"))
        throw new Exception("Static workpiece/tool pairs were not recognized.");
    if (ToolStockCollisionPolicy.IsStaticReferencePair("tool-holder", "fixture") ||
        ToolStockCollisionPolicy.IsStaticReferencePair("machine", "stock"))
        throw new Exception("A physical fixture or machine pair was incorrectly suppressed.");

    AssertRelative(
        ToolStockCollisionPolicy.MinimumLivePenetrationMm("shank", 0.15),
        0.075,
        1e-10,
        "Shank live-stock penetration tolerance");
    AssertRelative(
        ToolStockCollisionPolicy.MinimumLivePenetrationMm("holder", 0.15),
        0.025,
        1e-10,
        "Holder live-stock penetration tolerance");
}

static void TestCollisionObb()
{
    var local = new Bounds3(new Vector3(-5, -2, -1), new Vector3(5, 2, 1));
    var first = OrientedBounds3.Transform(local, Matrix4x4.Identity);
    var separated = OrientedBounds3.Transform(local, Matrix4x4.CreateTranslation(10.6f, 0, 0));
    if (first.Intersects(separated)) throw new Exception("Separated boxes reported as a collision.");
    if (!first.Intersects(separated, 0.61)) throw new Exception("Safety-clearance contact was missed.");

    var rotatedTransform = Matrix4x4.CreateRotationZ((float)(Math.PI / 4));
    rotatedTransform.Translation = new Vector3(7.1f, 0, 0);
    var rotated = OrientedBounds3.Transform(local, rotatedTransform);
    if (!first.Intersects(rotated)) throw new Exception("Rotated collision was missed.");

    var farTransform = Matrix4x4.CreateRotationZ((float)(Math.PI / 3));
    farTransform.Translation = new Vector3(20, 0, 0);
    if (first.Intersects(OrientedBounds3.Transform(local, farTransform)))
        throw new Exception("Far rotated boxes reported as a collision.");

    for (var warmup = 0; warmup < 64; warmup++) _ = first.Intersects(rotated, 0.25);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var hitCount = 0;
    for (var iteration = 0; iteration < 20_000; iteration++)
        if (first.Intersects(rotated, 0.25)) hitCount++;
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    if (hitCount != 20_000) throw new Exception("Repeated SAT result changed.");
    if (allocated > 256) throw new Exception($"Collision SAT allocated {allocated} bytes in the hot loop.");
}

static void TestMachineCollisionPolicy()
{
    var collidable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "X_SLIDER", "B_SLIDER", "SPINDLE", "RIGHT_DOOR", "CHANGER_DOOR", "TOOLCHANGER", "AUXILIARY"
    };
    var axes = new[]
    {
        new AxisDefinition("X", "LinearNcAxis", "X_SLIDER", "MACHINE_ZERO", Vector3.UnitX, 0, true, -1, 1, 1),
        new AxisDefinition("B", "RotaryNcAxis", "B_SLIDER", "B_AXIS", Vector3.UnitY, 0, true, -5, 110, 1),
        new AxisDefinition("S", "Spindle", "SPINDLE", "S", Vector3.UnitX, 0, false, 0, 0, 1),
        new AxisDefinition("Q5", "Linear", "RIGHT_DOOR", "RIGHT_DOOR", Vector3.UnitY, 0, true, 0, 805, 1),
        new AxisDefinition("Q7", "Linear", "CHANGER_DOOR", "CHANGER_DOOR", Vector3.UnitZ, 0, true, 0, 430, 1),
        new AxisDefinition("TC_LINEAR", "Linear", "TOOLCHANGER", "MACHINE_ZERO", Vector3.UnitZ, 0, true, -100, 0, 1)
    };

    foreach (var component in new[] { "X_SLIDER", "B_SLIDER", "SPINDLE" })
        if (!MachineCollisionPolicy.ShouldMonitorMachineComponent(component, "kinematic", collidable, axes))
            throw new Exception($"Machining-chain body was excluded: {component}");

    foreach (var component in new[] { "RIGHT_DOOR", "CHANGER_DOOR", "TOOLCHANGER" })
        if (MachineCollisionPolicy.ShouldMonitorMachineComponent(component, "kinematic", collidable, axes))
            throw new Exception($"Service mechanism entered fallback collision matrix: {component}");

    if (MachineCollisionPolicy.ShouldMonitorMachineComponent("AUXILIARY", "auxiliary", collidable, axes))
        throw new Exception("Auxiliary body entered fallback collision matrix.");
}

static void TestCollisionAlertPolicy()
{
    if (CollisionAlertPolicy.ShouldStopPlayback(new[] { true, true }))
        throw new Exception("Clearance-only warnings must not stop playback.");
    if (!CollisionAlertPolicy.ShouldStopPlayback(new[] { true, false }))
        throw new Exception("A real collision must stop playback even when warnings are also present.");
    if (CollisionAlertPolicy.ShouldStopPlayback(Array.Empty<bool>()))
        throw new Exception("An empty collision scan must not stop playback.");
}

static void TestToolCollisionRegions()
{
    var tool = new JobTool(
        "T01", "D50-Z5-L11-I22-MILL", "Mill", 50, 0.4, 13, "SHELL", null,
        "Mill5", 10, 50, 0, 0, 0, "SHELL", "nx-parametric-tool-builder",
        new[] { new ToolProfileSection(50, 3, 0, 0) },
        new[] { new ToolProfileSection(63.55, 20, 0, 0) });
    var mesh = ParametricToolMeshBuilder.Build(tool);
    var regions = ToolCollisionGeometry.FromNxAssemblyMesh(mesh, tool);
    var cutter = Merge(regions.Where(region => region.Kind == "cutter"));
    var shankRegions = regions.Where(region => region.Kind == "shank").ToArray();
    var holderRegions = regions.Where(region => region.Kind == "holder").ToArray();
    var shank = Merge(shankRegions);
    var holder = Merge(holderRegions);

    AssertRelative(cutter.Size.Y, 50, 1e-5, "Cutter collision diameter");
    AssertRelative(shank.Size.Y, 50, 1e-5, "Shank collision diameter");
    AssertRelative(holder.Size.Y, 63.55, 1e-5, "Holder collision diameter");
    if (holderRegions.Length < 2 || holderRegions.Any(region => region.Bounds.Size.X > 4.01f))
        throw new Exception("Holder collision envelope was not split along its real axial profile.");
    if (shank.Min.X < 10 + ToolCollisionGeometry.AxialContactToleranceMm - 1e-5)
        throw new Exception("Non-cutting shank still includes the closed cutter boundary.");

    var shankBox = OrientedBounds3.Transform(shank, Matrix4x4.Identity);
    var touchingStock = OrientedBounds3.Transform(
        new Bounds3(new Vector3(-100, -100, -100), new Vector3(10, 100, 100)),
        Matrix4x4.Identity);
    if (shankBox.Intersects(touchingStock))
        throw new Exception("A flute-end boundary touch was reported as material penetration.");
    if (!shankBox.Intersects(touchingStock, 0.25))
        throw new Exception("The configured safety clearance no longer detects the flute-end approach.");

    var penetratingStock = OrientedBounds3.Transform(
        new Bounds3(new Vector3(-100, -100, -100), new Vector3(10.05f, 100, 100)),
        Matrix4x4.Identity);
    if (!shankBox.Intersects(penetratingStock))
        throw new Exception("A real shank penetration was missed.");

    static Bounds3 Merge(IEnumerable<ToolCollisionGeometry.Region> selected)
    {
        var bounds = selected.Select(region => region.Bounds).ToArray();
        if (bounds.Length == 0) throw new Exception("Expected collision region is missing.");
        var minimum = bounds[0].Min;
        var maximum = bounds[0].Max;
        for (var index = 1; index < bounds.Length; index++)
        {
            minimum = Vector3.Min(minimum, bounds[index].Min);
            maximum = Vector3.Max(maximum, bounds[index].Max);
        }
        return new Bounds3(minimum, maximum);
    }
}

static void TestOperationBoundaryCatalog()
{
    var zero = new AxisState();
    var x1 = zero with { X = 1 };
    var x2 = zero with { X = 2 };
    var x3 = zero with { X = 3 };
    var blocks = new[]
    {
        new GCodeBlock(1, "MSG(\"FACE_MILLING , Tool : T01\")", MotionKind.None, zero, zero, 0, 0, 1, null, null),
        new GCodeBlock(2, "G0 X1", MotionKind.Rapid, zero, x1, 0, 0, 1, null, null),
        new GCodeBlock(3, "G1 X2", MotionKind.Linear, x1, x2, 500, 0, 1, null, null),
        new GCodeBlock(4, "MSG(\"CHAMFER_MILL , Tool : T02\")", MotionKind.None, x2, x2, 500, 0, 2, null, null),
        new GCodeBlock(5, "G0 X3", MotionKind.Rapid, x2, x3, 500, 0, 2, null, null),
        new GCodeBlock(6, "G1 X2", MotionKind.Linear, x3, x2, 300, 0, 2, null, null)
    };
    var program = new GCodeProgram
    {
        SourcePath = "operation-test.mpf",
        Blocks = blocks,
        SourceLines = Array.Empty<GCodeSourceLine>()
    };
    var metadata = new[]
    {
        new JobOperation("OP01", 1, "FACE_MILLING", "PROGRAM", "Planar Milling", "T01", 500, 8000, null),
        new JobOperation("OP02", 2, "CHAMFER_MILL", "PROGRAM", "Chamfer Milling", "T02", 300, 6000, null)
    };

    var ranges = GCodeOperationCatalog.Build(program, metadata);
    if (ranges.Count != 2)
        throw new Exception($"Expected 2 operation ranges, got {ranges.Count}.");
    if (ranges[0].Name != "FACE_MILLING" || ranges[0].StartBlockIndex != 0 || ranges[0].EndBlockIndex != 2)
        throw new Exception($"First operation boundary is wrong: {ranges[0]}.");
    if (ranges[1].Type != "Chamfer Milling" || ranges[1].StartBlockIndex != 3 || ranges[1].EndBlockIndex != 5)
        throw new Exception($"Second operation boundary is wrong: {ranges[1]}.");
    if (GCodeOperationCatalog.FindOperationIndex(ranges, 4) != 1)
        throw new Exception("Active block did not resolve to the second operation.");

    // Fanuc/Haas posts may use parenthesized operation comments, including
    // consecutive operations with the same tool. Ordinary comments are not
    // operation boundaries, and the tool suffix must not enter the name.
    var fanuc = new GCodeProgram { SourcePath = "operations.ptp", SourceLines = Array.Empty<GCodeSourceLine>(),
        Blocks = blocks.Select((block, index) => block with {
            Raw = index == 0 ? "(FACE_MILLING   TOOL : T01)" : index == 3 ? "(CHAMFER_MILL TOOL : T01)" : block.Raw,
            Tool = 1 }).ToArray() };
    var fanucRanges = GCodeOperationCatalog.Build(fanuc, metadata);
    if (fanucRanges.Count != 2 || fanucRanges[0].Name != "FACE_MILLING" ||
        fanucRanges[1].Name != "CHAMFER_MILL" || fanucRanges[1].StartBlockIndex != 3)
        throw new Exception("Same-tool Fanuc operations lost their independent color/path boundaries.");
}

static void TestOperationPathPreview()
{
    var zero = new AxisState();
    var z600 = zero with { Z = 600 };
    var z14 = zero with { Z = 14 };
    var z10 = zero with { Z = 10 };
    var x10z10 = z10 with { X = 10 };
    var x10z0 = zero with { X = 10 };
    var blocks = new[]
    {
        new GCodeBlock(1, "MSG(\"POCKET , Tool : T01\")", MotionKind.None, zero, zero, 0, 0, 1, null, null),
        new GCodeBlock(2, "SUPA G0 Z600", MotionKind.Rapid, zero, z600, 0, 0, 1, null, null),
        new GCodeBlock(3, "G0 Z14", MotionKind.Rapid, z600, z14, 0, 0, 1, null, null),
        new GCodeBlock(4, "G0 Z10", MotionKind.Rapid, z14, z10, 0, 0, 1, null, null),
        new GCodeBlock(5, "G1 X10", MotionKind.Linear, z10, x10z10, 500, 0, 1, null, null),
        new GCodeBlock(6, "G1 Z0", MotionKind.Linear, x10z10, x10z0, 500, 0, 1, null, null),
        new GCodeBlock(7, "SUPA G0 Z600", MotionKind.Rapid, x10z0, z600, 0, 0, 1, null, null)
    };
    var program = new GCodeProgram
    {
        SourcePath = "operation-preview-test.mpf",
        Blocks = blocks,
        SourceLines = Array.Empty<GCodeSourceLine>()
    };
    var operation = new GCodeOperationRange("OP01", 1, "POCKET", "PROGRAM", "Pocket", "T01", 0, 6, 1, 7);
    static Vector3 LocalTip(AxisState state, double gauge) =>
        new((float)state.X, (float)state.Y, (float)(state.Z - gauge));

    var cuttingOnly = GCodeOperationPathBuilder.Build(
        program, new Dictionary<int, double>(), new[] { operation }, LocalTip);
    if (cuttingOnly.CuttingSegmentCount != 2 || cuttingOnly.LinkingSegmentCount != 0)
        throw new Exception(
            $"Cut-only preview contained trace motion: cut={cuttingOnly.CuttingSegmentCount}, link={cuttingOnly.LinkingSegmentCount}.");

    var withLinks = GCodeOperationPathBuilder.Build(
        program, new Dictionary<int, double>(), new[] { operation }, LocalTip, includeLocalLinks: true);
    if (withLinks.CuttingSegmentCount != 2 || withLinks.LinkingSegmentCount != 1)
        throw new Exception(
            $"Reference moves were not filtered from operation links: cut={withLinks.CuttingSegmentCount}, link={withLinks.LinkingSegmentCount}.");
}

static void AuditOperationPaths(string machinePath, string jobPath, string ncPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var placement = CoordinateTransforms.BuildPlacement(
        job.MachineMount,
        MachineCoordinateResolver.ResolveTableFrame(machine));
    if (!Matrix4x4.Invert(placement, out var inversePlacement))
        throw new Exception("Operation audit placement is not invertible.");

    CoordinateFrame Place(CoordinateFrame frame, string source) => new(
        frame.Label,
        Vector3.Transform(frame.Origin, placement),
        Vector3.Normalize(Vector3.TransformNormal(frame.XAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.YAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.ZAxis, placement)),
        source);
    var workFrame = Place(job.Mcs, "operation-audit:mcs");
    var controllerFrame = Place(
        job.HasExplicitControllerWorkFrame ? job.ControllerWorkFrame : job.Mcs,
        "operation-audit:controller");
    var gauges = new Dictionary<int, double>();
    var toolNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        gauges[number] = Vector3.Distance(
            tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0),
            tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0));
        toolNumbers[tool.Id] = number;
        if (!string.IsNullOrWhiteSpace(tool.Name)) toolNumbers[tool.Name] = number;
    }
    var context = new GCodeSimulationContext(
        workFrame,
        gauges,
        controllerFrame,
        CoordinateTransforms.BuildPlacement(controllerFrame, workFrame),
        toolNumbers);
    var program = GCodeParser.Load(ncPath, machine, context);
    var operations = GCodeOperationCatalog.Build(program, job.Operations);
    var totalCutting = 0;
    var totalLinks = 0;
    foreach (var operation in operations)
    {
        var preview = GCodeOperationPathBuilder.Build(
            program,
            gauges,
            new[] { operation },
            (state, gauge) =>
            {
                var tip = new Vector3((float)state.X, (float)state.Y, (float)(state.Z - gauge));
                var neutral = MachineCoordinateResolver.InverseTransformWorkpiecePoint(
                    machine, tip, state.B, state.C);
                return Vector3.Transform(neutral, inversePlacement);
            },
            includeLocalLinks: true);
        totalCutting += preview.CuttingSegmentCount;
        totalLinks += preview.LinkingSegmentCount;
        Console.WriteLine(
            $"{operation.Number,3} {operation.Name,-30} cut={preview.CuttingSegmentCount,6} local-link={preview.LinkingSegmentCount,4}");
    }
    Console.WriteLine(
        $"operations={operations.Count}; cutting={totalCutting}; local-links={totalLinks}; machine reference moves excluded");
}

static void TestAdaptivePlaybackGovernor()
{
    var governor = new AdaptivePlaybackGovernor();
    governor.Reset(requestedSpeed: 25, stockSimulationActive: true);
    AssertRelative(governor.EffectiveSpeed(25), 25, 1e-12, "Initial measured stock speed");

    var capped = governor.CreateAdvanceStep(TimeSpan.FromMilliseconds(500));
    if (capped.TotalMilliseconds > 24.01)
        throw new Exception($"Blocked wall time was paid back as a jump: {capped.TotalMilliseconds:0.###} ms.");

    var beforePressure = governor.LoadScale;
    governor.ObserveWork(TimeSpan.FromMilliseconds(90));
    if (governor.LoadScale >= beforePressure)
        throw new Exception("An overloaded stock frame did not lower the effective playback rate.");

    var afterPressure = governor.LoadScale;
    for (var i = 0; i < 80; i++) governor.ObserveWork(TimeSpan.FromMilliseconds(5));
    if (governor.LoadScale <= afterPressure || governor.LoadScale > 1)
        throw new Exception("The governor did not recover conservatively after sustained light frames.");

    governor.Reset(requestedSpeed: 25, stockSimulationActive: false);
    var beforeBacklog = governor.LoadScale;
    governor.ObserveStockBacklog(exactBlockLag: 4, pendingDisplayCuts: 32);
    if (governor.LoadScale >= beforeBacklog)
        throw new Exception("Stock pipeline backlog did not lower the NC clock smoothly.");

    governor.Reset(requestedSpeed: 10, stockSimulationActive: false);
    AssertRelative(governor.LoadScale, 1, 1e-12, "Non-stock playback scale");
}

static void TestStockMotionCache()
{
    var cache = new StockMotionCache();
    var state = new AxisState();
    GCodeBlock Block(int i) => new(i, "G1", MotionKind.Linear, state, state, 500, 0, 1, null, null);
    var segments = new StockMotionSegment[100];
    for (var i = 0; i < 20_000; i++) cache[Block(i)] = segments;
    if (cache.Count > 512 || cache.SegmentCount > 32_768 || cache.TryGetValue(Block(0), out _))
        throw new Exception("Completed motion history grew beyond its budget.");
    if (!cache.TryGetValue(Block(19_999), out var latest) || !ReferenceEquals(latest, segments))
        throw new Exception("Current block segmentation was not reused.");
    cache[Block(20_000)] = new StockMotionSegment[40_000];
    if (cache.Count != 1) throw new Exception("Oversize active block retained old history.");
    cache[Block(20_001)] = segments;
    if (cache.SegmentCount > 32_768) throw new Exception("Oversize completed block was retained.");
    cache.Clear();
    if (cache.Count != 0 || cache.SegmentCount != 0) throw new Exception("Reset retained old segmentation.");
}

static void TestSynchronizedPlaybackRecovery()
{
    var governor = new AdaptivePlaybackGovernor();
    governor.Reset(25, true);
    for (var i = 0; i < 12; i++)
        governor.ObserveSynchronizedWork(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(4));
    var heavy = governor.LoadScale;
    if (heavy >= 0.5) throw new Exception("Heavy asynchronous work did not bound the next step.");
    for (var i = 0; i < 60; i++)
        governor.ObserveSynchronizedWork(TimeSpan.FromMilliseconds(22), TimeSpan.FromMilliseconds(5));
    if (governor.LoadScale < 0.98) throw new Exception("Moderate cutting remained trapped at the old low speed.");
    governor.ObserveSynchronizedWork(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(80));
    if (governor.LoadScale >= 0.98) throw new Exception("UI overload did not reduce the next step.");
    if (governor.CreateAdvanceStep(TimeSpan.FromSeconds(2)).TotalMilliseconds > 24.01)
        throw new Exception("Waiting time accumulated into a catch-up jump.");
}

static void TestSimulationCursorCanonicalization()
{
    var displayedCompletedBlock = new SimulationCursor(41, 1).Normalize(100);
    var workerNextBlock = new SimulationCursor(42, 0).Normalize(100);
    if (displayedCompletedBlock != workerNextBlock)
        throw new Exception(
            $"Equivalent IPW positions differ: displayed={displayedCompletedBlock}, worker={workerNextBlock}.");
    if (SimulationCursor.Compare(displayedCompletedBlock, workerNextBlock) != 0)
        throw new Exception("Equivalent completed/next-block cursors would trigger a false rewind.");

    var actualRewind = new SimulationCursor(20, 0.5).Normalize(100);
    if (SimulationCursor.Compare(actualRewind, workerNextBlock) >= 0)
        throw new Exception("A real backward seek was not recognized as an IPW rewind.");

    var lastBlockComplete = new SimulationCursor(99, 1).Normalize(100);
    if (lastBlockComplete != new SimulationCursor(99, 1))
        throw new Exception("Program-end cursor was normalized past the available block range.");
}

static void TestBoundedSurfaceBatches()
{
    var stock = new TripleDexelStock(
        new Bounds3(Vector3.Zero, new Vector3(70, 70, 40)),
        1.0);
    var initialPending = stock.DirtySurfaceChunkCount;
    if (initialPending <= 1)
        throw new Exception($"Expected multiple initial surface chunks, got {initialPending}.");

    var first = stock.BuildDirtySurfaceChunks(maximumChunkCount: 1);
    if (first.Count != 1 || stock.DirtySurfaceChunkCount != initialPending - 1)
        throw new Exception("A bounded surface update did not preserve the remaining dirty chunks.");

    var emitted = first.Count;
    while (stock.DirtySurfaceChunkCount > 0)
        emitted += stock.BuildDirtySurfaceChunks(maximumChunkCount: 3).Count;
    if (emitted != initialPending)
        throw new Exception($"Bounded surface batches lost chunks: {emitted}/{initialPending}.");
}

static void TestSurfaceWorkspaceIsolation()
{
    // Different extents, signs, tools and tags reuse the same scratch pool.
    // Compare incremental seam updates with a complete rebuild, then verify
    // reset removes every cut. Concurrent stocks must never share samples.
    Parallel.For(0, 4, scenario =>
    {
        var origin = new Vector3(-3.17f * scenario, -2.13f, -0.71f);
        var stock = new TripleDexelStock(
            new Bounds3(origin, origin + new Vector3(12.65f, 10.03f, 7.9f)), 0.15);
        var current = stock.BuildDirtySurfaceChunks().ToDictionary(c => c.Key);
        var initial = Fingerprint(current.Values);
        for (var pass = 0; pass < 3; pass++)
        {
            var axis = pass == 1 ? Vector3.Normalize(new Vector3(0.2f, -0.3f, 1)) : Vector3.UnitZ;
            var tip = origin + new Vector3(2 + pass, 2.5f + pass, 3.2f);
            var layers = new[] { new CutterCylinderLayer(0, 1.7 + scenario * 0.2, 8) };
            if (pass == 2)
                stock.ApplyLayeredCutterPath(new[] { new LayeredCutterPose(tip, axis),
                    new LayeredCutterPose(tip + new Vector3(5.2f, 1.1f, 0), axis),
                    new LayeredCutterPose(tip + new Vector3(4.5f, 3.2f, 0), axis) },
                    layers, false, scenario * 10 + pass + 1);
            else
                stock.ApplyLayeredCutterMove(tip, tip + new Vector3(5.2f, 1.1f, 0), axis, axis,
                    layers, false, scenario * 10 + pass + 1);
            while (stock.DirtySurfaceChunkCount > 0)
            foreach (var chunk in stock.BuildDirtySurfaceChunks(maximumChunkCount: pass + 1))
                current[chunk.Key] = chunk;
            var before = stock.Volume();
            if (Fingerprint(current.Values) != Fingerprint(stock.BuildAllSurfaceChunks()))
                throw new Exception($"Incremental surface differs at a seam (scenario {scenario}, pass {pass}).");
            if (stock.Volume() != before)
                throw new Exception("Surface workspace changed authoritative stock intervals.");
        }
        stock.Reset();
        foreach (var chunk in stock.BuildDirtySurfaceChunks()) current[chunk.Key] = chunk;
        if (Fingerprint(current.Values) != initial)
            throw new Exception($"Pooled surface samples survived reset (scenario {scenario}).");
    });

    static string Fingerprint(IEnumerable<IpwSurfaceChunk> chunks)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks.Where(c => c.Meshes.Count > 0)
                     .OrderBy(c => c.Key.Z).ThenBy(c => c.Key.Y).ThenBy(c => c.Key.X))
        {
            hash.AppendData(BitConverter.GetBytes(chunk.Key.X));
            hash.AppendData(BitConverter.GetBytes(chunk.Key.Y));
            hash.AppendData(BitConverter.GetBytes(chunk.Key.Z));
            foreach (var tagged in chunk.Meshes)
            {
                hash.AppendData(BitConverter.GetBytes(tagged.Tag));
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tagged.Mesh.Positions.AsSpan()));
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tagged.Mesh.Normals.AsSpan()));
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tagged.Mesh.Indices.AsSpan()));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

static void TestTripleDexelCutRapidAndReset()
{
    var bounds = new Bounds3(new Vector3(-25, -20, 0), new Vector3(25, 20, 20));
    var stock = new TripleDexelStock(bounds, 0.5);
    var initial = stock.Volume();
    // Put the cut boundary halfway between Z dexels.  This removes exactly
    // 4.75 mm under trapezoidal endpoint weights at a 0.5 mm pitch.
    var start = new Vector3(-10, 0, 15.25f);
    var end = new Vector3(10, 0, 15.25f);

    var rapid = stock.ApplyFlatEndMillMove(start, end, radius: 5, fluteLength: 20, isRapid: true);
    if (!rapid.WasRapid || !rapid.RemovedNothing || stock.Volume() != initial)
        throw new Exception("G0 changed the dexel stock.");

    var cut = stock.ApplyFlatEndMillMove(start, end, radius: 5, fluteLength: 20, isRapid: false, cutTag: 7);
    var expectedRemoved = ((2 * 5.0 * 20.0) + (Math.PI * 25.0)) * 4.75;
    AssertRelative(cut.Removed.X, expectedRemoved, 0.04, "X bundle removed volume");
    AssertRelative(cut.Removed.Y, expectedRemoved, 0.04, "Y bundle removed volume");
    AssertRelative(cut.Removed.Z, expectedRemoved, 0.04, "Z bundle removed volume");

    var repeated = stock.ApplyFlatEndMillMove(start, end, radius: 5, fluteLength: 20, isRapid: false, cutTag: 7);
    if (!repeated.RemovedNothing)
        throw new Exception($"Identical repeated cut removed material again: {repeated.Removed}.");

    var mesh = stock.BuildZHeightFieldMesh();
    if (mesh.TriangleCount <= 0 || mesh.Bounds != bounds)
        throw new Exception("Z dexel render mesh was not generated from the stock bounds.");
    var tagged = stock.BuildTaggedZHeightFieldMeshes();
    if (!tagged.Any(item => item.Tag == 7 && item.Mesh.TriangleCount > 0))
        throw new Exception("Newly exposed stock surface lost its operation color tag.");

    stock.Reset();
    var reset = stock.Volume();
    AssertRelative(reset.X, initial.X, 1e-10, "X bundle reset volume");
    AssertRelative(reset.Y, initial.Y, 1e-10, "Y bundle reset volume");
    AssertRelative(reset.Z, initial.Z, 1e-10, "Z bundle reset volume");
}

static void TestTripleDexelOrientedNcCylinder()
{
    var bounds = new Bounds3(new Vector3(-10), new Vector3(10));
    var stock = new TripleDexelStock(bounds, 0.25);
    var initial = stock.Volume();
    var tip = new Vector3(-4, 0, 0);

    var rapid = stock.ApplyCylindricalCutterMove(
        tip, tip, Vector3.UnitX, Vector3.UnitX,
        radius: 2, fluteLength: 8, isRapid: true);
    if (!rapid.WasRapid || !rapid.RemovedNothing || stock.Volume() != initial)
        throw new Exception("Oriented G0 cylinder changed the dexel stock.");

    var cut = stock.ApplyCylindricalCutterMove(
        tip, tip, Vector3.UnitX, Vector3.UnitX,
        radius: 2, fluteLength: 8, isRapid: false);
    var expected = Math.PI * 2 * 2 * 8;
    AssertRelative(cut.Removed.X, expected, 0.06, "Oriented cylinder X bundle");
    AssertRelative(cut.Removed.Y, expected, 0.06, "Oriented cylinder Y bundle");
    AssertRelative(cut.Removed.Z, expected, 0.06, "Oriented cylinder Z bundle");

    var repeated = stock.ApplyCylindricalCutterMove(
        tip, tip, Vector3.UnitX, Vector3.UnitX,
        radius: 2, fluteLength: 8, isRapid: false);
    if (!repeated.RemovedNothing)
        throw new Exception($"Repeated oriented cylinder removed stock twice: {repeated.Removed}.");
}

static void TestLayeredCutterSinglePassParity()
{
    var bounds = new Bounds3(new Vector3(-30, -25, -8), new Vector3(30, 25, 24));
    const double pitch = 0.25;
    var legacy = new TripleDexelStock(bounds, pitch);
    var combined = new TripleDexelStock(bounds, pitch);
    var start = new Vector3(-17, -6, -3);
    var end = new Vector3(18, 8, -3);
    var layers = new[]
    {
        new CutterCylinderLayer(0.00, 7.00, 20.00),
        new CutterCylinderLayer(0.25, 7.65, 19.75),
        new CutterCylinderLayer(0.50, 7.90, 19.50),
        new CutterCylinderLayer(0.75, 8.00, 19.25),
        new CutterCylinderLayer(1.00, 8.00, 19.00)
    };

    foreach (var layer in layers)
        legacy.ApplyCylindricalCutterMove(
            start + (Vector3.UnitZ * (float)layer.AxialOffset),
            end + (Vector3.UnitZ * (float)layer.AxialOffset),
            Vector3.UnitZ,
            Vector3.UnitZ,
            layer.Radius,
            layer.Length,
            isRapid: false,
            cutTag: 11);

    var result = combined.ApplyLayeredCutterMove(
        start,
        end,
        Vector3.UnitZ,
        Vector3.UnitZ,
        layers,
        isRapid: false,
        cutTag: 11);
    if (result.RemovedNothing)
        throw new Exception("Combined layered cutter removed no stock.");

    var expected = legacy.Volume();
    var actual = combined.Volume();
    AssertRelative(actual.X, expected.X, 1e-12, "Layered cutter X parity");
    AssertRelative(actual.Y, expected.Y, 1e-12, "Layered cutter Y parity");
    AssertRelative(actual.Z, expected.Z, 1e-12, "Layered cutter Z parity");

    // Indexed 5-axis pose: the optimized kernel must traverse each dexel field
    // once while preserving the exact union formerly produced layer by layer.
    var arbitraryLegacy = new TripleDexelStock(bounds, pitch);
    var arbitraryCombined = new TripleDexelStock(bounds, pitch);
    var arbitraryAxis = Vector3.Normalize(new Vector3(0.37f, -0.41f, 0.833f));
    var arbitraryTip = new Vector3(-4.25f, 3.75f, -5.5f);
    foreach (var layer in layers)
        arbitraryLegacy.ApplyCylindricalCutterMove(
            arbitraryTip + (arbitraryAxis * (float)layer.AxialOffset),
            arbitraryTip + (arbitraryAxis * (float)layer.AxialOffset),
            arbitraryAxis,
            arbitraryAxis,
            layer.Radius,
            layer.Length,
            isRapid: false,
            cutTag: 12);
    arbitraryCombined.ApplyLayeredCutterMove(
        arbitraryTip,
        arbitraryTip,
        arbitraryAxis,
        arbitraryAxis,
        layers,
        isRapid: false,
        cutTag: 12);
    var arbitraryExpected = arbitraryLegacy.Volume();
    var arbitraryActual = arbitraryCombined.Volume();
    AssertRelative(arbitraryActual.X, arbitraryExpected.X, 1e-10, "Arbitrary layered cutter X parity");
    AssertRelative(arbitraryActual.Y, arbitraryExpected.Y, 1e-10, "Arbitrary layered cutter Y parity");
    AssertRelative(arbitraryActual.Z, arbitraryExpected.Z, 1e-10, "Arbitrary layered cutter Z parity");
}

static void TestLayeredCutterPathParity()
{
    var bounds = new Bounds3(new Vector3(-35, -30, -8), new Vector3(35, 30, 24));
    var sequential = new TripleDexelStock(bounds, 0.25);
    var union = new TripleDexelStock(bounds, 0.25);
    var layers = new[]
    {
        new CutterCylinderLayer(0.00, 6.50, 18.00),
        new CutterCylinderLayer(0.50, 7.00, 17.50),
        new CutterCylinderLayer(1.00, 7.00, 17.00)
    };
    var points = Enumerable.Range(0, 13)
        .Select(index =>
        {
            var angle = (-125.0 + index * 20.0) * Math.PI / 180.0;
            return new Vector3(
                (float)(18.0 * Math.Cos(angle)),
                (float)(14.0 * Math.Sin(angle)),
                -3.0f);
        })
        .ToArray();
    for (var index = 1; index < points.Length; index++)
        sequential.ApplyLayeredCutterMove(
            points[index - 1], points[index],
            Vector3.UnitZ, Vector3.UnitZ,
            layers, false, 17,
            calculateRemainingVolume: false);

    union.ApplyLayeredCutterPath(
        points.Select(point => new LayeredCutterPose(point, Vector3.UnitZ)).ToArray(),
        layers,
        false,
        17,
        calculateRemainingVolume: false);
    var expected = sequential.Volume();
    var actual = union.Volume();
    AssertRelative(actual.X, expected.X, 1e-12, "Layered polyline X parity");
    AssertRelative(actual.Y, expected.Y, 1e-12, "Layered polyline Y parity");
    AssertRelative(actual.Z, expected.Z, 1e-12, "Layered polyline Z parity");
}

static void TestPlaybackSpeedInvariantStock()
{
    var bounds = new Bounds3(Vector3.Zero, new Vector3(40, 24, 12));
    var block = new GCodeBlock(
        1,
        "G1 X34 Y12 Z5 F1000",
        MotionKind.Linear,
        new AxisState(6, 12, 5, 0, 0, 0),
        new AxisState(34, 12, 5, 0, 0, 0),
        1000,
        0,
        1,
        null,
        null);
    var segments = DeterministicStockMotion.Build(
        block,
        state => new StockMotionPose(new Vector3((float)state.X, (float)state.Y, (float)state.Z), Vector3.UnitZ),
        0.75,
        6);
    var slow = new TripleDexelStock(bounds, 0.5);
    var fast = new TripleDexelStock(bounds, 0.5);
    ApplyPartition(slow, Enumerable.Range(0, 101).Select(index => index / 100.0).ToArray());
    ApplyPartition(fast, new[] { 0.0, 0.37, 0.91, 1.0 });
    var a = slow.Volume();
    var b = fast.Volume();
    if (Math.Abs(a.X - b.X) > 1e-8 || Math.Abs(a.Y - b.Y) > 1e-8 || Math.Abs(a.Z - b.Z) > 1e-8)
        throw new Exception($"Playback partition altered stock: slow={a}; fast={b}.");

    void ApplyPartition(TripleDexelStock stock, IReadOnlyList<double> progress)
    {
        for (var window = 1; window < progress.Count; window++)
        foreach (var segment in DeterministicStockMotion.SelectCompleted(
                     segments, progress[window - 1], progress[window]))
        {
            var start = new Vector3((float)segment.Start.X, (float)segment.Start.Y, (float)segment.Start.Z);
            var end = new Vector3((float)segment.End.X, (float)segment.End.Y, (float)segment.End.Z);
            stock.ApplyCylindricalCutterMove(
                start, end, Vector3.UnitZ, Vector3.UnitZ,
                radius: 2, fluteLength: 6, isRapid: false, cutTag: 1);
        }
    }
}

static void TestArbitraryStlStockAndRegionalSurface()
{
    const float radius = 20;
    const float height = 20;
    var mesh = CreateClosedCylinderMesh(radius, height, 64);
    var stock = new TripleDexelStock(mesh.Bounds, 0.5, mesh);
    var expected = Math.PI * radius * radius * height;
    var initial = stock.Volume();
    AssertRelative(initial.X, expected, 0.035, "STL cylinder X bundle volume");
    AssertRelative(initial.Y, expected, 0.035, "STL cylinder Y bundle volume");
    AssertRelative(initial.Z, expected, 0.035, "STL cylinder Z bundle volume");

    if (stock.SurfaceChunkCount <= 1 || stock.DirtySurfaceChunkCount != stock.SurfaceChunkCount)
        throw new Exception("Initial STL surface was not partitioned into independent dirty chunks.");
    var initialChunks = stock.BuildDirtySurfaceChunks();
    if (initialChunks.Count != stock.SurfaceChunkCount || initialChunks.Sum(chunk => chunk.TriangleCount) <= 0)
        throw new Exception("Full 3D surface was not generated from the arbitrary STL blank.");
    if (!initialChunks.SelectMany(chunk => chunk.Meshes).SelectMany(item => item.Mesh.Normals.Chunk(3))
            .Any(normal => Math.Abs(normal[2]) < 0.55f))
        throw new Exception("Arbitrary STL side surfaces collapsed into a Z-only height field.");
    if (stock.DirtySurfaceChunkCount != 0)
        throw new Exception("Rendered chunks remained dirty without another stock change.");

    var cut = stock.ApplyCylindricalCutterMove(
        new Vector3(-10, -10, 10), new Vector3(-10, -10, 10),
        Vector3.UnitZ, Vector3.UnitZ,
        radius: 2, fluteLength: 20, isRapid: false, cutTag: 9);
    if (cut.RemovedNothing)
        throw new Exception("Generic cutter did not remove material from the STL-derived stock.");
    if (stock.DirtySurfaceChunkCount <= 0 || stock.DirtySurfaceChunkCount >= stock.SurfaceChunkCount)
        throw new Exception($"A local cut dirtied {stock.DirtySurfaceChunkCount}/{stock.SurfaceChunkCount} chunks.");
    var changed = stock.BuildDirtySurfaceChunks();
    if (!changed.SelectMany(chunk => chunk.Meshes).Any(item => item.Tag == 9 && item.Mesh.TriangleCount > 0))
        throw new Exception("A newly exposed 3D cutter boundary lost its operation tag.");

    stock.Reset();
    var reset = stock.Volume();
    AssertRelative(reset.X, initial.X, 1e-10, "arbitrary STL reset X volume");
    AssertRelative(reset.Y, initial.Y, 1e-10, "arbitrary STL reset Y volume");
    AssertRelative(reset.Z, initial.Z, 1e-10, "arbitrary STL reset Z volume");
}

static TriangleMeshData CreateClosedCylinderMesh(float radius, float height, int sides)
{
    var positions = new List<float>();
    var normals = new List<float>();
    var indices = new List<int>();
    var bottomCenter = new Vector3(0, 0, 0);
    var topCenter = new Vector3(0, 0, height);
    for (var side = 0; side < sides; side++)
    {
        var a = (float)(side * Math.PI * 2 / sides);
        var b = (float)((side + 1) * Math.PI * 2 / sides);
        var p0 = new Vector3(radius * MathF.Cos(a), radius * MathF.Sin(a), 0);
        var p1 = new Vector3(radius * MathF.Cos(b), radius * MathF.Sin(b), 0);
        var p2 = new Vector3(p0.X, p0.Y, height);
        var p3 = new Vector3(p1.X, p1.Y, height);
        AddTriangle(p0, p1, p3);
        AddTriangle(p0, p3, p2);
        AddTriangle(bottomCenter, p1, p0);
        AddTriangle(topCenter, p2, p3);
    }

    return new TriangleMeshData
    {
        Positions = positions.ToArray(),
        Normals = normals.ToArray(),
        Indices = indices.ToArray(),
        Bounds = new Bounds3(new Vector3(-radius, -radius, 0), new Vector3(radius, radius, height))
    };

    void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        foreach (var point in new[] { a, b, c })
        {
            positions.Add(point.X); positions.Add(point.Y); positions.Add(point.Z);
            normals.Add(normal.X); normals.Add(normal.Y); normals.Add(normal.Z);
            indices.Add(indices.Count);
        }
    }
}

static void TestToolCuttingProfiles()
{
    JobTool Tool(string type, string subtype, double diameter, double corner, double tip = 0) => new(
        "T01", "AUDIT", type, diameter, corner, 75, "-", null, subtype,
        25, diameter, tip, 0, 0, "", "test",
        Array.Empty<ToolProfileSection>(), Array.Empty<ToolProfileSection>());

    var flat = ToolCuttingProfileFactory.Create(Tool("Mill", "Mill5", 12, 0));
    if (!flat.IsSupported || flat.Layers.Count != 1 || Math.Abs(flat.Layers[0].Radius - 6) > 1e-9)
        throw new Exception("Flat mill cutting profile is not one full-radius layer.");

    var bull = ToolCuttingProfileFactory.Create(Tool("Mill", "Mill5", 52, 2));
    if (!bull.IsSupported || bull.Layers.Count < 5 || bull.Layers[0].Radius >= 26)
        throw new Exception("Corner-radius mill was flattened into a full cylinder.");

    var ball = ToolCuttingProfileFactory.Create(Tool("Mill", "MillBall", 12, 6));
    if (!ball.IsSupported || ball.Layers.Count < 5 || ball.Layers[0].StartRadius != 0 ||
        ball.Layers[0].Radius >= 6)
        throw new Exception("Ball nose was flattened at its programmed tip.");

    var drill = ToolCuttingProfileFactory.Create(Tool("DrillStd", "DrillStdToolBuilder", 6.8, 0, 118));
    if (!drill.IsSupported || drill.Layers[0].StartRadius != 0 ||
        Math.Abs(drill.Layers[0].Length - 3.4 / Math.Tan(59 * Math.PI / 180)) > 1e-9)
        throw new Exception("Drill point did not preserve its specified cone angle.");

    var tap = ToolCuttingProfileFactory.Create(Tool("DrillTap", "DrillTapToolBuilder", 8, 0, 180));
    var threadMill = ToolCuttingProfileFactory.Create(Tool("DrillThreadMill", "DrillThreadMillToolBuilder", 12, 0, 180));
    if (tap.IsSupported || threadMill.IsSupported || tap.Layers.Count != 0 || threadMill.Layers.Count != 0)
        throw new Exception("Unverified thread tools must fail closed instead of gouging stock.");
}

static void TestConicalCuttingProfile()
{
    var tool = new JobTool("T12", "ANY_MACHINE_TOOL", "Mill", 12, 0, 75, "-", null,
        "ChamferTool", 25, 12, 0, 45, 0, "", "parametric",
        Array.Empty<ToolProfileSection>(), Array.Empty<ToolProfileSection>());
    foreach (var angle in new[] { 30.0, 45.0, 60.0 })
    foreach (var diameter in new[] { 6.0, 12.0, 20.0 })
    {
        var input = tool with { Diameter = diameter, TaperAngle = angle };
        var profile = ToolCuttingProfileFactory.Create(input);
        var slope = Math.Tan(angle * Math.PI / 180);
        if (!profile.IsSupported || profile.Layers[0].StartRadius != 0 ||
            Math.Abs(profile.Layers[0].Length - diameter * 0.5 / slope) > 1e-9)
            throw new Exception("Chamfer tool lost its package-derived cone.");
        // Names, IDs and manufacturer/machine labels are not cutting inputs.
        foreach (var name in new[] { "VENDOR_A", "VENDOR_B", "UNSEEN_MACHINE_812" })
        {
            var renamed = ToolCuttingProfileFactory.Create(input with { Name = name, Id = name, Holder = name });
            if (!renamed.Layers.SequenceEqual(profile.Layers))
                throw new Exception("Machine or metadata name changed cutter geometry.");
        }
        var mesh = ParametricToolMeshBuilder.BuildParts(input).Single(p => p.Role == "cutter").Mesh;
        for (var i = 0; i < mesh.Positions.Length; i += 3)
        {
            var axial = mesh.Positions[i];
            var radial = Math.Sqrt(mesh.Positions[i + 1] * mesh.Positions[i + 1] + mesh.Positions[i + 2] * mesh.Positions[i + 2]);
            if (radial > Math.Min(diameter * 0.5, axial * slope) + 1e-4)
                throw new Exception("Rendered cutter extends outside its cutting profile.");
        }
    }
    foreach (var invalid in new[] { double.NaN, -1.0, 0.0, 90.0 })
        if (ToolCuttingProfileFactory.Create(tool with { TaperAngle = invalid }).IsSupported)
            throw new Exception("Unknown chamfer geometry became a full cylinder.");

    var layers = ToolCuttingProfileFactory.Create(tool).Layers;
    var bounds = new Bounds3(new(-7.5f, -7.5f, -1.5f), new(7.5f, 7.5f, 7.5f));
    var stock = new TripleDexelStock(bounds, .15);
    stock.ApplyLayeredCutterMove(Vector3.Zero, Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ, layers, false, 2);
    if (!stock.IntersectsFiniteCylinder(new(4.5f, 0, .75f), Vector3.UnitZ, .1, .1))
        throw new Exception("Chamfer apex removed material at full cutter radius.");
    if (stock.IntersectsFiniteCylinder(new(0, 0, 3), Vector3.UnitZ, .1, .1))
        throw new Exception("The conical cutter did not cut its interior.");
    TripleDexelVolume? expected = null;
    foreach (var frames in new[] { 1, 7, 31 })
    {
        stock.Reset();
        for (var frame = 0; frame < frames; frame++)
            stock.ApplyLayeredCutterMoveProgress(new(-2, 0, 0), new(2, 0, 0),
                Vector3.UnitZ, Vector3.UnitZ, layers, frame / (double)frames,
                (frame + 1) / (double)frames, 2, false);
        var volume = stock.Volume();
        if (expected is { } reference && (Math.Abs(volume.X - reference.X) > 1e-8 ||
            Math.Abs(volume.Y - reference.Y) > 1e-8 || Math.Abs(volume.Z - reference.Z) > 1e-8))
            throw new Exception("Conical stock changed with playback frame count.");
        expected = volume;
    }
}

static void TestCutterRadiusCompensation()
{
    var machine = FakeMachine();
    var context = new GCodeSimulationContext(
        CoordinateFrame.Identity("G54"),
        new Dictionary<int, double> { [6] = 0 },
        ToolRadii: new Dictionary<int, double> { [6] = 5 });
    var program = GCodeParser.Parse("g41.mpf", new[]
    {
        "T6 M6",
        "G17 G90 G0 X50.272 Y8.437 Z10",
        "G1 Z-20",
        "G41 X47.366 Y26.183",
        "G3 X40 Y15.114 I4.634 J-11.069",
        "G2 X20 Y-4.886 I-20 J0",
        "G2 X0 Y15.114 I0 J20",
        "G2 X20 Y35.114 I20 J0",
        "G2 X40 Y15.114 I0 J-20",
        "G40"
    }, machine, context);

    var compensated = program.Blocks.First(block => block.Raw.Contains("X20 Y-4.886", StringComparison.OrdinalIgnoreCase));
    if (compensated.Path is not { Samples.Count: > 8 } path)
        throw new Exception("G41 boss-wall arc was not sampled.");
    foreach (var sample in path.Samples)
    {
        var radius = Math.Sqrt(Math.Pow(sample.X - 20, 2) + Math.Pow(sample.Y - 15.114, 2));
        if (Math.Abs(radius - 25) > 0.01)
            throw new Exception($"G41 clockwise-left path radius must be 25 mm, actual {radius:0.###}.");
    }
}

static void AssertRelative(double actual, double expected, double relativeTolerance, string label)
{
    var relative = Math.Abs(actual - expected) / Math.Max(Math.Abs(expected), 1e-12);
    if (relative > relativeTolerance)
        throw new Exception($"{label}: expected {expected:0.########}, got {actual:0.########} ({relative:P4}).");
}

static void TestCoordinateOrigin()
{
    var source = new CoordinateFrame("machineMountCsys", new Vector3(120, -44, 31), Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, "test");
    var target = new CoordinateFrame("PART_MOUNT_JCT", new Vector3(-12, 235.4f, -696.0236f), Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY, "test");
    var placement = CoordinateTransforms.BuildPlacement(source, target);
    var placedOrigin = CoordinateTransforms.TransformPoint(source.Origin, placement);
    AssertNear(placedOrigin, target.Origin, 0.0001f);
}

static void TestCoordinateAxes()
{
    var source = new CoordinateFrame("machineMountCsys", new Vector3(10, 20, 30), Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, "test");
    var target = new CoordinateFrame("PART_MOUNT_JCT", new Vector3(-80, 4, 77), Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX, "test");
    var placement = CoordinateTransforms.BuildPlacement(source, target);
    var origin = CoordinateTransforms.TransformPoint(source.Origin, placement);
    var xEnd = CoordinateTransforms.TransformPoint(source.Origin + source.XAxis, placement);
    var yEnd = CoordinateTransforms.TransformPoint(source.Origin + source.YAxis, placement);
    var zEnd = CoordinateTransforms.TransformPoint(source.Origin + source.ZAxis, placement);
    AssertNear(Vector3.Normalize(xEnd - origin), target.XAxis, 0.0001f);
    AssertNear(Vector3.Normalize(yEnd - origin), target.YAxis, 0.0001f);
    AssertNear(Vector3.Normalize(zEnd - origin), target.ZAxis, 0.0001f);
}

static void TestGCode()
{
    var machine = FakeMachine();
    var program = GCodeParser.Parse("memory.nc", new[] { "G90 SUPA G0 X10 Y20", "G91 SUPA G1 X5 B10 F500", "G90 SUPA G1 X500" }, machine);
    if (program.Blocks.Count != 3) throw new Exception("Block count mismatch.");
    if (Math.Abs(program.Blocks[1].End.X - 15) > 1e-9 || Math.Abs(program.Blocks[1].End.B - 10) > 1e-9)
        throw new Exception("Incremental coordinates were not applied.");
    if (program.ErrorCount != 1) throw new Exception("Axis limit violation was not reported.");
}

static void TestSinumerikGCode()
{
    var program = GCodeParser.Parse("sinumerik.mpf", new[]
    {
        "DEF REAL _X_HOME, _Z_HOME",
        "_X_HOME=-90.",
        "_Z_HOME=90.",
        "G90 SUPA G0 X=_X_HOME Z=_Z_HOME",
        "G54",
        "T2 M6",
        "T11",
        "G1 X-100. Z5.",
        "G2 X-80. Z0. C=DC(30.)"
    }, FakeMachine());
    if (program.ErrorCount != 0) throw new Exception("Valid SUPA/work coordinates produced a false limit error.");
    if (program.WarningCount != 0) throw new Exception("G2/G3 generated warning spam.");
    if (program.Blocks[^1].Tool != 2) throw new Exception("T preselection incorrectly replaced the active M6 tool.");
    if (Math.Abs(program.Blocks[^1].End.X - 20) > 1e-9 || Math.Abs(program.Blocks[^1].End.Z - -5) > 1e-9)
        throw new Exception("Work-coordinate anchoring or DC() parsing failed.");
}

static void TestLosslessSourceLines()
{
    var source = new[]
    {
        "; ***** TOOL LIST *****",
        "",
        "N10 CYCLE800(0,\"TABLE\",0,57,0,0,0,0,-45,0,0,0,0,1,0)",
        "MSG(\"FACE_MILLING , Tool : UGT0202_1006\")"
    };
    var program = GCodeParser.Parse("lossless.mpf", source, FakeMachine());
    if (program.SourceLines.Count != source.Length) throw new Exception("Comments or blank source lines were removed from the NC display.");
    for (var i = 0; i < source.Length; i++)
        if (!string.Equals(program.SourceLines[i].Raw, source[i], StringComparison.Ordinal))
            throw new Exception($"Source line {i + 1} was changed.");
    if (program.SourceLines[0].BlockIndex.HasValue || program.SourceLines[1].BlockIndex.HasValue)
        throw new Exception("Comment/blank lines were incorrectly marked executable.");
}

static void TestModalToolAfterSeek()
{
    // Starting from the middle must present that block's modal tool even though
    // the M6 that selected it was never executed by the player.
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", new Vector3(0, 0, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double> { [2] = 136.1, [11] = 114.8 },
        ToolNumbersByName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["UGT0202_1006"] = 2,
            ["END_MILL_10"] = 11
        });
    var program = GCodeParser.Parse(
        "seek.mpf",
        new[]
        {
            "N19 T=\"UGT0202_1006\"", "N20 M6", "N21 T=\"END_MILL_10\"", "N22 D1", "N23 G54",
            "N30 G0 X0 Y0 Z10", "N31 G1 X10 F200",
            "N40 T=\"END_MILL_10\"", "N41 M6", "N42 D1",
            "N50 G0 X20 Y20 Z10", "N51 G1 X30 F200"
        },
        FakeMachine(),
        context);

    var player = new SimulationPlayer();
    player.Load(program);
    var targetIndex = program.Blocks.ToList().FindLastIndex(x => x.Raw.Contains("X30", StringComparison.Ordinal));
    player.Seek(targetIndex);
    if (player.CurrentBlock?.Tool != 11)
        throw new Exception($"Seeking into the second operation must report T11, got {player.CurrentBlock?.Tool?.ToString() ?? "null"}.");

    var firstIndex = program.Blocks.ToList().FindLastIndex(x => x.Raw.Contains("X10", StringComparison.Ordinal));
    player.Seek(firstIndex);
    if (player.CurrentBlock?.Tool != 2)
        throw new Exception($"Seeking back into the first operation must report T2, got {player.CurrentBlock?.Tool?.ToString() ?? "null"}.");
}

static void TestModalDrillingCycles()
{
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", new Vector3(0, 0, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double> { [1] = 0 });

    GCodeProgram Parse(params string[] lines) =>
        GCodeParser.Parse("cycles.mpf", new[] { "T1 M6", "G54", "G17 G0 X0 Y0 Z10" }.Concat(lines), FakeMachine(), context);

    var cycle81 = Parse("MCALL CYCLE81(10,0,2,-8,)", "X5 Y6", "X15", "MCALL", "X25");
    var generated81 = cycle81.Blocks.Where(x => x.CycleMotion is not null).ToArray();
    if (generated81.Length != 2)
        throw new Exception($"MCALL activation/multiple-hole/cancellation mismatch: {generated81.Length} generated holes.");
    if (generated81.Any(x => x.CycleMotion!.CycleName != "CYCLE81" || x.Path is not { Samples.Count: 4 }))
        throw new Exception("CYCLE81 did not preserve position/approach/feed/retract phases.");
    var cycle81Path = generated81[0].Path!;
    var position81 = cycle81Path.Samples[0];
    var approach81 = cycle81Path.Samples[1];
    var depth81 = cycle81Path.Samples[2];
    if (Math.Abs(position81.X - 5) > 0.001 || Math.Abs(position81.Y - 6) > 0.001 || Math.Abs(position81.Z - 110) > 0.001)
        throw new Exception("Cycle plane positioning was combined with the tool-axis approach.");
    if (Math.Abs(approach81.X - 5) > 0.001 || Math.Abs(approach81.Y - 6) > 0.001 || Math.Abs(approach81.Z - 102) > 0.001 ||
        Math.Abs(depth81.X - 5) > 0.001 || Math.Abs(depth81.Y - 6) > 0.001 || Math.Abs(depth81.Z - 92) > 0.001 ||
        Math.Abs(generated81[0].End.X - 5) > 0.001 || Math.Abs(generated81[0].End.Y - 6) > 0.001 || Math.Abs(generated81[0].End.Z - 110) > 0.001)
        throw new Exception("CYCLE81 safety/depth/return-plane geometry failed.");
    var firstCycle81 = generated81[0].CycleMotion!;
    if (firstCycle81.SourceLine != generated81[0].SourceLine || firstCycle81.ActivationSourceLine != 4)
        throw new Exception("Generated cycle motion did not preserve trigger and MCALL source lines.");

    // Test-23-r2 compatibility gate: a generated cycle may animate above and
    // below the programmed block level, but it must finish on the same logical
    // endpoint Test-22-r2 would hand to the next block.  Otherwise a later
    // TRAORI activation inherits the drilling return plane and the accepted
    // simultaneous path shifts.
    var isolatedCycle = Parse("G0 Z5", "MCALL CYCLE81(10,0,2,-8,)", "X1 Y2")
        .Blocks.Single(x => x.CycleMotion is not null);
    if (Math.Abs(isolatedCycle.End.Z - 105) > 0.001 ||
        isolatedCycle.CycleMotion!.Phases[^1].Kind != CycleMotionPhaseKind.RapidReturnToProgrammedLevel)
        throw new Exception("Generated cycle leaked its return plane into the following modal/TRAORI state.");

    var profiles = new[]
    {
        (Name: "CYCLE82", Call: "MCALL CYCLE82(10,0,2,-8,,0.25,0,1,0)", Dwell: 0.25, FeedRetract: false),
        (Name: "CYCLE84", Call: "S400 MCALL CYCLE84(10,0,2,-8,,0.3,3,,1.25,,500,400,,1,,0,,,,,,0,1,1000)", Dwell: 0.3, FeedRetract: true),
        (Name: "CYCLE85", Call: "MCALL CYCLE85(10,0,2,-8,,0.4,250,200,,0,0)", Dwell: 0.4, FeedRetract: true),
        (Name: "CYCLE89", Call: "MCALL CYCLE89(10,0,2,-8,,0.5)", Dwell: 0.5, FeedRetract: false)
    };
    foreach (var profile in profiles)
    {
        var block = Parse(profile.Call, "X1 Y2").Blocks.Single(x => x.CycleMotion is not null);
        if (block.CycleMotion!.CycleName != profile.Name || Math.Abs(block.CycleMotion.DwellSeconds - profile.Dwell) > 1e-9)
            throw new Exception($"{profile.Name} dwell/profile mapping failed.");
        var retract = block.CycleMotion.Phases[^1];
        var expectedKind = profile.FeedRetract ? CycleMotionPhaseKind.FeedRetract : CycleMotionPhaseKind.RapidRetract;
        if (retract.Kind != expectedKind)
            throw new Exception($"{profile.Name} retract kind mismatch: {retract.Kind}.");
        if (profile.Name == "CYCLE84" &&
            block.CycleMotion.Phases.Where(x => x.Kind is CycleMotionPhaseKind.FeedIn or CycleMotionPhaseKind.FeedRetract)
                .Any(x => Math.Abs(x.Feed - 500) > 1e-9))
            throw new Exception("CYCLE84 did not derive synchronized feed from spindle speed * PIT.");
    }

    var cycle83 = Parse("MCALL CYCLE83(10,0,2,-8,,,3,0,0.1,,1,1,,0,,,,0,1,0)", "X1 Y2")
        .Blocks.Single(x => x.CycleMotion is not null);
    var feedInCount = cycle83.CycleMotion!.Phases.Count(x => x.Kind == CycleMotionPhaseKind.FeedIn);
    var peckRetractCount = cycle83.CycleMotion.Phases.Count(x => x.Kind == CycleMotionPhaseKind.PeckRetract);
    var peckDwellCount = cycle83.CycleMotion.Phases.Count(x => x.Kind == CycleMotionPhaseKind.Dwell);
    if (feedInCount != 3 || peckRetractCount != 2 || peckDwellCount != 3 ||
        Math.Abs(cycle83.CycleMotion.DwellSeconds - 0.3) > 1e-9)
        throw new Exception($"CYCLE83 peck profile mismatch: feed={feedInCount}, retract={peckRetractCount}.");

    var chipBreak83 = Parse("MCALL CYCLE83(10,0,2,-8,,,3,0,0,,1,0,,0,0.5,,,0,1,0)", "X1 Y2")
        .Blocks.Single(x => x.CycleMotion is not null);
    var firstFeed = chipBreak83.CycleMotion!.Phases.First(x => x.Kind == CycleMotionPhaseKind.FeedIn);
    var firstChipBreak = chipBreak83.CycleMotion.Phases.First(x => x.Kind == CycleMotionPhaseKind.PeckRetract);
    if (Math.Abs(firstChipBreak.End.Z - firstFeed.End.Z - 0.5) > 0.001)
        throw new Exception("CYCLE83 VARI=0 did not use the programmed VRT chip-break retract.");

    // Playback must follow phase time.  At 0.8 s this compact CYCLE82 is inside
    // its dwell; sample-count interpolation would already be retracting.
    var dwellProgram = Parse("F1000 MCALL CYCLE82(10,0,2,-8,,0.25,0,1,0)", "X1 Y2");
    var dwellIndex = dwellProgram.Blocks.ToList().FindIndex(x => x.CycleMotion is not null);
    var dwellPlayer = new SimulationPlayer();
    dwellPlayer.Load(dwellProgram);
    dwellPlayer.Seek(dwellIndex);
    dwellPlayer.Play();
    dwellPlayer.Advance(TimeSpan.FromSeconds(0.8));
    if (Math.Abs(dwellPlayer.Position.Z - 92) > 0.001)
        throw new Exception($"Cycle phase-timed playback skipped dwell: Z={dwellPlayer.Position.Z:0.###}.");

    var planes = new[]
    {
        (Plane: "G17", Position: "X4 Y5", Normal: 'Z'),
        (Plane: "G18", Position: "X4 Z5", Normal: 'Y'),
        (Plane: "G19", Position: "Y4 Z5", Normal: 'X')
    };
    foreach (var plane in planes)
    {
        var program = GCodeParser.Parse("plane-cycle.mpf",
            new[] { "T1 M6", "G54", $"{plane.Plane} G0 X0 Y0 Z0", "MCALL CYCLE81(0,0,2,-8,)", plane.Position },
            FakeMachine(), context);
        var depth = program.Blocks.Single(x => x.CycleMotion is not null).CycleMotion!.Phases
            .Single(x => x.Kind == CycleMotionPhaseKind.FeedIn).End.Get(plane.Normal);
        var expectedDepth = plane.Normal == 'Z' ? 92 : -8;
        if (Math.Abs(depth - expectedDepth) > 0.001)
            throw new Exception($"{plane.Plane} cycle normal mapping failed: {depth:0.###}.");
    }

    var traori = Parse("TRAORI", "MCALL CYCLE81(10,0,2,-8,)", "X1 Y2");
    var blocked = traori.Blocks.Single(x => x.Raw.Contains("X1 Y2", StringComparison.Ordinal));
    if (blocked.CycleMotion is not null || !(blocked.Warning?.Contains("TRAORI", StringComparison.OrdinalIgnoreCase) ?? false))
        throw new Exception("Modal cycle under TRAORI did not fail closed with a warning.");
}

static void TestArcAndHelixInterpolation()
{
    // A full circle in G17 with TURN=2 has to become three revolutions of real
    // path, not one straight chord.  Radius 10 -> 3 * 2 * pi * 10 = 188.496 mm.
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", new Vector3(0, 0, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double> { [1] = 0 });
    var helix = GCodeParser.Parse(
        "helix.mpf",
        new[] { "T1 M6", "G54", "G17 G0 X10 Y0 Z0", "G3 X10 Y0 Z-6 I-10 J0 TURN=2 F200" },
        FakeMachine(),
        context);
    var block = helix.Blocks[^1];
    if (block.Path is null) throw new Exception("TURN= helix produced no interpolated path.");
    if (block.Path.Revolutions != 3)
        throw new Exception($"TURN=2 must give 3 full revolutions, got {block.Path.Revolutions}.");
    var expected = 3 * 2 * Math.PI * 10;
    if (Math.Abs(block.Path.Length - expected) > 0.5)
        throw new Exception($"Helix path length {block.Path.Length:0.###} mm, expected about {expected:0.###} mm.");
    if (!block.HasMotion)
        throw new Exception("A helix that returns to its own start point must still count as motion.");
    if (Math.Abs(block.End.X - 10) > 1e-6 || Math.Abs(block.End.Y) > 1e-6 || Math.Abs(block.End.Z - 94) > 1e-6)
        throw new Exception($"Helix endpoint drifted: {block.End}.");
    // Half way through the helix the tool must be on the far side of the circle,
    // which is what a straight chord could never produce.
    var mid = block.Path.Samples[block.Path.Samples.Count / 2];
    if (Math.Sqrt(Math.Pow(mid.X - 0, 2) + Math.Pow(mid.Y - 0, 2)) < 9.9)
        throw new Exception($"Helix sample left the circle: {mid}.");

    // A plain quarter arc keeps its radius all the way round.
    var arc = GCodeParser.Parse(
        "arc.mpf",
        new[] { "T1 M6", "G54", "G17 G0 X10 Y0 Z0", "G2 X0 Y-10 I-10 J0 F200" },
        FakeMachine(),
        context);
    var quarter = arc.Blocks[^1];
    if (quarter.Path is null) throw new Exception("G2 arc produced no interpolated path.");
    if (Math.Abs(quarter.Path.Length - (Math.PI * 10 / 2)) > 0.2)
        throw new Exception($"Quarter arc length {quarter.Path.Length:0.###} mm, expected {Math.PI * 10 / 2:0.###} mm.");
    foreach (var sample in quarter.Path.Samples)
    {
        var radius = Math.Sqrt((sample.X * sample.X) + (sample.Y * sample.Y));
        if (Math.Abs(radius - 10) > 0.05)
            throw new Exception($"Arc sample off the circle: r={radius:0.###} at {sample}.");
    }
}

static void TestNamedToolCall()
{
    // A SINUMERIK post can call tools by name on its own block and
    // issues M6 on the next one.  Without name resolution the active tool stays
    // null, the gauge length collapses to 0 and the tool is drawn a full tool
    // length below the programmed tip - it then travels through the part.
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", new Vector3(0, 0, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double> { [11] = 114.8 },
        ToolNumbersByName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["END_MILL_10"] = 11 });
    var program = GCodeParser.Parse(
        "named.mpf",
        new[] { "N19 T=\"END_MILL_10\"", "N20 M6", "N22 D1", "N23 G54", "N30 G0 X0 Y0 Z0" },
        FakeMachine(),
        context);
    var last = program.Blocks[^1];
    if (last.Tool != 11)
        throw new Exception($"T=\"NAME\" tool call was not resolved: tool={last.Tool?.ToString() ?? "null"}");
    if (Math.Abs(last.End.Z - 214.8) > 1e-6)
        throw new Exception($"Named tool gauge length was not applied: Z={last.End.Z:0.###}, expected 214.8");

    var unknown = GCodeParser.Parse(
        "named.mpf",
        new[] { "N19 T=\"NO_SUCH_TOOL\"", "N20 M6", "N30 G0 X0 Y0 Z0" },
        FakeMachine(),
        context);
    if (!unknown.Blocks.Any(x => x.Warning is not null && x.Warning.Contains("NO_SUCH_TOOL", StringComparison.Ordinal)))
        throw new Exception("An unresolved named tool call must be reported, not silently ignored.");
}

static void TestWorkFrameMapping()
{
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", new Vector3(10, 20, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double> { [2] = 125 });
    var program = GCodeParser.Parse("work.mpf", new[] { "T2 M6", "G54", "G0 X5 Y-3 Z7" }, FakeMachine(), context);
    var end = program.Blocks[^1].End;
    if (Math.Abs(end.X - 15) > 1e-6 || Math.Abs(end.Y - 17) > 1e-6 || Math.Abs(end.Z - 232) > 1e-6)
        throw new Exception($"Work/tool mapping mismatch: {end}");
}

static void TestRotaryWorkFrameMapping()
{
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double>());
    var program = GCodeParser.Parse("rotary-work.mpf", new[]
    {
        "G90 G0 B90 C=DC(0.) X10 Y0 Z0",
        "G1 X20"
    }, FakeMachine(), context);
    var first = program.Blocks[0].End;
    var second = program.Blocks[1].End;
    // Explicit postprocessed machine B/C words do not rotate G54 a second time.
    AssertNear(new Vector3((float)first.X, (float)first.Y, (float)first.Z), new Vector3(10, 0, 0), 0.0001f);
    AssertNear(new Vector3((float)second.X, (float)second.Y, (float)second.Z), new Vector3(20, 0, 0), 0.0001f);
}

static void TestRotaryPivotRigidity()
{
    var machine = FakeBcTableMachine();
    var bAxis = machine.Axes.Single(x => x.Name == "B1");
    var cAxis = machine.Axes.Single(x => x.Name == "C1");
    var bPivot = MachineCoordinateResolver.ResolveAxisPivot(machine, bAxis);
    var cPivot = MachineCoordinateResolver.ResolveAxisPivot(machine, cAxis);
    var bTransform = MachineCoordinateResolver.BuildAxisDeltaTransform(machine, bAxis, 45);
    var cTransform = MachineCoordinateResolver.BuildAxisDeltaTransform(machine, cAxis, 123);

    AssertNear(Vector3.Transform(bPivot, bTransform), bPivot, 0.0001f);
    AssertNear(Vector3.Transform(cPivot, cTransform), cPivot, 0.0001f);

    var combined = MachineCoordinateResolver.BuildWorkpieceTransform(machine, 45, 123);
    var expectedMountedCenter = Vector3.Transform(Vector3.Transform(cPivot, cTransform), bTransform);
    AssertNear(Vector3.Transform(cPivot, combined), expectedMountedCenter, 0.0001f);

    // A fixture point and the corresponding table point must remain coincident
    // after both rotary axes move; otherwise the workpiece appears to rotate
    // around a different center than the C-axis table.
    var fixtureContact = new Vector3(40, -25, 0);
    var tableContact = fixtureContact;
    AssertNear(
        Vector3.Transform(fixtureContact, combined),
        Vector3.Transform(tableContact, cTransform * bTransform),
        0.0001f);
}

static void TestCycle800IndexedMapping()
{
    var machine = FakeBcTableMachine();
    var context = new GCodeSimulationContext(
        new CoordinateFrame("G54", Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, "test"),
        new Dictionary<int, double>());
    var program = GCodeParser.Parse("cycle800.mpf", new[]
    {
        "G90 G54",
        "CYCLE800(0,\"TABLE\",0,57,0,0,0,0,-45,0,0,0,0,1,0)",
        "G0 X10 Y20 Z30",
        "CYCLE800()"
    }, machine, context);

    var indexed = program.Blocks[1].End;
    if (Math.Abs(indexed.B - 45) > 1e-6 || Math.Abs(indexed.C - 180) > 1e-6)
        throw new Exception($"CYCLE800 indexed target mismatch: {indexed}");

    var move = program.Blocks[2].End;
    AssertNear(
        new Vector3((float)move.X, (float)move.Y, (float)move.Z),
        new Vector3(-10, -20, 30),
        0.0002f);

    var known = new[]
    {
        // Synthetic orientations: constants independently obtained by solving
        // the matrix normal-alignment constraint, not by this C# resolver.
        (A: 0.0, P: -45.0, B: 45.0, C: 180.0, Rz: -180.0),
        (A: 30.0, P: 20.0, B: 35.531347762804, C: 306.052388732388, Rz: 59.357657952044),
        // A crosses -90 degrees: retain the quadrant-loss regression without
        // publishing a production program's angles or machining coordinates.
        (A: -110.0, P: 2.0, B: 109.987296844510, C: 87.871758932910, Rz: -90.727753509216)
    };
    foreach (var item in known)
    {
        if (!MachineCoordinateResolver.TryResolveCycle800DualTablePose(machine, item.A, item.P, 0, out var b, out var c, out var rz))
            throw new Exception("Known Generic B/C CYCLE800 orientation was not resolved.");
        var rzDelta = (rz - item.Rz) % 360.0;
        if (rzDelta > 180) rzDelta -= 360;
        if (rzDelta <= -180) rzDelta += 360;
        if (Math.Abs(b - item.B) > 0.001 || Math.Abs(c - item.C) > 0.001 || Math.Abs(rzDelta) > 0.001)
             throw new Exception($"Synthetic CYCLE800 matrix-oracle mismatch: A{item.A}/B{item.P} -> B{b}/C{c}/RZ{rz}");
    }

    var highTilt = GCodeParser.Parse("cycle800-synthetic-high-tilt.mpf", new[]
    {
        "G90 G54",
        "CYCLE800(0,\"TABLE\",0,57,0,0,0,-110,2,0,0,0,0,1,0)",
        "G0 X12 Y8 Z4"
    }, machine, context);
    var highTiltPose = highTilt.Blocks[1].End;
    if (Math.Abs(highTiltPose.B - 109.987296844510) > 0.001 ||
        Math.Abs(highTiltPose.C - 87.871758932910) > 0.001)
        throw new Exception($"Synthetic high-tilt physical pose mismatch: {highTiltPose}");
    var expectedHighTiltPoint = Vector3.Transform(
        new Vector3(12f, 8f, 4f),
        Matrix4x4.CreateRotationZ((float)(-90.727753509216 * Math.PI / 180.0)));
    var highTiltMove = highTilt.Blocks[2].End;
    AssertNear(
        new Vector3((float)highTiltMove.X, (float)highTiltMove.Y, (float)highTiltMove.Z),
        expectedHighTiltPoint,
        0.0003f);

    var manual = GCodeParser.Parse("cycle800-manual.mpf", new[]
    {
        "G90 B20.096 C=DC(275.83)",
        "CYCLE800(0,\"TABLE\",220000,57,0,0,0,20,-2,0,0,0,0,0,0)"
    }, machine, context);
    if (Math.Abs(manual.Blocks[1].End.B - 20.096) > 0.0001 ||
        Math.Abs(manual.Blocks[1].End.C - -84.17) > 0.0001)
        throw new Exception("Manual CYCLE800 overwrote the explicit preceding B/C pose.");
}

static void TestToolPocketPlacement()
{
    var pocket = new CoordinateFrame(
        "POCKET_JCT",
        new Vector3(0, 0, 600),
        Vector3.UnitZ,
        Vector3.UnitY,
        -Vector3.UnitX,
        "test");
    const float gauge = 125;
    var placement = Matrix4x4.CreateTranslation(-gauge, 0, 0) * CoordinateTransforms.FrameMatrix(pocket);
    var holderBack = Vector3.Transform(new Vector3(gauge, 0, 0), placement);
    var tip = Vector3.Transform(Vector3.Zero, placement);
    AssertNear(holderBack, pocket.Origin, 0.0001f);
    AssertNear(tip, new Vector3(0, 0, 475), 0.0001f);
}

static void TestPlayer()
{
    var program = GCodeParser.Parse("memory.nc", new[] { "G90 G0 B45", "G1 X20 F600" }, FakeMachine());
    var player = new SimulationPlayer();
    player.Load(program);
    if (!player.Step()) throw new Exception("Player did not execute the rotary block.");
    if (Math.Abs(player.Position.B - 45) > 1e-6) throw new Exception("Player rotary position did not change.");
    if (player.DisplayedBlockIndex != 0 || player.CurrentBlock?.SourceLine != 1)
        throw new Exception("Completed rotary move is displayed on the following NC source line.");
    if (!player.Step() || player.DisplayedBlockIndex != 1 || player.CurrentBlock?.SourceLine != 2)
        throw new Exception("Step did not advance the displayed NC source line exactly once.");

    player.Seek(0, completed: true);
    if (!player.Step() || player.DisplayedBlockIndex != 1)
        throw new Exception("Stepping after a completed seek repeated the selected block.");

    var toolChangeProgram = GCodeParser.Parse("tool-change.mpf", new[]
    {
        "G90 SUPA G0 Z100",
        "T1 M6",
        "G0 Z0"
    }, FakeMachine());
    var fastPlayer = new SimulationPlayer { SpeedFactor = 100 };
    fastPlayer.Load(toolChangeProgram);
    fastPlayer.Play();
    if (!fastPlayer.Advance(TimeSpan.FromSeconds(1)))
        throw new Exception("High-speed player did not advance to the tool change.");
    if (fastPlayer.DisplayedBlockIndex != 1 || !fastPlayer.CurrentBlock!.IsToolChange ||
        Math.Abs(fastPlayer.Position.Z - 100) > 1e-6 || fastPlayer.BlockIndex != 2)
        throw new Exception("High-speed playback skipped the M6 reference-pose render barrier.");
}

static void TestIndexedRotaryPositioning()
{
    var program = GCodeParser.Parse("rotary.mpf", new[]
    {
        "G90 G0 B20.096 C=DC(275.83)",
        "G1 C=DC(280.33)",
        "G1 C=DC(1.33)"
    }, FakeMachine());
    var indexed = program.Blocks[0];
    if (Math.Abs(indexed.End.B - 20.096) > 1e-6 || Math.Abs(indexed.End.C - -84.17) > 1e-6)
        throw new Exception($"Indexed B/C target mismatch: {indexed.End}");
    if (Math.Abs(program.Blocks[1].End.C - -79.67) > 1e-6)
        throw new Exception("DC positioning did not take the shortest path.");
    if (Math.Abs(program.Blocks[2].End.C - 1.33) > 1e-6)
        throw new Exception("DC wrap-around introduced a full reverse revolution.");

    var player = new SimulationPlayer();
    player.Load(program);
    player.Seek(0, completed: true);
    if (Math.Abs(player.Position.B - 20.096) > 1e-6 || Math.Abs(player.Position.C - -84.17) > 1e-6)
        throw new Exception("Selecting the positioning row still shows the pre-block rotary state.");
}

static void TestParametricToolMesh()
{
    var tool = new JobTool(
        "T07", "D12 BALL", "Mill", 12, 6, 60, "HSK63", null,
        "MillBall", 24, 12, 0, 0, 20, "HSK63-ER32", "nx-parametric-tool-builder",
        new[] { new ToolProfileSection(12, 36, 0, 0) },
        new[]
        {
            new ToolProfileSection(20, 10, 45, 0),
            new ToolProfileSection(40, 20, 0, 0)
        });
    var mesh = ParametricToolMeshBuilder.Build(tool);
    var parts = ParametricToolMeshBuilder.BuildParts(tool);
    if (mesh.TriangleCount < 500) throw new Exception("Generated tool mesh is unexpectedly small.");
    if (mesh.Bounds.Min.X < -0.0001 || Math.Abs(mesh.Bounds.Max.X - 70) > 0.001)
        throw new Exception($"NX toolInsertion overlap was not applied to gauge length: {mesh.Bounds.Max.X:0.###}.");
    if (mesh.Bounds.Max.Y < 19.9f) throw new Exception("Holder taper/diameter was not applied.");
    if (!parts.Select(x => x.Role).SequenceEqual(new[] { "cutter", "shank", "holder_taper", "holder_flange" }))
        throw new Exception("Cutter, shank and holder sections were not emitted as distinct meshes.");
}

static void TestParametricJobRoundTrip()
{
    var root = Path.Combine(Path.GetTempPath(), "trmachinist_smoke_" + Guid.NewGuid().ToString("N"));
    var package = root + ".trjob";
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "project.json"), """
        {"partNumber":"TEST","partName":"PARAMETRIC","program":"NC_PROGRAM","models":[],"machineMountCsys":{"label":"MOUNT","origin":[0,0,0],"xAxis":[1,0,0],"yAxis":[0,1,0],"zAxis":[0,0,1],"source":"nx-csys"}}
        """);
        File.WriteAllText(Path.Combine(root, "operations.json"), "[]");
        File.WriteAllText(Path.Combine(root, "tools.json"), """
        [{"id":"T07","name":"D12 BALL","type":"Mill","subtype":"MillBall","diameter":12,"radius":6,"length":60,"fluteLength":24,"shankDiameter":12,"holder":"HSK63","holderLibraryReference":"HSK63-ER32","geometrySource":"nx-parametric-tool-builder","shankSections":[{"diameter":12,"length":36}],"holderSections":[{"diameter":20,"length":10,"taperAngle":45},{"diameter":40,"length":20}]}]
        """);
        var inventory = Directory.GetFiles(root).Select(path => new
        {
            path = Path.GetFileName(path),
            size = new FileInfo(path).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        }).ToArray();
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
        {
            format = "trmachinist-job/0.2",
            version = 2,
            entries = inventory
        }));
        ZipFile.CreateFromDirectory(root, package, CompressionLevel.Fastest, false);

        var job = JobPackageReader.Load(package);
        var tool = job.Tools.Single();
        if (tool.HolderLibraryReference != "HSK63-ER32" || tool.HolderSections.Count != 2 || tool.ShankSections.Count != 1)
            throw new Exception("Parametric tool fields did not survive TRJOB serialization.");
        if (ParametricToolMeshBuilder.Build(tool).TriangleCount <= 0) throw new Exception("Round-trip tool mesh is empty.");
    }
    finally
    {
        try { if (File.Exists(package)) File.Delete(package); } catch { }
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}

static void TestMachinePackage(string path)
{
    var machine = MachinePackageReader.Load(path);
    if (!machine.Junctions.Any(x => x.Name.Equals("PART_MOUNT_JCT", StringComparison.OrdinalIgnoreCase)))
        throw new Exception("PART_MOUNT_JCT is missing.");
    if (!machine.HasRuntimeMeshes) throw new Exception("Runtime STL mesh inventory is missing.");
    foreach (var component in machine.Components.Where(x => !string.IsNullOrWhiteSpace(x.RuntimeMeshPath)))
    {
        var mesh = StlMeshReader.Load(machine.Resolve(component.RuntimeMeshPath!));
        if (mesh.TriangleCount <= 0) throw new Exception($"Empty mesh: {component.Name}");
    }
}

static void TestJobPackage(string path)
{
    var job = JobPackageReader.Load(path);
    if (job.Models.Count == 0) throw new Exception("No job model was found.");
    if (job.MachineMount.Source == "fallback") throw new Exception("machineMountCsys is missing.");
    if (job.Tools.Count < 1) throw new Exception("NX job has no NC tools.");
    if (!job.Tools.Any(tool => tool.Diameter > 0 && tool.FluteLength > 0))
        throw new Exception("NX job has no cutter with diameter/flute geometry.");
    foreach (var tool in job.Tools)
    {
        if (string.IsNullOrWhiteSpace(tool.ModelPath) || !File.Exists(job.Resolve(tool.ModelPath)))
            throw new Exception($"Real NX tool assembly STL is missing for {tool.Id} {tool.Name}.");
        var mesh = StlMeshReader.Load(job.Resolve(tool.ModelPath));
        if (mesh.TriangleCount < 100 || mesh.Bounds.Max.X <= 80)
            throw new Exception($"NX tool assembly is empty or implausible for {tool.Id}: {mesh.Bounds}.");
        if (tool.MountPoint.HasValue != tool.TipPoint.HasValue)
            throw new Exception($"Incomplete SIM_TOOL_MOUNT/SIM_TOOL_TIP pair for {tool.Id}.");
    }

    if (job.Tools.Single(x => x.Id.Equals("T09", StringComparison.OrdinalIgnoreCase)).Name != "UGT0333_1001" ||
        job.Tools.Single(x => x.Id.Equals("T10", StringComparison.OrdinalIgnoreCase)).Name != "UGT0231_1001")
        throw new Exception("NX runtime T09/T10 pocket identity was not preserved.");

    var faceMill = job.Tools.Single(x => x.Id.Equals("T02", StringComparison.OrdinalIgnoreCase));
    var faceMillMesh = StlMeshReader.Load(job.Resolve(faceMill.ModelPath!));
    if (Math.Abs(faceMillMesh.Bounds.Max.X - 136.1f) > 0.2f)
        throw new Exception($"T02 is not the authoritative NX UGT0202_1006 assembly: {faceMillMesh.Bounds}.");
    if (faceMillMesh.TriangleCount < 5000)
        throw new Exception($"T02 NX mesh tessellation is below the visual-quality gate: {faceMillMesh.TriangleCount} triangles.");
    if (!faceMill.GeometrySource.StartsWith("nx-exported-tool-assembly", StringComparison.OrdinalIgnoreCase) &&
        !faceMill.GeometrySource.Equals("nx-parametric-tool-builder", StringComparison.OrdinalIgnoreCase))
        throw new Exception($"T02 geometry source is unsupported: {faceMill.GeometrySource}.");
}

static void TestCoordinateUserOffset()
{
    var source = CoordinateFrame.Identity("SOURCE");
    var target = new CoordinateFrame(
        "PART_MOUNT_JCT",
        new Vector3(100, 200, 300),
        Vector3.UnitY,
        -Vector3.UnitX,
        Vector3.UnitZ,
        "test");
    var basePlacement = CoordinateTransforms.BuildPlacement(source, target);
    var shifted = CoordinateTransforms.ApplyTargetFrameOffset(
        basePlacement,
        target,
        new Vector3(10, 20, 30));
    var placedOrigin = Vector3.Transform(Vector3.Zero, shifted);
    var expected = new Vector3(80, 210, 330);
    if (Vector3.Distance(placedOrigin, expected) > 1e-5f)
        throw new Exception($"CSYS-local offset followed machine axes instead of selected frame: {placedOrigin}.");
    if (Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, shifted), target.XAxis) > 1e-5f)
        throw new Exception("CSYS-local offset changed placement orientation.");
}

static void TestToolHolderLibrary()
{
    var tool = new JobTool(
        "T07", "D12 BALL", "Mill", 12, 6, 60, "HSK63", null,
        "MillBall", 24, 12, 0, 0, 20, "HSK63-EXACT", "nx-parametric-tool-builder",
        new[] { new ToolProfileSection(12, 36, 0, 0) },
        new[]
        {
            new ToolProfileSection(21, 11, 12, 0),
            new ToolProfileSection(41, 19, 0, 0)
        });
    var library = ToolHolderLibrary.Create(new[] { tool });
    var exact = library.FirstOrDefault(preset => preset.Source == "NX .trjob")
        ?? throw new Exception("The exact NX holder was not promoted into the holder library.");
    if (exact.ToolInsertion != 20 || exact.Sections.Count != 2 || exact.Sections[1].Diameter != 41)
        throw new Exception("The NX holder profile changed while entering the library.");
    if (!library.Any(preset => preset.Source == "TRMachinist hazır profil"))
        throw new Exception("Built-in holder profiles are missing.");
}

static void TestRealTest4StockSurface(string jobPath)
{
    var job = JobPackageReader.Load(jobPath);
    var stockAsset = job.Models.First(model =>
        model.Role.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
        model.Role.Contains("blank", StringComparison.OrdinalIgnoreCase));
    var mesh = StlMeshReader.Load(job.Resolve(stockAsset.Path));
    var pitch = TripleDexelStock.RecommendPitch(mesh.Bounds);
    var timer = Stopwatch.StartNew();
    var stock = new TripleDexelStock(mesh.Bounds, pitch, mesh);
    var rasterTime = timer.Elapsed;
    timer.Restart();
    var chunks = stock.BuildDirtySurfaceChunks();
    var meshTime = timer.Elapsed;
    var volume = stock.Volume();
    if (volume.Mean <= 0 || volume.Spread / volume.Mean > 0.04)
        throw new Exception($"Real blank STL tri-dexel bundles disagree: {volume}.");
    if (chunks.Sum(chunk => chunk.TriangleCount) <= 0)
        throw new Exception("Real blank STL did not produce a full 3D regional surface.");
    var cutTip = new Vector3(mesh.Bounds.Center.X, mesh.Bounds.Center.Y, mesh.Bounds.Max.Z - 5);
    stock.ApplyCylindricalCutterMove(
        cutTip, cutTip, Vector3.UnitZ, Vector3.UnitZ,
        radius: 5, fluteLength: 10, isRapid: false, cutTag: 1);
    var dirtyCount = stock.DirtySurfaceChunkCount;
    timer.Restart();
    var changed = stock.BuildDirtySurfaceChunks();
    var updateTime = timer.Elapsed;
    if (dirtyCount <= 0 || dirtyCount >= stock.SurfaceChunkCount || changed.Count != dirtyCount)
        throw new Exception($"Real local cut did not remain regional: {dirtyCount}/{stock.SurfaceChunkCount} chunks.");
    Console.WriteLine(
        $"      Test4 blank: pitch={pitch:0.###}; chunks={stock.SurfaceChunkCount}; " +
        $"triangles={chunks.Sum(chunk => chunk.TriangleCount):N0}; raster={rasterTime.TotalSeconds:0.00}s; " +
        $"mesh={meshTime.TotalSeconds:0.00}s; local={dirtyCount} chunks/{updateTime.TotalMilliseconds:0}ms");
}

static void TestRealPlacement(string machinePath, string jobPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var table = MachineCoordinateResolver.ResolveTableFrame(machine);
    var placement = CoordinateTransforms.BuildPlacement(job.MachineMount, table);
    var placedOrigin = CoordinateTransforms.TransformPoint(job.MachineMount.Origin, placement);
    AssertNear(placedOrigin, table.Origin, 0.001f);
    var placedX = CoordinateTransforms.TransformPoint(job.MachineMount.Origin + job.MachineMount.XAxis, placement) - placedOrigin;
    var placedY = CoordinateTransforms.TransformPoint(job.MachineMount.Origin + job.MachineMount.YAxis, placement) - placedOrigin;
    var placedZ = CoordinateTransforms.TransformPoint(job.MachineMount.Origin + job.MachineMount.ZAxis, placement) - placedOrigin;
    AssertNear(Vector3.Normalize(placedX), table.XAxis, 0.0001f);
    AssertNear(Vector3.Normalize(placedY), table.YAxis, 0.0001f);
    AssertNear(Vector3.Normalize(placedZ), table.ZAxis, 0.0001f);

    var mount = machine.Junctions.Single(x => x.Name.Equals("PART_MOUNT_JCT", StringComparison.OrdinalIgnoreCase));
    var tableComponent = FindFirstGeometryAncestor(machine, mount.Owner)
        ?? throw new Exception($"PART_MOUNT_JCT sahibi {mount.Owner} altında hareketli tabla geometrisi bulunamadı.");
    var tableBounds = TransformBounds(
        StlMeshReader.Load(machine.Resolve(tableComponent.RuntimeMeshPath!)).Bounds,
        tableComponent.GraphicsTransform);
    var fixture = job.Models.First(x => x.Role.Contains("fixture", StringComparison.OrdinalIgnoreCase));
    var fixtureBounds = TransformBounds(StlMeshReader.Load(job.Resolve(fixture.Path)).Bounds, placement);
    var spindleComponent = machine.Components.Single(x => x.Name.Equals("SPINDLE", StringComparison.OrdinalIgnoreCase));
    var spindleBounds = TransformBounds(
        StlMeshReader.Load(machine.Resolve(spindleComponent.RuntimeMeshPath!)).Bounds,
        spindleComponent.GraphicsTransform);
    var bPivot = machine.Junctions.Single(x => x.Name.Equals("B_AXIS_JCT", StringComparison.OrdinalIgnoreCase)).Origin;
    Console.WriteLine($"      table={tableBounds}; fixture={fixtureBounds}; B pivot={bPivot}");
    if (fixtureBounds.Min.Z < tableBounds.Min.Z || fixtureBounds.Max.Z >= spindleBounds.Min.Z)
        throw new Exception($"Fixture is not in the table-to-spindle corridor: fixture={fixtureBounds}, table={tableBounds}, spindle={spindleBounds}.");
    if (fixtureBounds.Min.X < tableBounds.Min.X || fixtureBounds.Max.X > tableBounds.Max.X ||
        fixtureBounds.Min.Y < tableBounds.Min.Y || fixtureBounds.Max.Y > tableBounds.Max.Y)
        throw new Exception($"Fixture footprint is not centered on the C-axis table: fixture={fixtureBounds}, table={tableBounds}.");
    if (!table.Source.Contains("canonical-kim-junction", StringComparison.OrdinalIgnoreCase))
        throw new Exception("U630 PART_MOUNT_JCT is not being consumed in canonical KIM space.");

    var yAxis = FindAddressAxis(machine, 'Y');
    var zAxis = FindAddressAxis(machine, 'Z');
    var cAxis = FindAddressAxis(machine, 'C');
    AssertNear(table.ZAxis, Vector3.UnitZ, 0.03f);
    AssertNear(MachineCoordinateResolver.ResolveAxisVector(machine, yAxis.Vector), Vector3.UnitY, 0.0001f);
    AssertNear(MachineCoordinateResolver.ResolveAxisVector(machine, zAxis.Vector), Vector3.UnitZ, 0.0001f);
    AssertNear(MachineCoordinateResolver.ResolveAxisVector(machine, cAxis.Vector), -Vector3.UnitZ, 0.0001f);

    var bAxis = FindAddressAxis(machine, 'B');
    AssertNear(MachineCoordinateResolver.ResolveAxisPivot(machine, bAxis), bPivot, 0.0001f);
    var cPivot = MachineCoordinateResolver.ResolveAxisPivot(machine, cAxis);
    AssertNear(MachineCoordinateResolver.ResolveAxisPivot(machine, cAxis), cPivot, 0.0001f);
}

static void TestRealToolMount(string machinePath, string jobPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var pocket = machine.Junctions.FirstOrDefault(x =>
        x.Name.Equals("POCKET_JCT", StringComparison.OrdinalIgnoreCase))
        ?? machine.Junctions.FirstOrDefault(x => x.Name.Equals("S", StringComparison.OrdinalIgnoreCase))
        ?? throw new Exception("Gerçek makinede spindle takım bağlama junction'ı yok.");
    if (!machine.Components.Any(x => x.Name.Equals(pocket.Owner, StringComparison.OrdinalIgnoreCase)))
        throw new Exception($"Takım junction sahibi kinematik ağaçta yok: {pocket.Owner}");

    var tool = job.Tools.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ModelPath))
        ?? throw new Exception("Gerçek NX iş paketinde takım modeli yok.");
    var mesh = StlMeshReader.Load(job.Resolve(tool.ModelPath!));
    var centerY = (mesh.Bounds.Min.Y + mesh.Bounds.Max.Y) * 0.5f;
    var centerZ = (mesh.Bounds.Min.Z + mesh.Bounds.Max.Z) * 0.5f;
    if (Math.Abs(centerY) > 0.2f || Math.Abs(centerZ) > 0.2f)
        throw new Exception($"NX takım modeli X eksenine merkezli değil: Y={centerY}, Z={centerZ}");

    var frame = new CoordinateFrame(
        pocket.Name,
        pocket.Origin,
        new Vector3(pocket.Orientation.M11, pocket.Orientation.M12, pocket.Orientation.M13),
        new Vector3(pocket.Orientation.M21, pocket.Orientation.M22, pocket.Orientation.M23),
        new Vector3(pocket.Orientation.M31, pocket.Orientation.M32, pocket.Orientation.M33),
        "real-trmac-tool-mount");
    var placement = Matrix4x4.CreateTranslation(-mesh.Bounds.Max.X, 0, 0)
        * CoordinateTransforms.FrameMatrix(frame);
    var holderBack = Vector3.Transform(new Vector3(mesh.Bounds.Max.X, 0, 0), placement);
    var toolTip = Vector3.Transform(new Vector3(mesh.Bounds.Min.X, 0, 0), placement);
    AssertNear(holderBack, pocket.Origin, 0.001f);

    var table = MachineCoordinateResolver.ResolveTableFrame(machine);
    if (Vector3.Distance(toolTip, table.Origin) >= Vector3.Distance(holderBack, table.Origin))
        throw new Exception($"Takım spindle'dan tablaya doğru uzanmıyor: holder={holderBack}, tip={toolTip}, table={table.Origin}");
    Console.WriteLine($"      {tool.Id}: holder={holderBack}; tip={toolTip}; table={table.Origin}; triangles={mesh.TriangleCount}");
}

static MachineComponent? FindFirstGeometryAncestor(MachinePackage machine, string componentName)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var current = componentName;
    while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
    {
        var component = machine.Components.FirstOrDefault(x => x.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (component is null) return null;
        if (!string.IsNullOrWhiteSpace(component.RuntimeMeshPath)) return component;
        current = component.Parent ?? "";
    }
    return null;
}

static AxisDefinition FindAddressAxis(MachinePackage machine, char address) =>
    machine.Axes.FirstOrDefault(x => x.Name.Length > 0 && char.ToUpperInvariant(x.Name[0]) == char.ToUpperInvariant(address))
    ?? throw new Exception($"Gerçek makinede {address} ekseni yok.");

static void TestRealNc(string machinePath, string ncPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var program = GCodeParser.Load(ncPath, machine);
    Console.WriteLine($"      {program.Blocks.Count} blocks, {program.MotionCount} motions, {program.WarningCount} warnings, {program.ErrorCount} errors");
    if (program.ErrorCount != 0)
    {
        foreach (var block in program.Blocks.Where(x => x.Error is not null).Take(12))
            Console.WriteLine($"      line {block.SourceLine}: {block.Error} | {block.Raw}");
        throw new Exception($"Real U630 NC still has {program.ErrorCount} false/genuine limit errors.");
    }
    if (program.WarningCount > 20) throw new Exception($"Warning flood remains: {program.WarningCount}.");
    var indexed = program.Blocks.FirstOrDefault(x =>
        x.Raw.Contains("C=DC(", StringComparison.OrdinalIgnoreCase) &&
        (Math.Abs(x.End.B - x.Start.B) > 0.001 || Math.Abs(x.End.C - x.Start.C) > 0.001));
    if (indexed is null) throw new Exception("Real U630 indexed B/C positioning block was not found.");
    Console.WriteLine($"      indexed line {indexed.SourceLine}: B {indexed.Start.B:0.###}->{indexed.End.B:0.###}, C {indexed.Start.C:0.###}->{indexed.End.C:0.###}");
    if (Math.Abs(indexed.End.C - indexed.Start.C) > 180.0001)
        throw new Exception("Real U630 DC rotary target did not take the shortest path.");

    var implicitCycle = program.Blocks.FirstOrDefault(x =>
        x.Raw.Contains("CYCLE800(0,\"TABLE\"", StringComparison.OrdinalIgnoreCase) && x.HasMotion);
    if (implicitCycle is null)
        throw new Exception($"Real U630 implicit CYCLE800 positioning was not applied: {implicitCycle?.End}");
    if (!implicitCycle.HasMotion)
        throw new Exception("Real U630 implicit CYCLE800 positioning is not an executable rotary-motion block.");
}

static void TestRealAlpha4MaterialRemoval(string machinePath, string jobPath, string ncPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var table = MachineCoordinateResolver.ResolveTableFrame(machine);
    var placement = CoordinateTransforms.BuildPlacement(job.MachineMount, table);
    if (!Matrix4x4.Invert(placement, out var inversePlacement))
        throw new Exception("R2 workpiece placement is not invertible.");

    CoordinateFrame PlaceFrame(CoordinateFrame frame, string source) => new(
        frame.Label,
        Vector3.Transform(frame.Origin, placement),
        Vector3.Normalize(Vector3.TransformNormal(frame.XAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.YAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.ZAxis, placement)),
        source);

    var gauges = new Dictionary<int, double>();
    var toolNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        gauges[number] = Vector3.Distance(
            tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0),
            tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0));
        toolNames[tool.Id] = number;
        if (!string.IsNullOrWhiteSpace(tool.Name)) toolNames[tool.Name] = number;
    }

    var workFrame = PlaceFrame(job.Mcs, "alpha4:r2-mcs");
    var controllerFrame = PlaceFrame(
        job.HasExplicitControllerWorkFrame ? job.ControllerWorkFrame : job.Mcs,
        "alpha4:r2-controller-or-mcs");
    var context = new GCodeSimulationContext(
        workFrame,
        gauges,
        controllerFrame,
        CoordinateTransforms.BuildPlacement(controllerFrame, workFrame),
        toolNames);
    var program = GCodeParser.Load(ncPath, machine, context);
    var t02 = job.Tools.First(tool => new string(tool.Id.Where(char.IsDigit).ToArray()) == "02");
    var stockAsset = job.Models.First(model =>
        model.Role.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
        model.Role.Contains("blank", StringComparison.OrdinalIgnoreCase));
    var stockBounds = StlMeshReader.Load(job.Resolve(stockAsset.Path)).Bounds;
    var stock = new TripleDexelStock(stockBounds, 1.5);
    var initial = stock.Volume();
    var firstFaceIndex = program.Blocks.ToList().FindIndex(block =>
        block.Tool == 2 && block.Motion == MotionKind.Linear &&
        block.Raw.Contains("Z0.", StringComparison.OrdinalIgnoreCase));
    if (firstFaceIndex < 0) throw new Exception("R2 first T02 face cut was not found.");

    var rapidPassed = true;
    var processedFeedMoves = 0;
    foreach (var block in program.Blocks.Skip(firstFaceIndex))
    {
        if (Math.Abs(block.End.B) > 0.001 || Math.Abs(block.End.C) > 0.001) break;
        if (!block.HasMotion || block.Tool != 2) continue;
        var start = Pose(block.Start, gauges[2]);
        var end = Pose(block.End, gauges[2]);
        var result = stock.ApplyCylindricalCutterMove(
            start.Tip, end.Tip, start.Axis, end.Axis,
            t02.Diameter * 0.5,
            t02.FluteLength,
            block.Motion == MotionKind.Rapid);
        if (block.Motion == MotionKind.Rapid) rapidPassed &= result.RemovedNothing;
        else processedFeedMoves++;
    }

    var remaining = stock.Volume();
    var removed = new TripleDexelVolume(
        initial.X - remaining.X,
        initial.Y - remaining.Y,
        initial.Z - remaining.Z);
    if (!rapidPassed) throw new Exception("A real R2 G0 block removed stock.");
    if (processedFeedMoves < 10)
        throw new Exception($"R2 face-cut feed coverage is too low: {processedFeedMoves}.");
    if (removed.X <= 1000 || removed.Y <= 1000 || removed.Z <= 1000)
        throw new Exception($"R2 face milling did not visibly remove stock: {removed}.");
    Console.WriteLine($"      R2 T02 face milling: {processedFeedMoves} feed moves; removed X/Y/Z={removed}");

    (Vector3 Tip, Vector3 Axis) Pose(AxisState state, double gauge)
    {
        var tipWorld = new Vector3((float)state.X, (float)state.Y, (float)(state.Z - gauge));
        var axisWorld = tipWorld + Vector3.UnitZ;
        var neutralTip = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, tipWorld, state.B, state.C);
        var neutralAxis = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, axisWorld, state.B, state.C);
        var localTip = Vector3.Transform(neutralTip, inversePlacement);
        var localAxisPoint = Vector3.Transform(neutralAxis, inversePlacement);
        return (localTip, Vector3.Normalize(localAxisPoint - localTip));
    }
}

static void AuditCurrentAlpha4Ipw(
    string machinePath,
    string jobPath,
    string ncPath,
    double pitch,
    bool auditSurface,
    int maximumSourceLine)
{
    var auditTimer = Stopwatch.StartNew();
    var phaseTimer = Stopwatch.StartNew();
    if (!double.IsFinite(pitch) || pitch <= 0) throw new ArgumentOutOfRangeException(nameof(pitch));
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var table = MachineCoordinateResolver.ResolveTableFrame(machine);
    var placement = CoordinateTransforms.BuildPlacement(job.MachineMount, table);
    if (!Matrix4x4.Invert(placement, out var inversePlacement))
        throw new Exception("IPW audit placement is not invertible.");

    CoordinateFrame PlaceFrame(CoordinateFrame frame, string source) => new(
        frame.Label,
        Vector3.Transform(frame.Origin, placement),
        Vector3.Normalize(Vector3.TransformNormal(frame.XAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.YAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.ZAxis, placement)),
        source);

    var gauges = new Dictionary<int, double>();
    var radii = new Dictionary<int, double>();
    var tools = new Dictionary<int, JobTool>();
    var toolNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        gauges[number] = Vector3.Distance(
            tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0),
            tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0));
        tools[number] = tool;
        if (tool.Diameter > 0) radii[number] = tool.Diameter * 0.5;
        toolNames[tool.Id] = number;
        if (!string.IsNullOrWhiteSpace(tool.Name)) toolNames[tool.Name] = number;
    }

    var workFrame = PlaceFrame(job.Mcs, "audit:r2-mcs");
    var controllerFrame = PlaceFrame(
        job.HasExplicitControllerWorkFrame ? job.ControllerWorkFrame : job.Mcs,
        "audit:r2-controller-or-mcs");
    var program = GCodeParser.Load(ncPath, machine, new GCodeSimulationContext(
        workFrame,
        gauges,
        controllerFrame,
        CoordinateTransforms.BuildPlacement(controllerFrame, workFrame),
        toolNames,
        radii));
    Console.WriteLine($"PROFILE load+parse={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    phaseTimer.Restart();
    var stockAsset = job.Models.First(model =>
        model.Role.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
        model.Role.Contains("blank", StringComparison.OrdinalIgnoreCase));
    var stockMesh = StlMeshReader.Load(job.Resolve(stockAsset.Path));
    var stock = new TripleDexelStock(stockMesh.Bounds, pitch, stockMesh, surfaceCacheEnabled: auditSurface);
    Console.WriteLine($"PROFILE stock-init={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    phaseTimer.Restart();
    var partAsset = job.Models.First(model => model.Role.Contains("part", StringComparison.OrdinalIgnoreCase));
    var target = stock.CreateZTarget(StlMeshReader.Load(job.Resolve(partAsset.Path)));
    Console.WriteLine($"PROFILE target-init={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    if (auditSurface)
    {
        phaseTimer.Restart();
        var initialSurface = stock.BuildDirtySurfaceChunks(target);
        Console.WriteLine(
            $"PROFILE initial-surface={phaseTimer.Elapsed.TotalSeconds:0.000}s; " +
            $"chunks={initialSurface.Count:N0}; models={initialSurface.Sum(chunk => chunk.Meshes.Count):N0}; " +
            $"triangles={initialSurface.Sum(chunk => chunk.TriangleCount):N0}");
    }
    PrintMemory("INITIALIZED");
    var initial = stock.Volume();
    var currentOperation = "PROGRAM START";
    var toolRemoved = new Dictionary<int, TripleDexelVolume>();
    var operationStart = stock.Volume();
    var operationTool = -1;
    var operationTag = 0;
    var kernelCalls = new Dictionary<string, int>(StringComparer.Ordinal);
    var kernelSamples = new Dictionary<string, long>(StringComparer.Ordinal);
    var kernelTicks = new Dictionary<string, long>(StringComparer.Ordinal);
    var blockKernelTimings = new List<(int SourceLine, double Milliseconds)>();

    phaseTimer.Restart();
    var initialComparison = stock.CompareWithTarget(target);
    Console.WriteLine($"PROFILE initial-compare={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    phaseTimer.Restart();
    Console.WriteLine($"AUDIT blocks={program.Blocks.Count}, pitch={pitch:0.###} mm, initial={initial.Mean:0.0} mm3, targetZ={initialComparison.TargetVolume:0.0} mm3");
    foreach (var block in program.Blocks.Where(block => block.SourceLine <= maximumSourceLine))
    {
        var blockStarted = Stopwatch.GetTimestamp();
        var performedCut = false;
        if (block.Raw.Contains("MSG(", StringComparison.OrdinalIgnoreCase))
        {
            PrintOperationSummary();
            currentOperation = block.Raw.Trim();
            operationStart = stock.Volume();
            operationTool = block.Tool ?? -1;
            operationTag++;
        }

        if (!block.Tool.HasValue || !tools.TryGetValue(block.Tool.Value, out var tool) || tool.Diameter <= 0)
            continue;

        var blockRemoved = new TripleDexelVolume();
        if (block.CycleMotion is { } cycle)
        {
            foreach (var phase in cycle.Phases.Where(phase =>
                         phase.Kind is CycleMotionPhaseKind.FeedIn or CycleMotionPhaseKind.FeedRetract))
                Add(Cut(phase.Start, phase.End, tool, block.Tool.Value));
        }
        else if (block.HasMotion && block.Motion != MotionKind.Rapid)
        {
            var states = new List<AxisState> { block.Start };
            if (block.Path is { Samples.Count: > 0 } path) states.AddRange(path.Samples);
            else states.Add(block.End);
            Add(CutPath(states, tool, block.Tool.Value));
        }

        if (blockRemoved.Mean > 0.01)
        {
            toolRemoved.TryGetValue(block.Tool.Value, out var prior);
            toolRemoved[block.Tool.Value] = Sum(prior, blockRemoved);
        }

        if (performedCut)
        {
            var blockElapsed = Stopwatch.GetElapsedTime(blockStarted).TotalMilliseconds;
            blockKernelTimings.Add((block.SourceLine, blockElapsed));
        }

        void Add(TripleDexelCutResult result)
        {
            performedCut = true;
            blockRemoved = Sum(blockRemoved, result.Removed);
        }
    }
    PrintOperationSummary();
    Console.WriteLine($"PROFILE cutting={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    foreach (var category in kernelCalls.Keys.OrderBy(key => key))
        Console.WriteLine(
            $"PROFILE kernel {category}: calls={kernelCalls[category]:N0}; " +
            $"samples={kernelSamples.GetValueOrDefault(category):N0}; " +
            $"time={TimeSpan.FromTicks(kernelTicks.GetValueOrDefault(category)).TotalSeconds:0.000}s");
    if (blockKernelTimings.Count > 0)
    {
        var orderedBlockTimes = blockKernelTimings.Select(item => item.Milliseconds).OrderBy(value => value).ToArray();
        var p95 = orderedBlockTimes[(int)Math.Floor((orderedBlockTimes.Length - 1) * 0.95)];
        var maximum = blockKernelTimings.MaxBy(item => item.Milliseconds);
        Console.WriteLine(
            $"PROFILE block-kernel: count={blockKernelTimings.Count:N0}; p95={p95:0.000}ms; " +
            $"max={maximum.Milliseconds:0.000}ms at source line {maximum.SourceLine}");
    }

    Console.WriteLine("--- BY TOOL ---");
    foreach (var pair in toolRemoved.OrderBy(pair => pair.Key))
    {
        var tool = tools[pair.Key];
        Console.WriteLine($"T{pair.Key:00} {tool.Name,-18} {tool.Subtype,-28} removed={pair.Value.Mean,11:0.0} spread={pair.Value.Spread,8:0.0}");
    }
    phaseTimer.Restart();
    var finalComparison = stock.CompareWithTarget(target);
    Console.WriteLine($"PROFILE final-compare={phaseTimer.Elapsed.TotalSeconds:0.000}s");
    Console.WriteLine($"FINAL mean={stock.Volume().Mean:0.0}; removed={initial.Mean - stock.Volume().Mean:0.0}; gougeZ={finalComparison.MissingTargetVolume:0.0}; excessZ={finalComparison.ExcessStockVolume:0.0}");
    var renderTimer = Stopwatch.StartNew();
    var finalChunks = stock.BuildDirtySurfaceChunks(target);
    Console.WriteLine(
        $"FINAL RENDER chunks={finalChunks.Count}; models={finalChunks.Sum(chunk => chunk.Meshes.Count)}; " +
        $"triangles={finalChunks.Sum(chunk => chunk.TriangleCount):N0}; time={renderTimer.Elapsed.TotalSeconds:0.00}s");
    PrintMemory("FINAL");
    Console.WriteLine($"PROFILE total={auditTimer.Elapsed.TotalSeconds:0.000}s");

    TripleDexelCutResult Cut(AxisState start, AxisState end, JobTool tool, int toolNumber)
    {
        var startPose = Pose(start, gauges[toolNumber]);
        var endPose = Pose(end, gauges[toolNumber]);
        var profile = ToolCuttingProfileFactory.Create(tool);
        if (!profile.IsSupported || profile.Layers.Count == 0)
            return new TripleDexelCutResult(new TripleDexelVolume(), stock.Volume(), false);
        var first = profile.Layers[0];
        var (kernelKind, samples) = ClassifyKernelCall(
            startPose.Tip, endPose.Tip, startPose.Axis, endPose.Axis,
            first.Radius, first.Length);
        var category = profile.Layers.Count > 1
            ? $"layered-{kernelKind}"
            : kernelKind;
        var started = Stopwatch.GetTimestamp();
        var result = stock.ApplyLayeredCutterMove(
            startPose.Tip, endPose.Tip, startPose.Axis, endPose.Axis,
            profile.Layers, false, operationTag, calculateRemainingVolume: false);
        var elapsed = Stopwatch.GetTimestamp() - started;
        kernelCalls[category] = kernelCalls.GetValueOrDefault(category) + 1;
        kernelSamples[category] = kernelSamples.GetValueOrDefault(category) + samples;
        kernelTicks[category] = kernelTicks.GetValueOrDefault(category) +
                                (long)(elapsed * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return result;
    }

    TripleDexelCutResult CutPath(
        IReadOnlyList<AxisState> states,
        JobTool tool,
        int toolNumber)
    {
        var profile = ToolCuttingProfileFactory.Create(tool);
        if (!profile.IsSupported || profile.Layers.Count == 0)
            return new TripleDexelCutResult(new TripleDexelVolume(), new TripleDexelVolume(), false);
        var poses = states.Select(state =>
        {
            var pose = Pose(state, gauges[toolNumber]);
            return new LayeredCutterPose(pose.Tip, pose.Axis);
        }).ToArray();
        var started = Stopwatch.GetTimestamp();
        var result = stock.ApplyLayeredCutterPath(
            poses,
            profile.Layers,
            false,
            operationTag,
            calculateRemainingVolume: false);
        var elapsed = Stopwatch.GetTimestamp() - started;
        const string category = "layered-block-polyline";
        kernelCalls[category] = kernelCalls.GetValueOrDefault(category) + 1;
        kernelSamples[category] = kernelSamples.GetValueOrDefault(category) + Math.Max(1, poses.Length - 1);
        kernelTicks[category] = kernelTicks.GetValueOrDefault(category) +
                                (long)(elapsed * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return result;
    }

    (string Category, int Samples) ClassifyKernelCall(
        Vector3 startTip,
        Vector3 endTip,
        Vector3 startAxis,
        Vector3 endAxis,
        double radius,
        double fluteLength)
    {
        startAxis = Vector3.Normalize(startAxis);
        endAxis = Vector3.Normalize(endAxis);
        var dot = Math.Clamp(Vector3.Dot(startAxis, endAxis), -1f, 1f);
        var absolute = Vector3.Abs(startAxis);
        var principal = absolute.X >= 0.999999f || absolute.Y >= 0.999999f || absolute.Z >= 0.999999f;
        if (dot >= 0.999999f && principal)
        {
            var travel = endTip - startTip;
            var axialTravel = Vector3.Dot(travel, startAxis);
            var transverseTravel = travel - (startAxis * axialTravel);
            if (transverseTravel.LengthSquared() <= 1e-10f)
                return ("principal-plunge", 1);
            if (Math.Abs(axialTravel) <= 1e-6)
                return ("principal-lateral", 1);
        }
        var orientationTravel = fluteLength * Math.Acos(dot);
        var linearTravel = Vector3.Distance(startTip, endTip);
        var sampleStep = Math.Max(pitch * 1.5, Math.Min(radius * 0.5, 5.0));
        var sampleCount = Math.Clamp(
            (int)Math.Ceiling(Math.Max(linearTravel, orientationTravel) / sampleStep),
            1,
            256);
        var stampCount = sampleCount + 1;
        var bucket = stampCount switch
        {
            <= 2 => "02",
            <= 4 => "03-04",
            <= 8 => "05-08",
            <= 16 => "09-16",
            _ => "17+"
        };
        var travelVector = endTip - startTip;
        var axial = Vector3.Dot(travelVector, startAxis);
        var transverseOnly = dot >= 0.999999f && Math.Abs(axial) <= 1e-6;
        return ($"sampled-arbitrary-{bucket}-{(transverseOnly ? "constant-transverse" : "general")}", stampCount);
    }

    (Vector3 Tip, Vector3 Axis) Pose(AxisState state, double gauge)
    {
        var tipWorld = new Vector3((float)state.X, (float)state.Y, (float)(state.Z - gauge));
        var axisWorld = tipWorld + Vector3.UnitZ;
        var neutralTip = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, tipWorld, state.B, state.C);
        var neutralAxis = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, axisWorld, state.B, state.C);
        var localTip = Vector3.Transform(neutralTip, inversePlacement);
        var localAxisPoint = Vector3.Transform(neutralAxis, inversePlacement);
        return (localTip, Vector3.Normalize(localAxisPoint - localTip));
    }

    void PrintOperationSummary()
    {
        if (currentOperation == "PROGRAM START") return;
        var now = stock.Volume();
        var removed = operationStart.Mean - now.Mean;
        if (pitch >= 0.25)
        {
            var comparison = stock.CompareWithTarget(target);
            Console.WriteLine($"T{operationTool:00} removed={removed,10:0.0} remain={now.Mean,10:0.0} gougeZ={comparison.MissingTargetVolume,8:0.0} excessZ={comparison.ExcessStockVolume,9:0.0} | {currentOperation}");
        }
        else
        {
            Console.WriteLine($"T{operationTool:00} removed={removed,10:0.0} remain={now.Mean,10:0.0} | {currentOperation}");
        }
    }

    static void PrintMemory(string phase)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(
            $"MEMORY {phase}: managed={GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB; " +
            $"working={process.WorkingSet64 / (1024.0 * 1024.0):0.0} MB; private={process.PrivateMemorySize64 / (1024.0 * 1024.0):0.0} MB");
    }

    static TripleDexelVolume Sum(TripleDexelVolume left, TripleDexelVolume right) => new(
        left.X + right.X,
        left.Y + right.Y,
        left.Z + right.Z);
}

/// <summary>
/// A job package only guarantees that machineMountCsys and the exported STL
/// geometry share one space when it declares axisConvention "stl-wcs-local".
/// The NX2412 raw export proves the gap for "nx-csys" packages: it declares the
/// mount at Z-100 while its exported fixture base is at STL Z0, and the MCS at
/// Z94.5 while the part top is at Z194.5 - both off by the same 100 mm.  So the
/// declared height cannot be trusted there, and seating the work group on the
/// table face has to reproduce the verified corrected package exactly.
/// </summary>
static void TestSeatingPolicy(string machinePath, string trustedJobPath, string rawJobPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var table = MachineCoordinateResolver.ResolveTableFrame(machine);
    var trusted = JobPackageReader.Load(trustedJobPath);
    var raw = JobPackageReader.Load(rawJobPath);
    if (!trusted.MountFrameSharesStlSpace)
        throw new Exception($"The reference job must declare stl-wcs-local, got '{trusted.MountAxisConvention}'.");
    if (raw.MountFrameSharesStlSpace)
        throw new Exception($"The raw job is expected to be an nx-csys package, got '{raw.MountAxisConvention}'.");

    (float Lowest, Vector3 Mcs) Place(JobPackage job)
    {
        var placement = CoordinateTransforms.BuildPlacement(job.MachineMount, table);
        var lowest = float.PositiveInfinity;
        foreach (var asset in job.Models.Where(x => x.Path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)))
        {
            var file = job.Resolve(asset.Path);
            if (!File.Exists(file)) continue;
            lowest = Math.Min(lowest, TransformBounds(StlMeshReader.Load(file).Bounds, placement).Min.Z);
        }
        return (lowest, Vector3.Transform(job.Mcs.Origin, placement));
    }

    var reference = Place(trusted);
    if (Math.Abs(reference.Lowest) > 0.001)
        throw new Exception($"The stl-wcs-local package must already sit on the table: lowest={reference.Lowest:0.###}.");

    if (Vector3.Distance(reference.Mcs, new Vector3(0, 0, 194.5f)) > 0.01f)
        throw new Exception($"The verified package must place the CAM MCS on the part top: {reference.Mcs}.");

    // The raw nx-csys export of the same setup is inconsistent in two
    // independent ways and both have to be detectable, because no single rigid
    // offset can repair it: the geometry floats 100 mm above the table while the
    // declared MCS sits 100 mm below the exported stock.
    var placedRaw = Place(raw);
    if (Math.Abs(placedRaw.Lowest - 100) > 0.01)
        throw new Exception($"Raw package geometry offset changed: lowest={placedRaw.Lowest:0.###}, expected 100.");
    if (Math.Abs(placedRaw.Mcs.Z - 194.5) > 0.01)
        throw new Exception($"Raw package MCS placement changed: {placedRaw.Mcs}.");
    var seatedMcs = placedRaw.Mcs + table.ZAxis * -placedRaw.Lowest;
    if (Vector3.Distance(seatedMcs, reference.Mcs) < 0.01f)
        throw new Exception("Seating the raw package must NOT be presented as a repair; it moves the MCS off the part top.");
    Console.WriteLine($"      raw nx-csys package: geometry {placedRaw.Lowest:0.###} mm above the table while the declared "
        + $"MCS lands at {placedRaw.Mcs.Z:0.###} - CSYS and STL are in different spaces, so it is reported, not patched");
}

static void TestRealContextNc(string machinePath, string jobPath, string ncPath)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var placement = CoordinateTransforms.BuildPlacement(
        job.MachineMount,
        MachineCoordinateResolver.ResolveTableFrame(machine));
    var hasCseControllerFrame = job.HasExplicitControllerWorkFrame;
    CoordinateFrame PlaceFrame(CoordinateFrame frame, string source) => new(
        frame.Label,
        Vector3.Transform(frame.Origin, placement),
        Vector3.Normalize(Vector3.TransformNormal(frame.XAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.YAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.ZAxis, placement)),
        source);
    var visualWorkFrame = PlaceFrame(job.Mcs, "smoke:trjob-cam-mcs");
    var controllerWorkFrame = PlaceFrame(
        hasCseControllerFrame ? job.ControllerWorkFrame : job.Mcs,
        hasCseControllerFrame ? "smoke:trjob-controller-work-frame" : "smoke:trjob-mcs-fallback");
    var gaugeLengths = new Dictionary<int, double>();
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        gaugeLengths[number] = Vector3.Distance(
            tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0),
            tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0));
    }
    var controllerToVisual = CoordinateTransforms.BuildPlacement(controllerWorkFrame, visualWorkFrame);
    var toolNumbersByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        if (!string.IsNullOrWhiteSpace(tool.Name)) toolNumbersByName[tool.Name.Trim()] = number;
        if (!string.IsNullOrWhiteSpace(tool.Id)) toolNumbersByName[tool.Id.Trim()] = number;
    }
    var visualContext = new GCodeSimulationContext(
        visualWorkFrame, gaugeLengths, controllerWorkFrame, controllerToVisual, toolNumbersByName);
    var controllerContext = new GCodeSimulationContext(controllerWorkFrame, gaugeLengths);
    var contextProgram = GCodeParser.Load(ncPath, machine, visualContext);
    var controllerProgram = GCodeParser.Load(ncPath, machine, controllerContext);
    var plainProgram = GCodeParser.Load(ncPath, machine);
    Console.WriteLine($"      context={contextProgram.MotionCount} motions; plain={plainProgram.MotionCount}; {contextProgram.WarningCount} warnings; {contextProgram.ErrorCount} errors");
    foreach (var block in contextProgram.Blocks.Where(x => x.Warning is not null || x.Error is not null).Take(20))
        Console.WriteLine($"      line {block.SourceLine}: warning={block.Warning}; error={block.Error} | {block.Raw}");
    if (contextProgram.ErrorCount != 0)
        throw new Exception($"Real context parse produced {contextProgram.ErrorCount} errors.");
    var toolChangeIndices = contextProgram.Blocks
        .Select((block, index) => (block, index))
        .Where(item => item.block.IsToolChange)
        .Select(item => item.index)
        .ToArray();
    if (toolChangeIndices.Length < 2)
        throw new Exception($"Repeated real M6 coverage is missing: {toolChangeIndices.Length}.");
    foreach (var toolChangeIndex in toolChangeIndices)
    {
        var start = Math.Max(0, toolChangeIndex - 8);
        if (!contextProgram.Blocks.Skip(start).Take(toolChangeIndex - start)
                .Any(block => block.Raw.Contains("SUPA", StringComparison.OrdinalIgnoreCase)))
            throw new Exception($"M6 block {toolChangeIndex} has no preceding SUPA reference move.");
    }

    var toolChangePlayer = new SimulationPlayer { SpeedFactor = 100 };
    toolChangePlayer.Load(contextProgram);
    toolChangePlayer.Play();
    var displayedToolChanges = new HashSet<int>();
    for (var guard = 0; guard < contextProgram.Blocks.Count * 2 && toolChangePlayer.IsPlaying; guard++)
    {
        toolChangePlayer.Advance(TimeSpan.FromSeconds(30));
        if (toolChangePlayer.CurrentBlock?.IsToolChange == true)
            displayedToolChanges.Add(toolChangePlayer.DisplayedBlockIndex);
    }
    if (displayedToolChanges.Count != toolChangeIndices.Length)
        throw new Exception($"High-speed playback displayed {displayedToolChanges.Count}/{toolChangeIndices.Length} M6 reference poses.");
    Console.WriteLine($"      {displayedToolChanges.Count}/{toolChangeIndices.Length} M6 reference poses remained visible at 100x");
    if (!hasCseControllerFrame)
    {
        if (!job.MountFrameSharesStlSpace)
            throw new Exception("R2 MCS-only job must declare stl-wcs-local.");
        if (contextProgram.MotionCount < 3000)
            throw new Exception($"R2 context motion coverage is too low: {contextProgram.MotionCount}.");
        if (contextProgram.WarningCount > 20)
            throw new Exception($"R2 context warning flood remains: {contextProgram.WarningCount}.");
        var r2FaceCut = contextProgram.Blocks.FirstOrDefault(item =>
            item.Tool == 2 && item.Motion == MotionKind.Linear &&
            item.Raw.Contains("Z0.", StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception("R2 T02 face-cut Z0 block was not found.");
        var r2RenderedTipZ = r2FaceCut.End.Z - gaugeLengths[2];
        if (Math.Abs(r2RenderedTipZ - visualWorkFrame.Origin.Z) > 0.01)
            throw new Exception($"R2 face-cut/MCS mismatch: tip Z={r2RenderedTipZ:0.###}, MCS Z={visualWorkFrame.Origin.Z:0.###}.");
        Console.WriteLine($"      R2 MCS-only contract: {contextProgram.MotionCount} motions; T02 face tip Z={r2RenderedTipZ:0.###}");
        return;
    }
    // Test-23 adds 18 real modal-cycle hole blocks to the previous 4660
    // context motions in this canonical NC corpus.
    if (contextProgram.MotionCount != 4678)
        throw new Exception($"Real context motion count changed: {contextProgram.MotionCount}.");
    var generatedCycles = contextProgram.Blocks.Where(x => x.CycleMotion is not null).ToArray();
    if (generatedCycles.Length != 51)
        throw new Exception($"Canonical NC generated cycle-hole count changed: {generatedCycles.Length}.");
    var malformedCycle = generatedCycles.FirstOrDefault(x =>
        x.CycleMotion!.SourceLine != x.SourceLine ||
        x.CycleMotion.ActivationSourceLine >= x.SourceLine ||
        x.CycleMotion.Phases.FirstOrDefault()?.Kind != CycleMotionPhaseKind.RapidPosition);
    if (malformedCycle is not null)
        throw new Exception($"Real generated cycle lost phase/source mapping at line {malformedCycle.SourceLine}.");
    var realCycle83 = generatedCycles.First(x => x.CycleMotion!.CycleName == "CYCLE83").CycleMotion!;
    var real83Feeds = realCycle83.Phases.Count(x => x.Kind == CycleMotionPhaseKind.FeedIn);
    var real83Retracts = realCycle83.Phases.Count(x => x.Kind == CycleMotionPhaseKind.PeckRetract);
    if (real83Feeds <= 1 || real83Retracts != real83Feeds - 1)
        throw new Exception($"Real CYCLE83 FDEP/FDPR profile changed: feed={real83Feeds}, retract={real83Retracts}.");

    // Source line 50 is T02 face milling at programmed Z0.  The rendered tool
    // tip must coincide with the exported CAM MCS/part top (Z=194.5), not the
    // raw controller $P_ACTFRAME (Z=100).
    var faceCut = contextProgram.Blocks.Single(item => item.SourceLine == 50);
    var faceGauge = gaugeLengths[2];
    var renderedTipZ = faceCut.End.Z - faceGauge;
    if (Math.Abs(renderedTipZ - visualWorkFrame.Origin.Z) > 0.002)
        throw new Exception($"3-axis CAM MCS mismatch at source line 50: tool tip Z={renderedTipZ:0.###}, MCS Z={visualWorkFrame.Origin.Z:0.###}.");
    var part = job.Models.Single(item => item.Role.Equals("part", StringComparison.OrdinalIgnoreCase));
    var partBounds = TransformBounds(StlMeshReader.Load(job.Resolve(part.Path)).Bounds, placement);
    if (Math.Abs(partBounds.Max.Z - renderedTipZ) > 0.002)
        throw new Exception($"T02 crosses the part top: tool tip Z={renderedTipZ:0.###}, part top Z={partBounds.Max.Z:0.###}.");
    Console.WriteLine($"      line 50 T02 tip Z={renderedTipZ:0.###}; CAM MCS/part top Z={partBounds.Max.Z:0.###}");

    // Exact NX CSE TRAORI states captured from the same MCF/controller engine.
    // The golden capture ran without ATC/tool geometry, so compare the rendered
    // tool tip (machine Z minus the real package gauge) to NX $AA_IM Z.
    AssertTraoriGolden(1036, -34.2578463679, -5.3604101608, 90.9173235946, 20.096, 298.33);
    AssertTraoriGolden(1205, -41.5634302392, 5.5160173248, 87.7886912001, 20.096, 1058.83);
    AssertTraoriGolden(1882, -40.1035927285, -7.1094041359, 85.6245245315, 20.096, 3565.33);

    // Alpha 3 Test-18 part-relative gate.  The visual context resolves the whole
    // TRAORI section in the exported CAM MCS frame, so undoing the physical B/C
    // chain on the rendered tool tip has to return the programmed NC point of
    // that very source line, measured from the CAM MCS the part/stock/fixture
    // STLs share.  This is the invariant the operator actually sees: the tool
    // stays on the part in every rotary pose, not only at B=0.
    AssertVisualTcp(1036, -13.314, 13.4, 0.197);
    AssertVisualTcp(1205, -22.299, 14.551, -0.231);
    AssertVisualTcp(1882, -24.952, 8.614, -2.765);

    var controllerByLine = controllerProgram.Blocks.ToDictionary(item => item.SourceLine);
    var workOffsetDelta = visualWorkFrame.Origin - controllerWorkFrame.Origin;
    var calibratedTraoriBlocks = 0;
    foreach (var visualBlock in contextProgram.Blocks.Where(item =>
                 item.SourceLine >= 1021 && item.SourceLine < 2120 && item.HasMotion))
    {
        var controllerBlock = controllerByLine[visualBlock.SourceLine];
        var gauge = visualBlock.Tool.HasValue ? gaugeLengths[visualBlock.Tool.Value] : 0;
        var controllerBlockTip = new Vector3(
            (float)controllerBlock.End.X,
            (float)controllerBlock.End.Y,
            (float)(controllerBlock.End.Z - gauge));
        // The CAM MCS and the NX CSE G54 differ by a work offset that is fixed
        // in the workpiece frame, so it must travel through the same B/C chain
        // as the geometry.  Adding it in world space is only correct at B=0.
        var rotatedWorkOffset =
            MachineCoordinateResolver.TransformWorkpiecePoint(
                machine, workOffsetDelta, visualBlock.End.B, visualBlock.End.C)
            - MachineCoordinateResolver.TransformWorkpiecePoint(
                machine, Vector3.Zero, visualBlock.End.B, visualBlock.End.C);
        var expectedVisualTip = controllerBlockTip + rotatedWorkOffset;
        var actualVisualTip = new Vector3(
            (float)visualBlock.End.X,
            (float)visualBlock.End.Y,
            (float)(visualBlock.End.Z - gauge));
        AssertNear(actualVisualTip, expectedVisualTip, 0.002f);
        calibratedTraoriBlocks++;
    }
    if (calibratedTraoriBlocks < 900)
        throw new Exception($"TRAORI full-section work-offset coverage is too low: {calibratedTraoriBlocks} blocks.");
    Console.WriteLine($"      {calibratedTraoriBlocks} TRAORI motion blocks satisfy the rotating work-offset invariant "
        + $"(CAM MCS - NX CSE G54 = {workOffsetDelta.Length():0.###} mm)");

    // Full-path parity with the user-accepted Test-22-r2 core.  This covers all
    // 3,134 motion blocks executed while TRAORI is active, including the short
    // TRAORI sections after MCALL cycles and the final simultaneous section.
    // It is intentionally an independent aggregate of source line, motion,
    // tool and every start/end axis value; a cycle-state leak changes the hash.
    var traoriFingerprint = ComputeTraoriTrajectoryFingerprint(contextProgram);
    const string acceptedTest22R2TraoriFingerprint =
        "4EB1608033430990A707370ADC84F55C4CDA4084BD4ABBF2E9F30B287806463D";
    if (!traoriFingerprint.Equals(acceptedTest22R2TraoriFingerprint, StringComparison.Ordinal))
        throw new Exception($"Test-22-r2 TRAORI trajectory parity changed: {traoriFingerprint}.");
    Console.WriteLine($"      Test-22-r2 full TRAORI trajectory parity: {traoriFingerprint}");

    // Exact NX CSE golden states from the same no-ATC diagnostic NC.  Gauge
    // cancels on these inherited-Z positioning rows, so the production parser
    // must match X/Y/Z as well as B/C.
    AssertGolden(70, -104, -50.8, 598, 45, 180);
    AssertGolden(71, 17.6776609407, 99, 598, 45, 180);
    AssertGolden(121, 17.6776609407, -104, 598, 30.0000250788, 314.9999273145);
    AssertGolden(122, 75.4452434212, -25.6753386444, 598, 30.0000250788, 314.9999273145);

    void AssertGolden(int sourceLine, double x, double y, double z, double b, double c)
    {
        var block = controllerProgram.Blocks.Single(item => item.SourceLine == sourceLine);
        var end = block.End;
        if (Math.Abs(end.X - x) > 0.002 || Math.Abs(end.Y - y) > 0.002 || Math.Abs(end.Z - z) > 0.002 ||
            Math.Abs(end.B - b) > 0.002 || Math.Abs(NormalizeAngle(end.C - c)) > 0.002)
            throw new Exception($"NX CSE golden mismatch at source line {sourceLine}: {end}");
    }

    void AssertVisualTcp(int sourceLine, double x, double y, double z)
    {
        var block = contextProgram.Blocks.Single(item => item.SourceLine == sourceLine);
        var gauge = block.Tool.HasValue ? gaugeLengths[block.Tool.Value] : 0;
        var tip = new Vector3(
            (float)block.End.X,
            (float)block.End.Y,
            (float)(block.End.Z - gauge));
        var recovered = MachineCoordinateResolver.InverseTransformWorkpiecePoint(
            machine, tip, block.End.B, block.End.C);
        var expected = visualWorkFrame.Origin
            + visualWorkFrame.XAxis * (float)x
            + visualWorkFrame.YAxis * (float)y
            + visualWorkFrame.ZAxis * (float)z;
        if (Vector3.Distance(recovered, expected) > 0.002f)
            throw new Exception(
                $"Visual TCP is not on the part at source line {sourceLine}: recovered={recovered}, expected={expected}, "
                + $"delta={Vector3.Distance(recovered, expected):0.###} mm.");
        Console.WriteLine($"      line {sourceLine} visual TCP follows B/C onto the part: {recovered}");
    }

    void AssertTraoriGolden(int sourceLine, double x, double y, double tipZ, double b, double c)
    {
        var block = controllerProgram.Blocks.Single(item => item.SourceLine == sourceLine);
        var gauge = block.Tool.HasValue ? gaugeLengths[block.Tool.Value] : 0;
        var end = block.End;
        var renderedTipZ = end.Z - gauge;
        if (Math.Abs(end.X - x) > 0.002 || Math.Abs(end.Y - y) > 0.002 || Math.Abs(renderedTipZ - tipZ) > 0.002 ||
            Math.Abs(end.B - b) > 0.002 || Math.Abs(NormalizeAngle(end.C - c)) > 0.002)
            throw new Exception($"NX CSE TRAORI golden mismatch at source line {sourceLine}: axes={end}, tipZ={renderedTipZ:0.######}");
    }

    static double NormalizeAngle(double value)
    {
        value %= 360;
        if (value > 180) value -= 360;
        if (value <= -180) value += 360;
        return value;
    }
}

static string ComputeTraoriTrajectoryFingerprint(GCodeProgram program)
{
    var builder = new StringBuilder();
    var traoriActive = false;
    foreach (var block in program.Blocks)
    {
        if (Regex.IsMatch(block.Raw, @"(?i)(?<![A-Z0-9_])TRAFOOF(?![A-Z0-9_])"))
            traoriActive = false;
        else if (Regex.IsMatch(block.Raw, @"(?i)(?<![A-Z0-9_])TRAORI(?![A-Z0-9_])"))
            traoriActive = true;
        if (!traoriActive || !block.HasMotion) continue;

        object[] values =
        [
            block.SourceLine,
            (int)block.Motion,
            block.Start.X, block.Start.Y, block.Start.Z,
            block.Start.A, block.Start.B, block.Start.C,
            block.End.X, block.End.Y, block.End.Z,
            block.End.A, block.End.B, block.End.C,
            block.Tool ?? -1
        ];
        foreach (var value in values)
        {
            if (value is double or float or decimal)
                builder.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture));
            else
                builder.Append(value);
            builder.Append('|');
        }
        builder.AppendLine();
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
}

static void DumpContext(string machinePath, string jobPath, string ncPath, HashSet<int> lines)
{
    var machine = MachinePackageReader.Load(machinePath);
    var job = JobPackageReader.Load(jobPath);
    var placement = CoordinateTransforms.BuildPlacement(
        job.MachineMount, MachineCoordinateResolver.ResolveTableFrame(machine));
    CoordinateFrame Place(CoordinateFrame frame, string source) => new(
        frame.Label,
        Vector3.Transform(frame.Origin, placement),
        Vector3.Normalize(Vector3.TransformNormal(frame.XAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.YAxis, placement)),
        Vector3.Normalize(Vector3.TransformNormal(frame.ZAxis, placement)),
        source);
    var workFrame = Place(job.Mcs, "dump:cam-mcs");
    var gauges = new Dictionary<int, double>();
    var namesByNumber = new Dictionary<int, string>();
    var numbersByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in job.Tools)
    {
        var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number)) continue;
        var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(job.Resolve(tool.ModelPath))
            ? StlMeshReader.Load(job.Resolve(tool.ModelPath))
            : ParametricToolMeshBuilder.Build(tool);
        gauges[number] = Vector3.Distance(
            tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0),
            tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0));
        namesByNumber[number] = tool.Name;
        if (!string.IsNullOrWhiteSpace(tool.Name)) numbersByName[tool.Name.Trim()] = number;
        if (!string.IsNullOrWhiteSpace(tool.Id)) numbersByName[tool.Id.Trim()] = number;
    }
    var context = new GCodeSimulationContext(workFrame, gauges, null, null, numbersByName);
    var program = GCodeParser.Load(ncPath, machine, context);

    Console.WriteLine($"machine   : {Path.GetFileName(machinePath)}");
    Console.WriteLine($"job       : {Path.GetFileName(jobPath)}  mcs={job.Mcs.Origin}  mount={job.MachineMount.Origin}");
    Console.WriteLine($"nc        : {Path.GetFileName(ncPath)}");
    Console.WriteLine($"parse     : {program.SourceLines.Count} source lines, {program.Blocks.Count} blocks, "
        + $"{program.MotionCount} motions, {program.WarningCount} warnings, {program.ErrorCount} errors");
    foreach (var w in program.Blocks.Where(x => x.Warning is not null).Take(6))
        Console.WriteLine($"warning   : line {w.SourceLine}: {w.Warning}");

    var seatLow = double.PositiveInfinity;
    foreach (var asset in job.Models.Where(x => x.Path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)))
    {
        var file = job.Resolve(asset.Path);
        if (!File.Exists(file)) continue;
        var b = TransformBounds(StlMeshReader.Load(file).Bounds, placement);
        Console.WriteLine($"model     : {asset.Role,-8} z {b.Min.Z,10:0.000} .. {b.Max.Z,10:0.000}");
        seatLow = Math.Min(seatLow, b.Min.Z);
    }
    Console.WriteLine($"seating   : lowest work-group point at table Z {seatLow:0.000}"
        + (seatLow < -0.01 ? $"  -> SINKS {-seatLow:0.000} mm INTO THE TABLE" : "  -> ok"));

    foreach (var line in lines.OrderBy(x => x))
    {
        var block = program.Blocks.FirstOrDefault(x => x.SourceLine == line);
        if (block is null) { Console.WriteLine($"line {line}: no executable block"); continue; }
        var gauge = block.Tool.HasValue && gauges.TryGetValue(block.Tool.Value, out var g) ? g : 0;
        var tip = new Vector3((float)block.End.X, (float)block.End.Y, (float)(block.End.Z - gauge));
        var inWork = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, tip, block.End.B, block.End.C);
        var fromMcs = inWork - workFrame.Origin;
        var toolName = block.Tool.HasValue && namesByNumber.TryGetValue(block.Tool.Value, out var n) ? n : "-";
        Console.WriteLine($"line {line,5}: T{block.Tool?.ToString() ?? "--"} {toolName,-14} gauge={gauge,8:0.000} "
            + $"axes X{block.End.X,10:0.000} Y{block.End.Y,10:0.000} Z{block.End.Z,10:0.000} "
            + $"B{block.End.B,9:0.000} C{block.End.C,10:0.000}");
        Console.WriteLine($"            tip(machine)={tip}  tip(work, from CAM MCS)=({fromMcs.X:0.000}, {fromMcs.Y:0.000}, {fromMcs.Z:0.000})");
        if (block.Path is { } arc)
            Console.WriteLine($"            path: {arc.Samples.Count} ornek, {arc.Length:0.###} mm, {arc.Revolutions} tam tur");
        else if (block.Motion is MotionKind.CircularClockwise or MotionKind.CircularCounterClockwise)
            Console.WriteLine("            path: yok (yay duz cizgi)");
        Console.WriteLine($"            nc: {block.Raw.Trim()}");
    }
}

static Bounds3 TransformBounds(Bounds3 bounds, Matrix4x4 transform)
{
    var corners = new[]
    {
        new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Min.Z), new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Min.Z),
        new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Min.Z), new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Min.Z),
        new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Max.Z), new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Max.Z),
        new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Max.Z), new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Max.Z)
    };
    var min = new Vector3(float.PositiveInfinity);
    var max = new Vector3(float.NegativeInfinity);
    foreach (var corner in corners)
    {
        var point = Vector3.Transform(corner, transform);
        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }
    return new Bounds3(min, max);
}

static MachinePackage FakeMachine()
{
    var axes = new[]
    {
        new AxisDefinition("X1", "Linear", "X_AXIS", "X_JCT", Vector3.UnitX, 0, true, -100, 100, 1000),
        new AxisDefinition("Y1", "Linear", "Y_AXIS", "Y_JCT", Vector3.UnitY, 0, true, -100, 100, 1000),
        new AxisDefinition("Z1", "Linear", "Z_AXIS", "Z_JCT", Vector3.UnitZ, 0, true, -100, 100, 1000),
        new AxisDefinition("B1", "Rotary", "B_AXIS", "B_JCT", Vector3.UnitY, 0, true, -120, 120, 30),
        new AxisDefinition("C1", "Rotary", "C_AXIS", "C_JCT", Vector3.UnitZ, 0, false, 0, 0, 30)
    };
    return new MachinePackage
    {
        SourcePath = "memory.trmac", CacheRoot = ".", PackageSha256 = "test", Schema = "trmac/0.3.6",
        MachineName = "TEST", ControllerFamily = "TEST", RootComponent = "ROOT",
        Components = Array.Empty<MachineComponent>(), Axes = axes, Junctions = Array.Empty<JunctionDefinition>(),
        Geometry = Array.Empty<GeometryAsset>(), CollidableComponents = new HashSet<string>()
    };
}

static MachinePackage FakeBcTableMachine()
{
    var source = FakeMachine();
    return new MachinePackage
    {
        SourcePath = source.SourcePath,
        CacheRoot = source.CacheRoot,
        PackageSha256 = source.PackageSha256,
        Schema = source.Schema,
        MachineName = "GENERIC_BC_TABLE_TEST",
        ControllerFamily = "SINUMERIK ONE",
        RootComponent = source.RootComponent,
        Components = source.Components,
        Axes = source.Axes.Select(axis => axis.Name switch
        {
            "B1" => axis with { Vector = -Vector3.UnitY },
            "C1" => axis with { Vector = -Vector3.UnitZ },
            _ => axis
        }).ToArray(),
        Junctions = source.Junctions,
        Geometry = source.Geometry,
        CollidableComponents = source.CollidableComponents
    };
}

static void AssertNear(Vector3 actual, Vector3 expected, float tolerance)
{
    if (Vector3.Distance(actual, expected) > tolerance)
        throw new Exception($"Expected {expected}; actual {actual}.");
}


static void TestProgressivePath()
{
    var start = new AxisState();
    var arcMid = start with { X = 5, Y = 5 };
    var arcEnd = start with { X = 10 };
    var bottom = arcEnd with { Z = -10 };
    var phases = new[] {
        new CycleMotionPhase(CycleMotionPhaseKind.FeedIn, arcEnd, bottom, 600),
        new CycleMotionPhase(CycleMotionPhaseKind.Dwell, bottom, bottom, 0, 2),
        new CycleMotionPhase(CycleMotionPhaseKind.RapidRetract, bottom, arcEnd, 600)
    };
    var blocks = new[] {
        new GCodeBlock(1,"G2 X10",MotionKind.CircularClockwise,start,arcEnd,600,0,1,null,null,
            new MotionPath(new[]{arcMid,arcEnd},14,0)),
        new GCodeBlock(2,"CYCLE",MotionKind.Linear,arcEnd,arcEnd,600,0,1,null,null,
            CycleMotion:new GeneratedCycleMotion("TEST",2,2,phases,2))
    };
    var program = new GCodeProgram { SourcePath="prefix.nc",Blocks=blocks,SourceLines=Array.Empty<GCodeSourceLine>() };
    var range=new GCodeOperationRange("op",1,"op","program","test","T01",0,1,1,2);
    var path=GCodeOperationPathBuilder.Build(program,new Dictionary<int,double>(),new[]{range},
        (state,gauge)=>new Vector3((float)state.X,(float)state.Y,(float)state.Z),true);
    if(path.CuttingTimings.Length!=3 || path.LinkingTimings.Length!=1) throw new Exception("Lost arc/cycle classifications.");
    var before=GCodePathPrefix.At(path.CuttingTimings,-1,0);
    var arc=GCodePathPrefix.At(path.CuttingTimings,0,.25);
    var boundary=GCodePathPrefix.At(path.CuttingTimings,0,.5);
    var cut=GCodePathPrefix.At(path.CuttingTimings,1,.125);
    var dwell=GCodePathPrefix.At(path.LinkingTimings,1,.5);
    var retract=GCodePathPrefix.At(path.LinkingTimings,1,.875);
    var end=GCodePathPrefix.At(path.CuttingTimings,2,0);
    var reverse=GCodePathPrefix.At(path.CuttingTimings,0,.25);
    if(before!=new GCodePathPrefix(0,0) || arc!=new GCodePathPrefix(0,.5) ||
        boundary!=new GCodePathPrefix(1,0) || cut!=new GCodePathPrefix(2,.5) ||
        dwell!=new GCodePathPrefix(0,0) || retract!=new GCodePathPrefix(0,.5) ||
        end!=new GCodePathPrefix(3,0) || reverse!=arc)
        throw new Exception("Progressive path drew future segments, dwell travel or failed reverse seek.");
}

static void TestOrientedCutterCavities()
{
    var bounds=new Bounds3(new Vector3(-8,-8,-8),new Vector3(8,8,8));
    var expected=new TripleDexelStock(bounds,.3);
    var actual=new TripleDexelStock(bounds,.3);
    // A transverse through-cut leaves multiple intervals and empty rays.
    foreach(var stock in new[]{expected,actual})
        stock.ApplyCylindricalCutterMove(new(-10,0,0),new(-10,0,0),Vector3.UnitX,Vector3.UnitX,4,20,false,3);
    var random=new Random(2715);
    var layers=new[]{new CutterCylinderLayer(0,2.3,5),new CutterCylinderLayer(3,3.7,3)};
    for(var index=0;index<24;index++)
    {
        var tip=new Vector3((float)(random.NextDouble()*30-15),(float)(random.NextDouble()*30-15),(float)(random.NextDouble()*30-15));
        var axis=Vector3.Normalize(new Vector3((float)random.NextDouble()+.1f,(float)random.NextDouble()-.5f,(float)random.NextDouble()-.5f));
        foreach(var layer in layers)
        {
            var origin=tip+axis*(float)layer.AxialOffset;
            expected.ApplyCylindricalCutterMove(origin,origin,axis,axis,layer.Radius,layer.Length,false,4);
        }
        actual.ApplyLayeredCutterMove(tip,tip,axis,axis,layers,false,4);
        // The independent cylinder formula differs by <0.00001 mm3 already in R26; the R26/R27 cavity surface hash is exact.
        var a=actual.Volume();var e=expected.Volume();
        AssertRelative(a.X,e.X,1e-8,"Cavity X"); AssertRelative(a.Y,e.Y,1e-8,"Cavity Y"); AssertRelative(a.Z,e.Z,1e-8,"Cavity Z");
    }
}



