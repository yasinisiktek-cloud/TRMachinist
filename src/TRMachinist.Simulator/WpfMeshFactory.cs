using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

internal static class WpfMeshFactory
{
    private readonly record struct QuantizedPoint(long X, long Y, long Z) : IComparable<QuantizedPoint>
    {
        public int CompareTo(QuantizedPoint other)
        {
            var x = X.CompareTo(other.X);
            if (x != 0) return x;
            var y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }
    }

    private readonly record struct EdgeKey(QuantizedPoint A, QuantizedPoint B);

    private sealed class EdgeInfo
    {
        public required Point3D A { get; init; }
        public required Point3D B { get; init; }
        public required Vector3D FirstNormal { get; init; }
        public Vector3D SecondNormal { get; set; }
        public int FaceCount { get; set; } = 1;
    }

    private sealed class NormalCluster
    {
        public required int VertexIndex { get; init; }
        public required Vector3D Sum { get; set; }
        public required Vector3D Direction { get; set; }
    }

    public static GeometryModel3D Create(
        TriangleMeshData data,
        Color color,
        double opacity = 1,
        bool smoothNormals = true)
    {
        var mesh = PrepareGeometry(data, smoothNormals);
        return Create(mesh, color, opacity);
    }

    public static GeometryModel3D CreateIpwSurface(
        TriangleMeshData data,
        Color color,
        double opacity = 1)
    {
        var mesh = PrepareGeometry(data, smoothNormals: true);
        color.A = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        var material = CreateMaterial(color, opacity, specularStrength: 0.14, specularPower: 24);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    /// <summary>
    /// Performs the expensive STL-to-WPF vertex and crease-normal conversion.
    /// The result is frozen, so callers may prepare it on a worker thread and
    /// safely attach it to the UI scene afterwards.
    /// </summary>
    public static MeshGeometry3D PrepareGeometry(
        TriangleMeshData data,
        bool smoothNormals = true)
    {
        if (smoothNormals)
            return PrepareCreaseAwareIndexedGeometry(data, 42);

        var positions = new Point3DCollection(data.Positions.Length / 3);
        for (var i = 0; i < data.Positions.Length; i += 3)
            positions.Add(new Point3D(data.Positions[i], data.Positions[i + 1], data.Positions[i + 2]));

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = CreateSourceNormals(data),
            TriangleIndices = new Int32Collection(data.Indices)
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>
    /// Welds duplicate STL corners while retaining separate vertices at real
    /// creases. Raw STL and the procedural IPW surface repeat three vertices
    /// per triangle; sending those duplicates to WPF wastes memory, normal
    /// work and GPU bandwidth. NX-style smoothing is retained by clustering
    /// coincident face normals inside the requested angle.
    /// </summary>
    private static MeshGeometry3D PrepareCreaseAwareIndexedGeometry(
        TriangleMeshData data,
        double smoothingAngleDegrees)
    {
        const double quantization = 1000.0; // 0.001 mm
        var threshold = Math.Cos(smoothingAngleDegrees * Math.PI / 180.0);
        var clusters = new Dictionary<QuantizedPoint, List<NormalCluster>>(
            Math.Max(16, data.Positions.Length / 9));
        var positions = new Point3DCollection(Math.Max(4, data.Positions.Length / 6));
        var indices = new Int32Collection(data.Indices.Length);

        for (var triangle = 0; triangle + 2 < data.Indices.Length; triangle += 3)
        {
            var i0 = data.Indices[triangle];
            var i1 = data.Indices[triangle + 1];
            var i2 = data.Indices[triangle + 2];
            var p0 = Point(i0);
            var p1 = Point(i1);
            var p2 = Point(i2);
            var faceNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
            if (faceNormal.LengthSquared > 1e-16) faceNormal.Normalize();
            else faceNormal = new Vector3D(0, 0, 1);

            Append(i0, p0, faceNormal);
            Append(i1, p1, faceNormal);
            Append(i2, p2, faceNormal);
        }

        var normalArray = new Vector3D[positions.Count];
        foreach (var buckets in clusters.Values)
        foreach (var bucket in buckets)
            normalArray[bucket.VertexIndex] = bucket.Direction;

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = new Vector3DCollection(normalArray),
            TriangleIndices = indices
        };
        mesh.Freeze();
        return mesh;

        Point3D Point(int index)
        {
            var offset = index * 3;
            return new Point3D(data.Positions[offset], data.Positions[offset + 1], data.Positions[offset + 2]);
        }

        Vector3D SourceNormal(int index, Vector3D fallback)
        {
            var offset = index * 3;
            if (offset + 2 >= data.Normals.Length) return fallback;
            var normal = new Vector3D(data.Normals[offset], data.Normals[offset + 1], data.Normals[offset + 2]);
            if (normal.LengthSquared <= 1e-16) return fallback;
            normal.Normalize();
            return normal;
        }

        void Append(int sourceIndex, Point3D point, Vector3D faceNormal)
        {
            var normal = SourceNormal(sourceIndex, faceNormal);
            var key = new QuantizedPoint(
                (long)Math.Round(point.X * quantization),
                (long)Math.Round(point.Y * quantization),
                (long)Math.Round(point.Z * quantization));
            if (!clusters.TryGetValue(key, out var buckets))
            {
                buckets = new List<NormalCluster>(2);
                clusters[key] = buckets;
            }

            NormalCluster? selected = null;
            var bestDot = threshold;
            foreach (var bucket in buckets)
            {
                var dot = Vector3D.DotProduct(normal, bucket.Direction);
                if (dot < bestDot) continue;
                selected = bucket;
                bestDot = dot;
            }

            if (selected is null)
            {
                var vertexIndex = positions.Count;
                positions.Add(point);
                selected = new NormalCluster
                {
                    VertexIndex = vertexIndex,
                    Sum = normal,
                    Direction = normal
                };
                buckets.Add(selected);
            }
            else
            {
                selected.Sum += normal;
                if (selected.Sum.LengthSquared > 1e-16)
                {
                    var direction = selected.Sum;
                    direction.Normalize();
                    selected.Direction = direction;
                }
            }
            indices.Add(selected.VertexIndex);
        }
    }

    public static GeometryModel3D Create(
        MeshGeometry3D preparedGeometry,
        Color color,
        double opacity = 1)
    {
        if (!preparedGeometry.IsFrozen)
            throw new ArgumentException("Hazırlanmış WPF mesh dondurulmuş olmalıdır.", nameof(preparedGeometry));
        color.A = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        var material = CreateMaterial(color, opacity);
        return new GeometryModel3D(preparedGeometry, material) { BackMaterial = material };
    }

    private static Vector3DCollection CreateSourceNormals(TriangleMeshData data)
    {
        var normals = new Vector3DCollection(data.Normals.Length / 3);
        for (var i = 0; i < data.Normals.Length; i += 3)
            normals.Add(new Vector3D(data.Normals[i], data.Normals[i + 1], data.Normals[i + 2]));
        return normals;
    }

    public static Material CreateMaterial(Color color, double opacity = 1)
        => CreateMaterial(color, opacity, specularStrength: 0.58, specularPower: 52);

    private static Material CreateMaterial(
        Color color,
        double opacity,
        double specularStrength,
        double specularPower)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        color.A = alpha;
        var diffuseBrush = new SolidColorBrush(color);
        diffuseBrush.Freeze();

        var highlight = Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(alpha * specularStrength), 0, 255),
            242,
            247,
            249);
        var specularBrush = new SolidColorBrush(highlight);
        specularBrush.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(diffuseBrush));
        material.Children.Add(new SpecularMaterial(specularBrush, specularPower));
        material.Freeze();
        return material;
    }

    /// <summary>
    /// Returns the visible diffuse colour without assuming that it is the
    /// material's top-level object. CreateMaterial deliberately returns a
    /// MaterialGroup (diffuse + specular), so opacity changes must recurse into
    /// that group or they collapse every operation colour back to stock blue.
    /// </summary>
    public static Color GetDiffuseColor(Material? material, Color fallback)
    {
        if (material is DiffuseMaterial { Brush: SolidColorBrush brush })
            return brush.Color;
        if (material is MaterialGroup group)
        {
            foreach (var child in group.Children)
            {
                var color = GetDiffuseColor(child, fallback);
                if (color != fallback || child is DiffuseMaterial)
                    return color;
            }
        }
        return fallback;
    }

    private static Vector3DCollection CreateCreaseAwareNormals(
        TriangleMeshData data,
        double smoothingAngleDegrees)
    {
        const double quantization = 1000.0; // 0.001 mm
        var sourceNormals = new Vector3D[data.Positions.Length / 3];
        var adjacent = new Dictionary<QuantizedPoint, List<Vector3D>>(sourceNormals.Length / 2);

        for (var vertex = 0; vertex < sourceNormals.Length; vertex++)
        {
            var offset = vertex * 3;
            var normal = new Vector3D(data.Normals[offset], data.Normals[offset + 1], data.Normals[offset + 2]);
            if (normal.LengthSquared > 1e-16) normal.Normalize();
            sourceNormals[vertex] = normal;

            var point = new Point3D(data.Positions[offset], data.Positions[offset + 1], data.Positions[offset + 2]);
            var key = Quantize(point);
            if (!adjacent.TryGetValue(key, out var list))
            {
                list = new List<Vector3D>(8);
                adjacent[key] = list;
            }
            list.Add(normal);
        }

        var threshold = Math.Cos(smoothingAngleDegrees * Math.PI / 180.0);
        var result = new Vector3DCollection(sourceNormals.Length);
        for (var vertex = 0; vertex < sourceNormals.Length; vertex++)
        {
            var offset = vertex * 3;
            var point = new Point3D(data.Positions[offset], data.Positions[offset + 1], data.Positions[offset + 2]);
            var source = sourceNormals[vertex];
            var sum = new Vector3D();
            foreach (var candidate in adjacent[Quantize(point)])
            {
                if (source.LengthSquared <= 1e-16 ||
                    candidate.LengthSquared <= 1e-16 ||
                    Vector3D.DotProduct(source, candidate) >= threshold)
                    sum += candidate;
            }

            if (sum.LengthSquared <= 1e-16) sum = source.LengthSquared > 1e-16 ? source : new Vector3D(0, 0, 1);
            sum.Normalize();
            result.Add(sum);
        }
        return result;

        static QuantizedPoint Quantize(Point3D point) => new(
            (long)Math.Round(point.X * quantization),
            (long)Math.Round(point.Y * quantization),
            (long)Math.Round(point.Z * quantization));
    }

    /// <summary>
    /// Creates NX/ShopDoc-style feature edges without exposing every STL
    /// triangulation diagonal.  Only boundary edges and edges whose adjacent
    /// face normals exceed the requested crease angle are rendered.
    /// </summary>
    public static LinesVisual3D? CreateFeatureEdges(
        TriangleMeshData data,
        Color color,
        double thickness = 0.7,
        double featureAngleDegrees = 32)
    {
        var points = PrepareFeatureEdgePoints(data, featureAngleDegrees);
        return CreateFeatureEdges(points, color, thickness);
    }

    /// <summary>
    /// Calculates sharp/boundary edge points without creating a Visual3D.
    /// The frozen collection can cross from a background preparation thread
    /// to the WPF dispatcher without blocking the application window.
    /// </summary>
    public static Point3DCollection PrepareFeatureEdgePoints(
        TriangleMeshData data,
        double featureAngleDegrees = 32)
    {
        if (data.Indices.Length < 3)
        {
            var empty = new Point3DCollection();
            empty.Freeze();
            return empty;
        }
        const double quantization = 1000.0; // 0.001 mm
        var edges = new Dictionary<EdgeKey, EdgeInfo>(data.Indices.Length);

        for (var i = 0; i + 2 < data.Indices.Length; i += 3)
        {
            var p0 = Point(data.Indices[i]);
            var p1 = Point(data.Indices[i + 1]);
            var p2 = Point(data.Indices[i + 2]);
            var normal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
            if (normal.LengthSquared < 1e-16) continue;
            normal.Normalize();
            AddEdge(p0, p1, normal);
            AddEdge(p1, p2, normal);
            AddEdge(p2, p0, normal);
        }

        var threshold = Math.Cos(featureAngleDegrees * Math.PI / 180.0);
        var points = new Point3DCollection();
        foreach (var edge in edges.Values)
        {
            var isFeature = edge.FaceCount == 1 ||
                            Vector3D.DotProduct(edge.FirstNormal, edge.SecondNormal) < threshold;
            if (!isFeature) continue;
            points.Add(edge.A);
            points.Add(edge.B);
        }
        points.Freeze();
        return points;

        Point3D Point(int index)
        {
            var offset = index * 3;
            return new Point3D(data.Positions[offset], data.Positions[offset + 1], data.Positions[offset + 2]);
        }

        void AddEdge(Point3D a, Point3D b, Vector3D normal)
        {
            var qa = Quantize(a);
            var qb = Quantize(b);
            var key = qa.CompareTo(qb) <= 0 ? new EdgeKey(qa, qb) : new EdgeKey(qb, qa);
            if (edges.TryGetValue(key, out var existing))
            {
                if (existing.FaceCount == 1) existing.SecondNormal = normal;
                existing.FaceCount++;
                return;
            }
            edges[key] = new EdgeInfo { A = a, B = b, FirstNormal = normal };
        }

        static QuantizedPoint Quantize(Point3D point) => new(
            (long)Math.Round(point.X * quantization),
            (long)Math.Round(point.Y * quantization),
            (long)Math.Round(point.Z * quantization));
    }

    public static LinesVisual3D? CreateFeatureEdges(
        Point3DCollection preparedPoints,
        Color color,
        double thickness = 0.7)
    {
        if (preparedPoints.Count == 0) return null;
        if (!preparedPoints.IsFrozen)
            throw new ArgumentException("Hazırlanmış kenar noktaları dondurulmuş olmalıdır.", nameof(preparedPoints));
        return new LinesVisual3D
        {
            Color = color,
            Thickness = thickness,
            Points = preparedPoints
        };
    }

    public static Color ParseColor(string text, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(text); }
        catch { return fallback; }
    }
}
