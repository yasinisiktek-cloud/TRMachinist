using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using TRMachinist.Core;
using DxMeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using DxLineGeometry3D = HelixToolkit.SharpDX.LineGeometry3D;
using MediaColor = System.Windows.Media.Color;
using MathColor4 = HelixToolkit.Maths.Color4;
using WpfLinesVisual3D = HelixToolkit.Wpf.LinesVisual3D;
using WpfGeometryModel3D = System.Windows.Media.Media3D.GeometryModel3D;
using WpfMaterial = System.Windows.Media.Media3D.Material;
using WpfMeshGeometry3D = System.Windows.Media.Media3D.MeshGeometry3D;

namespace TRMachinist.Simulator;

/// <summary>
/// Transitional Test9 renderer. The established WPF scene graph remains the
/// kinematic source of truth, but all visible geometry is mirrored into one
/// DirectX 11 depth scene. IPW chunks never become WPF Model3D objects: their
/// regional buffers live directly in the GPU renderer.
/// </summary>
internal sealed partial class GpuSceneMirror : IDisposable
{
    private sealed record SourceGeometry(
        WpfGeometryModel3D Geometry,
        IReadOnlyList<Model3D> ModelPath,
        IReadOnlyList<ModelVisual3D> VisualPath);

    private sealed class StaticEntry
    {
        public required SourceGeometry Source { get; init; }
        public required MeshGeometryModel3D Model { get; init; }
        public MediaColor LastColor { get; set; }
        public Matrix3D LastTransform { get; set; }
    }

    private sealed record SourceLines(
        WpfLinesVisual3D Lines,
        IReadOnlyList<ModelVisual3D> VisualPath);

    private sealed class StaticLineEntry
    {
        public required SourceLines Source { get; init; }
        public required LineGeometryModel3D Model { get; init; }
        public Matrix3D LastTransform { get; set; }
    }

    private readonly HelixToolkit.Wpf.HelixViewport3D _source;
    private readonly Viewport3DX _viewport;
    private readonly GroupModel3D _root;
    private readonly GroupModel3D _staticRoot = new();
    private readonly GroupModel3D _lineRoot = new();
    private readonly GroupModel3D _operationPathRoot = new();
    private readonly GroupModel3D _ipwRoot = new();
    private readonly List<StaticEntry> _staticEntries = new();
    private readonly List<StaticLineEntry> _staticLineEntries = new();
    private bool _disposed;
    private Matrix3D _ipwTransform = Matrix3D.Identity;
    private double _ipwOpacity = 1.0;
    private bool _ipwVisible = true;
    private bool _operationPathVisible;
    private bool _updatingDepthRange;

    public GpuSceneMirror(
        HelixToolkit.Wpf.HelixViewport3D source,
        Viewport3DX viewport,
        GroupModel3D root)
    {
        _source = source;
        _viewport = viewport;
        _root = root;
        _root.Children.Add(_staticRoot);
        _root.Children.Add(_lineRoot);
        _root.Children.Add(_operationPathRoot);
        _root.Children.Add(_ipwRoot);
    }

    public int StaticMeshCount => _staticEntries.Count;
    public int StaticTriangleCount => _staticEntries.Sum(entry =>
        (entry.Model.Geometry as DxMeshGeometry3D)?.Indices?.Count / 3 ?? 0);
    public int StaticLineCount => _staticLineEntries.Sum(entry =>
        (entry.Model.Geometry as DxLineGeometry3D)?.Indices?.Count / 2 ?? 0);
    public void SynchronizeStaticScene(bool forceRebuild = false)
    {
        if (_disposed) return;
        if (forceRebuild || _staticEntries.Count == 0)
            RebuildStaticScene(CollectSourceGeometry(), CollectSourceLines());

        foreach (var entry in _staticEntries)
        {
            var transform = ComposeWorldTransform(entry.Source);
            if (transform != entry.LastTransform)
            {
                entry.LastTransform = transform;
                entry.Model.Transform = new MatrixTransform3D(transform);
            }
            var color = DiffuseColor(entry.Source.Geometry.Material);
            if (color != entry.LastColor)
            {
                entry.LastColor = color;
                entry.Model.Material = CreateMaterial(color, glossy: false);
                entry.Model.IsTransparent = color.A < 255;
            }
        }
        foreach (var entry in _staticLineEntries)
        {
            var transform = ComposeWorldTransform(entry.Source.VisualPath);
            if (transform == entry.LastTransform) continue;
            entry.LastTransform = transform;
            entry.Model.Transform = new MatrixTransform3D(transform);
        }
        if (forceRebuild) UpdateCameraDepthRange();
    }

    public void SetIpwTransform(Matrix3D transform)
    {
        if (transform == _ipwTransform) return;
        _ipwTransform = transform;
        _ipwRoot.Transform = new MatrixTransform3D(transform);
    }

    public void SetOperationPathTransform(Matrix3D transform)
    {
        _operationPathRoot.Transform = new MatrixTransform3D(transform);
    }

    public void SetOperationPath(GCodeOperationPathPreview data) => CreateOperationPath(data);

    public void SetOperationPathVisible(bool visible)
    {
        _operationPathVisible = visible;
        _operationPathRoot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _viewport.InvalidateRender();
    }

    public void ClearOperationPath()
    {
        _operationPathVisible = false;
        DisposeChildren(_operationPathRoot);
        _pathGroups.Clear();
        _viewport.InvalidateRender();
    }
    public void SnapNearestOrthographicView()
    {
        if (_viewport.Camera is not HelixToolkit.Wpf.SharpDX.OrthographicCamera camera)
            return;
        var currentLook = camera.LookDirection;
        if (currentLook.LengthSquared < 1e-12) return;
        var distance = currentLook.Length;
        var target = camera.Position + currentLook;
        currentLook.Normalize();

        Vector3D snappedLook;
        var absoluteX = Math.Abs(currentLook.X);
        var absoluteY = Math.Abs(currentLook.Y);
        var absoluteZ = Math.Abs(currentLook.Z);
        if (absoluteX >= absoluteY && absoluteX >= absoluteZ)
            snappedLook = new Vector3D(Math.Sign(currentLook.X), 0, 0);
        else if (absoluteY >= absoluteZ)
            snappedLook = new Vector3D(0, Math.Sign(currentLook.Y), 0);
        else
            snappedLook = new Vector3D(0, 0, Math.Sign(currentLook.Z));

        var currentUp = camera.UpDirection;
        currentUp -= snappedLook * Vector3D.DotProduct(currentUp, snappedLook);
        Vector3D snappedUp;
        if (Math.Abs(snappedLook.X) > 0.5)
            snappedUp = Math.Abs(currentUp.Z) >= Math.Abs(currentUp.Y)
                ? new Vector3D(0, 0, SignedUnit(currentUp.Z))
                : new Vector3D(0, SignedUnit(currentUp.Y), 0);
        else if (Math.Abs(snappedLook.Y) > 0.5)
            snappedUp = Math.Abs(currentUp.Z) >= Math.Abs(currentUp.X)
                ? new Vector3D(0, 0, SignedUnit(currentUp.Z))
                : new Vector3D(SignedUnit(currentUp.X), 0, 0);
        else
            snappedUp = Math.Abs(currentUp.Y) >= Math.Abs(currentUp.X)
                ? new Vector3D(0, SignedUnit(currentUp.Y), 0)
                : new Vector3D(SignedUnit(currentUp.X), 0, 0);

        _viewport.StopSpin();
        var scaledLook = snappedLook * Math.Max(distance, 1.0);
        camera.Position = target - scaledLook;
        camera.LookDirection = scaledLook;
        camera.UpDirection = snappedUp;
        UpdateCameraDepthRange();
        _viewport.InvalidateRender();

        static double SignedUnit(double value) => value < 0 ? -1 : 1;
    }

    public void ZoomExtents(double animationMilliseconds = 250)
    {
        // Viewport3DX.ZoomExtents can run before a newly attached scene graph has
        // produced its first render bounds.  That leaves the camera looking at
        // the old empty scene.  Fit from the source meshes instead; this is also
        // deterministic on slower PCs and independent of render timing.
        if (!TryGetStaticBounds(out var bounds))
        {
            _viewport.ZoomExtents(animationMilliseconds);
            return;
        }

        var center = new Point3D(
            bounds.X + bounds.SizeX * 0.5,
            bounds.Y + bounds.SizeY * 0.5,
            bounds.Z + bounds.SizeZ * 0.5);
        var radius = Math.Max(1.0, Math.Sqrt(
            bounds.SizeX * bounds.SizeX +
            bounds.SizeY * bounds.SizeY +
            bounds.SizeZ * bounds.SizeZ) * 0.5);
        var direction = new Vector3D(1.0, -1.0, 0.72);
        direction.Normalize();

        if (_viewport.Camera is HelixToolkit.Wpf.SharpDX.OrthographicCamera orthographic)
        {
            // Fit the world AABB in the active isometric view without bringing
            // perspective back through Viewport3DX.ZoomExtents.  Orthographic
            // width is derived from the projected spans and viewport aspect.
            var look = -direction;
            var up = new Vector3D(0, 0, 1);
            var right = Vector3D.CrossProduct(look, up);
            right.Normalize();
            up = Vector3D.CrossProduct(right, look);
            up.Normalize();

            var halfHorizontal = 0.0;
            var halfVertical = 0.0;
            foreach (var x in new[] { bounds.X, bounds.X + bounds.SizeX })
            foreach (var y in new[] { bounds.Y, bounds.Y + bounds.SizeY })
            foreach (var z in new[] { bounds.Z, bounds.Z + bounds.SizeZ })
            {
                var offset = new Point3D(x, y, z) - center;
                halfHorizontal = Math.Max(halfHorizontal, Math.Abs(Vector3D.DotProduct(offset, right)));
                halfVertical = Math.Max(halfVertical, Math.Abs(Vector3D.DotProduct(offset, up)));
            }

            var aspect = Math.Max(_viewport.ActualWidth, 1.0) / Math.Max(_viewport.ActualHeight, 1.0);
            var fittedWidth = Math.Max(halfHorizontal * 2.0, halfVertical * 2.0 * aspect) * 1.18;
            var distance = Math.Max(radius * 3.0, 100.0);
            orthographic.Position = center + direction * distance;
            orthographic.LookDirection = center - orthographic.Position;
            orthographic.UpDirection = new Vector3D(0, 0, 1);
            orthographic.Width = Math.Max(fittedWidth, 1.0);
        }
        else if (_viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
        {
            var halfFov = Math.Max(5.0, camera.FieldOfView * 0.5) * Math.PI / 180.0;
            var distance = radius / Math.Tan(halfFov) * 1.18;
            camera.Position = center + direction * distance;
            camera.LookDirection = center - camera.Position;
            camera.UpDirection = new Vector3D(0, 0, 1);
            camera.NearPlaneDistance = Math.Max(0.01, distance - radius * 2.2);
            camera.FarPlaneDistance = Math.Max(distance + radius * 3.0, camera.NearPlaneDistance + 1000.0);
        }
        else
        {
            _viewport.ZoomExtents(animationMilliseconds);
        }
        UpdateCameraDepthRange();
        _viewport.InvalidateRender();
    }

    /// <summary>
    /// Keeps the DirectX depth interval tight around the actual machine instead
    /// of spending depth-buffer precision on empty space from 0.01 mm to the
    /// far side of the whole world.  This is especially important in the
    /// orthographic close view where nearly coincident machine shells otherwise
    /// alternate as large black/grey triangles.
    /// </summary>
    public void UpdateCameraDepthRange()
    {
        if (_updatingDepthRange ||
            _viewport.Camera is not HelixToolkit.Wpf.SharpDX.OrthographicCamera camera) return;
        TryGetStaticBounds(out var bounds);
        // IPW has its own render root. Hiding the fixture/reference CAD must
        // not narrow the camera's depth interval around the retracted tool
        // and clip away otherwise visible live stock.
        if (_ipwVisible)
            foreach (var batch in _ipwBatches.Values)
            {
                if (batch.Model is null || batch.Geometry.Positions is not { Count: > 0 }) continue;
                var box = batch.Geometry.Bound;
                var local = new Rect3D(box.Minimum.X, box.Minimum.Y, box.Minimum.Z,
                    Math.Max(0, box.Maximum.X - box.Minimum.X),
                    Math.Max(0, box.Maximum.Y - box.Minimum.Y),
                    Math.Max(0, box.Maximum.Z - box.Minimum.Z));
                foreach (var corner in BoundsCorners(local))
                    bounds.Union(_ipwTransform.Transform(corner));
            }
        if (bounds.IsEmpty) return;

        var look = camera.LookDirection;
        if (look.LengthSquared < 1e-12) return;
        look.Normalize();
        // A bounding sphere gives one conservative orthographic depth interval
        // for every orbit direction. Width-only wheel zoom and table-centred
        // orbit no longer rewrite Near/Far on every input message, avoiding the
        // camera/render invalidation loop that caused stutter during live IPW.
        var center = new Point3D(
            bounds.X + bounds.SizeX * 0.5,
            bounds.Y + bounds.SizeY * 0.5,
            bounds.Z + bounds.SizeZ * 0.5);
        var radius = Math.Max(1.0, 0.5 * Math.Sqrt(
            bounds.SizeX * bounds.SizeX +
            bounds.SizeY * bounds.SizeY +
            bounds.SizeZ * bounds.SizeZ));
        var centerDepth = Vector3D.DotProduct(center - camera.Position, look);
        if (!double.IsFinite(centerDepth) || !double.IsFinite(radius)) return;
        var margin = Math.Max(10.0, radius * 0.08);
        var near = Math.Max(0.1, centerDepth - radius - margin);
        var far = Math.Max(near + 10.0, centerDepth + radius + margin);
        if (Math.Abs(camera.NearPlaneDistance - near) < 0.01 &&
            Math.Abs(camera.FarPlaneDistance - far) < 0.01) return;

        _updatingDepthRange = true;
        try
        {
            camera.NearPlaneDistance = near;
            camera.FarPlaneDistance = far;
        }
        finally
        {
            _updatingDepthRange = false;
        }
    }

    public string DiagnosticSummary()
    {
        if (!TryGetStaticBounds(out var bounds))
            return $"GPU: {StaticMeshCount} mesh / {StaticTriangleCount:N0} üçgen / {StaticLineCount:N0} kenar / sınır yok";
        return $"GPU: {StaticMeshCount} mesh / {StaticTriangleCount:N0} üçgen / {StaticLineCount:N0} kenar / " +
               $"{bounds.SizeX:0.#}×{bounds.SizeY:0.#}×{bounds.SizeZ:0.#} mm";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeIpwBatches();
        DisposeChildren(_staticRoot);
        DisposeChildren(_lineRoot);
        DisposeChildren(_operationPathRoot);
        _pathGroups.Clear();
        _ipwRoot.Children.Clear();
        _root.Children.Clear();
        _staticEntries.Clear();
        _staticLineEntries.Clear();
        _ipwEntries.Clear();
        _chunkTags.Clear();
    }

    private IReadOnlyList<SourceGeometry> CollectSourceGeometry()
    {
        var result = new List<SourceGeometry>();
        foreach (var visual in _source.Children.OfType<ModelVisual3D>())
            VisitVisual(visual, Array.Empty<ModelVisual3D>(), result);
        return result;
    }

    private IReadOnlyList<SourceLines> CollectSourceLines()
    {
        var result = new List<SourceLines>();
        foreach (var visual in _source.Children.OfType<ModelVisual3D>())
            VisitLines(visual, Array.Empty<ModelVisual3D>(), result);
        return result;
    }

    private static void VisitLines(
        ModelVisual3D visual,
        IReadOnlyList<ModelVisual3D> ancestors,
        ICollection<SourceLines> output)
    {
        var visualPath = Append(ancestors, visual);
        if (visual is WpfLinesVisual3D { Points.Count: >= 2 } lines)
            output.Add(new SourceLines(lines, visualPath));
        foreach (var child in visual.Children.OfType<ModelVisual3D>())
            VisitLines(child, visualPath, output);
    }

    private static void VisitVisual(
        ModelVisual3D visual,
        IReadOnlyList<ModelVisual3D> ancestors,
        ICollection<SourceGeometry> output)
    {
        // MachineSceneController's real component/job nodes are plain
        // ModelVisual3D instances. Helix LinesVisual3D overlays inherit from
        // ModelVisual3D too, but internally turn every feature edge into more
        // triangle geometry. Mirroring those doubled both draw calls and
        // triangles (16 -> 32 meshes on U630) and produced the dark, noisy
        // Test9 preview. DirectX shades the actual solids directly, so skip all
        // specialized overlay visuals here.
        if (visual.GetType() != typeof(ModelVisual3D)) return;
        var visualPath = Append(ancestors, visual);
        if (visual.Content is not null)
            VisitModel(visual.Content, Array.Empty<Model3D>(), visualPath, output);
        foreach (var child in visual.Children.OfType<ModelVisual3D>())
            VisitVisual(child, visualPath, output);
    }

    private static void VisitModel(
        Model3D model,
        IReadOnlyList<Model3D> ancestors,
        IReadOnlyList<ModelVisual3D> visualPath,
        ICollection<SourceGeometry> output)
    {
        var modelPath = Append(ancestors, model);
        if (model is WpfGeometryModel3D geometry && geometry.Geometry is WpfMeshGeometry3D mesh &&
            mesh.Positions.Count > 0 && mesh.TriangleIndices.Count > 0)
        {
            output.Add(new SourceGeometry(geometry, modelPath, visualPath));
            return;
        }
        if (model is not Model3DGroup group) return;
        foreach (var child in group.Children)
            VisitModel(child, modelPath, visualPath, output);
    }

    private void RebuildStaticScene(
        IReadOnlyList<SourceGeometry> sources,
        IReadOnlyList<SourceLines> lines)
    {
        DisposeChildren(_staticRoot);
        DisposeChildren(_lineRoot);
        _staticEntries.Clear();
        _staticLineEntries.Clear();
        foreach (var source in sources)
        {
            var wpf = (WpfMeshGeometry3D)source.Geometry.Geometry;
            var color = DiffuseColor(source.Geometry.Material);
            var transform = ComposeWorldTransform(source);
            var model = new MeshGeometryModel3D
            {
                Geometry = ConvertGeometry(wpf),
                Material = CreateMaterial(color, glossy: false),
                Transform = new MatrixTransform3D(transform),
                IsHitTestVisible = false,
                IsTransparent = color.A < 255,
                // NX renders the closed machine solids as outward-facing
                // surfaces. Rendering both sides with one-sided Phong normals
                // exposed the unlit backs of STL triangle fans as large black
                // wedges at close zoom. Back-face culling matches NX and also
                // removes coincident opposite shells without depth hacks.
                CullMode = SharpDX.Direct3D11.CullMode.Back
            };
            _staticRoot.Children.Add(model);
            _staticEntries.Add(new StaticEntry
            {
                Source = source,
                Model = model,
                LastColor = color,
                LastTransform = transform
            });
        }
        foreach (var source in lines)
        {
            var geometry = ConvertLines(source.Lines.Points);
            if (geometry.Indices is null || geometry.Indices.Count == 0) continue;
            var transform = ComposeWorldTransform(source.VisualPath);
            var model = new LineGeometryModel3D
            {
                Geometry = geometry,
                Color = source.Lines.Color,
                Thickness = Math.Clamp(source.Lines.Thickness, 0.65, 1.15),
                Smoothness = 1.0,
                DepthBias = -10,
                SlopeScaledDepthBias = -1.0,
                Transform = new MatrixTransform3D(transform),
                IsHitTestVisible = false
            };
            _lineRoot.Children.Add(model);
            _staticLineEntries.Add(new StaticLineEntry
            {
                Source = source,
                Model = model,
                LastTransform = transform
            });
        }
        _viewport.InvalidateSceneGraph();
    }

    private MeshGeometryModel3D CreateIpwModel(DxMeshGeometry3D geometry, MediaColor color) => new()
    {
        Geometry = geometry,
        Material = CreateMaterial(color, glossy: true),
        IsHitTestVisible = false,
        IsTransparent = color.A < 255,
        // TripleDexelSurfaceMesher emits outward, consistently wound closed
        // surfaces. Drawing their hidden back faces doubles the raster load
        // during orbit without adding a visible pixel.
        CullMode = SharpDX.Direct3D11.CullMode.Back,
        EnableViewFrustumCheck = true
    };

    private static DxMeshGeometry3D ConvertGeometry(WpfMeshGeometry3D mesh)
    {
        var positions = new Vector3Collection(mesh.Positions.Count);
        foreach (var point in mesh.Positions)
            positions.Add(new Vector3((float)point.X, (float)point.Y, (float)point.Z));
        var normals = new Vector3Collection(mesh.Positions.Count);
        if (mesh.Normals.Count == mesh.Positions.Count)
        {
            foreach (var normal in mesh.Normals)
                normals.Add(Vector3.Normalize(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z)));
        }
        else
        {
            for (var index = 0; index < mesh.Positions.Count; index++) normals.Add(Vector3.UnitZ);
        }
        return new DxMeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            Indices = new IntCollection(mesh.TriangleIndices),
            IsDynamic = false
        };
    }

    private static DxLineGeometry3D ConvertLines(Point3DCollection points)
    {
        var positions = new Vector3Collection(points.Count);
        var indices = new IntCollection(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            positions.Add(new Vector3((float)point.X, (float)point.Y, (float)point.Z));
            indices.Add(index);
        }
        return new DxLineGeometry3D
        {
            Positions = positions,
            Indices = indices,
            IsDynamic = false
        };
    }

    private static Vector3Collection Positions(TriangleMeshData mesh)
    {
        var result = new Vector3Collection(mesh.Positions.Length / 3);
        for (var index = 0; index + 2 < mesh.Positions.Length; index += 3)
            result.Add(new Vector3(mesh.Positions[index], mesh.Positions[index + 1], mesh.Positions[index + 2]));
        return result;
    }

    private static Vector3Collection Normals(TriangleMeshData mesh)
    {
        var result = new Vector3Collection(mesh.Positions.Length / 3);
        for (var index = 0; index + 2 < mesh.Positions.Length; index += 3)
        {
            var normal = mesh.Normals.Length > index + 2
                ? new Vector3(mesh.Normals[index], mesh.Normals[index + 1], mesh.Normals[index + 2])
                : Vector3.UnitZ;
            result.Add(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ);
        }
        return result;
    }

    private static Matrix3D ComposeWorldTransform(SourceGeometry source)
    {
        var result = Matrix3D.Identity;
        for (var index = source.ModelPath.Count - 1; index >= 0; index--)
            result.Append(source.ModelPath[index].Transform.Value);
        for (var index = source.VisualPath.Count - 1; index >= 0; index--)
            result.Append(source.VisualPath[index].Transform.Value);
        return result;
    }

    private static Matrix3D ComposeWorldTransform(IReadOnlyList<ModelVisual3D> visualPath)
    {
        var result = Matrix3D.Identity;
        for (var index = visualPath.Count - 1; index >= 0; index--)
            result.Append(visualPath[index].Transform.Value);
        return result;
    }

    private bool TryGetStaticBounds(out Rect3D bounds)
    {
        var found = false;
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        foreach (var entry in _staticEntries)
        {
            if (entry.Source.Geometry.Geometry is not WpfMeshGeometry3D mesh) continue;
            var transform = ComposeWorldTransform(entry.Source);
            var localBounds = mesh.Bounds;
            if (localBounds.IsEmpty) continue;
            foreach (var local in BoundsCorners(localBounds))
            {
                var point = transform.Transform(local);
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                    continue;
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                minZ = Math.Min(minZ, point.Z);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
                maxZ = Math.Max(maxZ, point.Z);
                found = true;
            }
        }

        bounds = found
            ? new Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ)
            : Rect3D.Empty;
        return found;
    }

    private static IEnumerable<Point3D> BoundsCorners(Rect3D bounds)
    {
        foreach (var x in new[] { bounds.X, bounds.X + bounds.SizeX })
        foreach (var y in new[] { bounds.Y, bounds.Y + bounds.SizeY })
        foreach (var z in new[] { bounds.Z, bounds.Z + bounds.SizeZ })
            yield return new Point3D(x, y, z);
    }

    private static PhongMaterial CreateMaterial(MediaColor color, bool glossy)
    {
        var alpha = color.A / 255f;
        return new PhongMaterial
        {
            DiffuseColor = new MathColor4(color.R / 255f, color.G / 255f, color.B / 255f, alpha),
            AmbientColor = new MathColor4(0.07f, 0.07f, 0.07f, alpha),
            SpecularColor = glossy
                ? new MathColor4(0.34f, 0.34f, 0.34f, alpha)
                : new MathColor4(0.16f, 0.16f, 0.16f, alpha),
            ReflectiveColor = glossy
                ? new MathColor4(0.07f, 0.07f, 0.07f, alpha)
                : new MathColor4(0.025f, 0.025f, 0.025f, alpha),
            SpecularShininess = glossy ? 72 : 36,
            EnableFlatShading = false
        };
    }

    private static MediaColor DiffuseColor(WpfMaterial? material)
    {
        if (material is System.Windows.Media.Media3D.DiffuseMaterial { Brush: SolidColorBrush brush }) return brush.Color;
        if (material is MaterialGroup group)
        {
            foreach (var child in group.Children)
            {
                var color = DiffuseColor(child);
                if (color.A > 0) return color;
            }
        }
        return Colors.LightGray;
    }

    private static MediaColor Alpha4SurfaceColor(int tag, double opacity)
    {
        MediaColor color;
        if (tag < 0) color = MediaColor.FromRgb(211, 58, 58);
        else if (tag == 0) color = MediaColor.FromRgb(81, 164, 184);
        else
        {
            var palette = new[]
            {
                MediaColor.FromRgb(208, 164, 61), MediaColor.FromRgb(81, 153, 111),
                MediaColor.FromRgb(128, 112, 173), MediaColor.FromRgb(207, 123, 78),
                MediaColor.FromRgb(61, 146, 138), MediaColor.FromRgb(87, 106, 159),
                MediaColor.FromRgb(166, 171, 74), MediaColor.FromRgb(185, 78, 116)
            };
            color = palette[(tag - 1) % palette.Length];
        }
        return MediaColor.FromArgb((byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255), color.R, color.G, color.B);
    }

    private static int Capacity(int count)
    {
        if (count <= 0) return 1;
        var value = 1;
        while (value < count && value < (1 << 30)) value <<= 1;
        return value;
    }

    private static IReadOnlyList<T> Append<T>(IReadOnlyList<T> source, T value)
    {
        var result = new T[source.Count + 1];
        for (var index = 0; index < source.Count; index++) result[index] = source[index];
        result[^1] = value;
        return result;
    }
}

