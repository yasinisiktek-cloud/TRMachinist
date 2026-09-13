using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// Immutable triangle BVH for rigid machine/fixture geometry. Bounds only
/// reject candidates: a positive result requires surface distance or solid
/// containment. Build once per loaded mesh; instances cache transformed nodes.
/// </summary>
public sealed class MeshCollisionGeometry
{
    private readonly Triangle[] _triangles;
    private readonly Node[] _nodes;
    private readonly Component[] _components;
    public int TriangleCount => _triangles.Length;
    public int ClosedComponentCount => _components.Count(c => c.Closed);
    public Bounds3 Bounds => _nodes[0].Bounds;
    // Numerical contact tolerance, independent of the user's safety distance.
    public const double ContactToleranceMm = 0.000001;

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Component)
    {
        public Bounds3 Bounds => new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));
        public Vector3 Center => (A + B + C) / 3;
    }
    private readonly record struct Node(Bounds3 Bounds, int Start, int Count, int Left, int Right);
    private readonly record struct Component(Bounds3 Bounds, Vector3 Sample, bool Closed, int Orientation);
    private readonly record struct WorldTriangle(D A, D B, D C, Bounds3 Bounds);

    public MeshCollisionGeometry(TriangleMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var triangles = new List<Triangle>(mesh.TriangleCount);
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = Point(mesh.Indices[i]);
            var b = Point(mesh.Indices[i + 1]);
            var c = Point(mesh.Indices[i + 2]);
            if (!Finite(a) || !Finite(b) || !Finite(c))
                throw new InvalidDataException("Collision mesh contains a non-finite vertex.");
            if (D.Cross(new D(b) - new D(a), new D(c) - new D(a)).LengthSquared <= 1e-24) continue;
            triangles.Add(new Triangle(a, b, c, 0));
        }
        if (triangles.Count == 0) throw new InvalidDataException("Collision mesh has no non-degenerate triangles.");
        _triangles = triangles.ToArray();
        _components = BuildComponents();
        var nodes = new List<Node>();
        Build(0, _triangles.Length);
        _nodes = nodes.ToArray();

        Vector3 Point(int index)
        {
            if (index < 0 || index >= mesh.Positions.Length / 3)
                throw new InvalidDataException("Collision mesh vertex index is invalid.");
            return new(mesh.Positions[index * 3], mesh.Positions[index * 3 + 1], mesh.Positions[index * 3 + 2]);
        }
        int Build(int start, int count)
        {
            var bounds = _triangles[start].Bounds;
            var centers = new Bounds3(_triangles[start].Center, _triangles[start].Center);
            for (var i = start + 1; i < start + count; i++)
            {
                bounds = Union(bounds, _triangles[i].Bounds);
                var p = _triangles[i].Center;
                centers = Union(centers, new Bounds3(p, p));
            }
            var index = nodes.Count;
            nodes.Add(new Node(bounds, start, count, -1, -1));
            if (count <= 8) return index;
            var size = centers.Size;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(_triangles, start, count, Comparer<Triangle>.Create((a, b) => a.Center[axis].CompareTo(b.Center[axis])));
            var half = count / 2;
            var left = Build(start, half);
            var right = Build(start + half, count - half);
            nodes[index] = new Node(bounds, start, 0, left, right);
            return index;
        }
    }

    public Instance CreateInstance() => new(this);

    // STL duplicates vertices per facet. Weld exact stored coordinates to
    // identify each connected shell and verify that all its edges are closed.
    // Open sheets still take part in surface tests, but cannot invent a volume.
    private Component[] BuildComponents()
    {
        var parent = Enumerable.Range(0, _triangles.Length).ToArray();
        var vertices = new Dictionary<Vector3, (int Id, int Triangle)>();
        var ids = new int[_triangles.Length * 3];
        for (var i = 0; i < _triangles.Length; i++)
        {
            var t = _triangles[i];
            Add(t.A, i, 0); Add(t.B, i, 1); Add(t.C, i, 2);
        }
        var groups = new Dictionary<int, int>();
        var bounds = new List<Bounds3>();
        var samples = new List<Vector3>();
        for (var i = 0; i < _triangles.Length; i++)
        {
            var root = Root(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = groups.Count; groups.Add(root, group);
                bounds.Add(_triangles[i].Bounds); samples.Add(_triangles[i].Center);
            }
            bounds[group] = Union(bounds[group], _triangles[i].Bounds);
            _triangles[i] = _triangles[i] with { Component = group };
        }
        var edges = new Dictionary<ulong, (int Count, int Group)>();
        for (var i = 0; i < _triangles.Length; i++)
        for (var j = 0; j < 3; j++)
        {
            var a = ids[i * 3 + j]; var b = ids[i * 3 + (j + 1) % 3];
            var key = ((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            edges.TryGetValue(key, out var edge);
            edges[key] = (edge.Count + 1, _triangles[i].Component);
        }
        var closed = Enumerable.Repeat(true, groups.Count).ToArray();
        foreach (var edge in edges.Values) if (edge.Count != 2) closed[edge.Group] = false;
        var signedVolumes = new double[groups.Count];
        foreach (var triangle in _triangles)
        {
            var origin = new D(bounds[triangle.Component].Center);
            signedVolumes[triangle.Component] += D.Dot(new D(triangle.A) - origin,
                D.Cross(new D(triangle.B) - origin, new D(triangle.C) - origin));
        }
        return bounds.Select((b, i) => new Component(b, samples[i], closed[i], Math.Sign(signedVolumes[i]))).ToArray();

        int Root(int i)
        {
            while (i != parent[i]) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Add(Vector3 p, int triangle, int corner)
        {
            if (vertices.TryGetValue(p, out var v)) parent[Root(triangle)] = Root(v.Triangle);
            else { v = (vertices.Count, triangle); vertices.Add(p, v); }
            ids[triangle * 3 + corner] = v.Id;
        }
    }

    /// <summary>One scene occurrence. Mutable transform caches are instance-local.</summary>
    public sealed class Instance
    {
        private readonly MeshCollisionGeometry _mesh;
        private readonly Bounds3[] _worldBounds;
        private readonly int[] _boundsRevision;
        private readonly WorldTriangle[] _worldTriangles;
        private readonly int[] _triangleRevision;
        private Matrix4x4 _world = Matrix4x4.Identity;
        private Matrix4x4 _inverse = Matrix4x4.Identity;
        private int _revision = 1;
        internal Instance(MeshCollisionGeometry mesh)
        {
            _mesh = mesh;
            _worldBounds = new Bounds3[mesh._nodes.Length];
            _boundsRevision = new int[mesh._nodes.Length];
            _worldTriangles = new WorldTriangle[mesh._triangles.Length];
            _triangleRevision = new int[mesh._triangles.Length];
        }
        public void UpdateWorld(Matrix4x4 world)
        {
            if (world.Equals(_world)) return;
            if (!Matrix4x4.Invert(world, out var inverse))
                throw new InvalidDataException("Collision mesh transform is singular.");
            _world = world; _inverse = inverse;
            if (_revision == int.MaxValue) { Array.Clear(_boundsRevision); Array.Clear(_triangleRevision); _revision = 1; }
            else _revision++;
        }

        public bool Intersects(Instance other, double clearanceMm = 0)
        {
            ArgumentNullException.ThrowIfNull(other);
            var distance = Math.Max(ContactToleranceMm, clearanceMm);
            var squared = distance * distance;
            if (BoxDistanceSquared(WorldBounds(0), other.WorldBounds(0)) > squared) return false;
            if (SurfacesWithin(0, other, 0, squared)) return true;
            // Surface intersection alone misses a whole body enclosed by another.
            return SamplesInside(other) || other.SamplesInside(this);
        }

        private bool SamplesInside(Instance target)
        {
            foreach (var component in _mesh._components)
            {
                var world = D.Transform(new D(component.Sample), _world);
                var local = D.Transform(world, target._inverse);
                if (target._mesh.Contains(local)) return true;
            }
            return false;
        }

        private bool SurfacesWithin(int aIndex, Instance other, int bIndex, double squared)
        {
            var aBounds = WorldBounds(aIndex); var bBounds = other.WorldBounds(bIndex);
            if (BoxDistanceSquared(aBounds, bBounds) > squared) return false;
            var a = _mesh._nodes[aIndex]; var b = other._mesh._nodes[bIndex];
            if (a.Count > 0 && b.Count > 0)
            {
                for (var i = a.Start; i < a.Start + a.Count; i++)
                {
                    var t = GetTriangle(i);
                    if (BoxDistanceSquared(t.Bounds, bBounds) > squared) continue;
                    for (var j = b.Start; j < b.Start + b.Count; j++)
                    {
                        var u = other.GetTriangle(j);
                        if (BoxDistanceSquared(t.Bounds, u.Bounds) > squared) continue;
                        if (TriangleDistanceSquared(t.A, t.B, t.C, u.A, u.B, u.C) <= squared) return true;
                    }
                }
                return false;
            }
            if (a.Count == 0 && (b.Count > 0 || aBounds.Size.LengthSquared() >= bBounds.Size.LengthSquared()))
                return SurfacesWithin(a.Left, other, bIndex, squared) || SurfacesWithin(a.Right, other, bIndex, squared);
            return SurfacesWithin(aIndex, other, b.Left, squared) || SurfacesWithin(aIndex, other, b.Right, squared);
        }

        private WorldTriangle GetTriangle(int index)
        {
            if (_triangleRevision[index] == _revision) return _worldTriangles[index];
            var t = _mesh._triangles[index];
            var a = D.Transform(new D(t.A), _world);
            var b = D.Transform(new D(t.B), _world);
            var c = D.Transform(new D(t.C), _world);
            var min = new Vector3((float)Math.Min(a.X, Math.Min(b.X, c.X)), (float)Math.Min(a.Y, Math.Min(b.Y, c.Y)), (float)Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            var max = new Vector3((float)Math.Max(a.X, Math.Max(b.X, c.X)), (float)Math.Max(a.Y, Math.Max(b.Y, c.Y)), (float)Math.Max(a.Z, Math.Max(b.Z, c.Z)));
            var padding = new Vector3(Math.Max(0.00001f, Math.Max(min.Length(), max.Length()) * 0.000001f));
            var result = new WorldTriangle(a, b, c, new Bounds3(min - padding, max + padding));
            _worldTriangles[index] = result; _triangleRevision[index] = _revision;
            return result;
        }

        private Bounds3 WorldBounds(int index)
        {
            if (_boundsRevision[index] == _revision) return _worldBounds[index];
            var box = OrientedBounds3.Transform(_mesh._nodes[index].Bounds, _world).ToAxisAlignedBounds();
            // Float bounds are conservative; exact triangle tests use doubles.
            var magnitude = Math.Max(Vector3.Abs(box.Min).Length(), Vector3.Abs(box.Max).Length());
            var pad = new Vector3(Math.Max(0.00001f, magnitude * 0.000001f));
            box = new Bounds3(box.Min - pad, box.Max + pad);
            _worldBounds[index] = box; _boundsRevision[index] = _revision;
            return box;
        }
    }

    private bool Contains(D point)
    {
        var winding = 0;
        for (var component = 0; component < _components.Length; component++)
        {
            var c = _components[component];
            if (!c.Closed || !InBounds(point, c.Bounds)) continue;
            // Retry a different ray if it passes exactly through an edge/vertex.
            foreach (var direction in RayDirections)
            {
                var count = 0;
                if (!CountCrossings(0, component, point, direction, ref count)) continue;
                if ((count & 1) != 0) winding += c.Orientation;
                break;
            }
        }
        // Oppositely oriented inner shells enclose cavities. Summing shell
        // orientation preserves those voids and also accepts globally reversed
        // winding and overlapping, consistently oriented solid components.
        return winding != 0;
    }

    private static readonly D[] RayDirections = [new(1, 0.3713906764, 0.6947465906), new(0.529113714, 1, 0.21371819), new(0.1937191, 0.4197119, 1)];
    private bool CountCrossings(int index, int component, D origin, D direction, ref int count)
    {
        var node = _nodes[index];
        if (!RayBox(origin, direction, node.Bounds)) return true;
        if (node.Count == 0)
            return CountCrossings(node.Left, component, origin, direction, ref count) &&
                   CountCrossings(node.Right, component, origin, direction, ref count);
        for (var i = node.Start; i < node.Start + node.Count; i++)
        {
            var t = _triangles[i];
            if (t.Component != component) continue;
            if (!RayTriangle(origin, direction, new D(t.A), new D(t.B), new D(t.C), out var hit, out var u, out var v)) continue;
            if (hit <= ContactToleranceMm) continue;
            if (u < 1e-9 || v < 1e-9 || 1 - u - v < 1e-9) return false;
            count++;
        }
        return true;
    }

    private static bool RayBox(D p, D d, Bounds3 box)
    {
        var near = 0.0; var far = double.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var first = (box.Min[axis] - p[axis]) / d[axis];
            var last = (box.Max[axis] - p[axis]) / d[axis];
            near = Math.Max(near, Math.Min(first, last)); far = Math.Min(far, Math.Max(first, last));
            if (near > far + 1e-9) return false;
        }
        return true;
    }

    private static double TriangleDistanceSquared(D a, D b, D c, D d, D e, D f)
    {
        if (SegmentTriangle(a, b, d, e, f) || SegmentTriangle(b, c, d, e, f) || SegmentTriangle(c, a, d, e, f) ||
            SegmentTriangle(d, e, a, b, c) || SegmentTriangle(e, f, a, b, c) || SegmentTriangle(f, d, a, b, c)) return 0;
        var best = Math.Min(PointTriangleSquared(a, d, e, f), Math.Min(PointTriangleSquared(b, d, e, f), PointTriangleSquared(c, d, e, f)));
        best = Math.Min(best, Math.Min(PointTriangleSquared(d, a, b, c), Math.Min(PointTriangleSquared(e, a, b, c), PointTriangleSquared(f, a, b, c))));
        Span<D> first = stackalloc D[3] { a, b, c };
        Span<D> second = stackalloc D[3] { d, e, f };
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            best = Math.Min(best, SegmentDistanceSquared(first[i], first[(i + 1) % 3], second[j], second[(j + 1) % 3]));
        return best;
    }

    private static bool SegmentTriangle(D p, D q, D a, D b, D c) =>
        RayTriangle(p, q - p, a, b, c, out var t, out _, out _) && t >= 0 && t <= 1;

    private static bool RayTriangle(D p, D direction, D a, D b, D c, out double t, out double u, out double v)
    {
        var ab = b - a; var ac = c - a; var h = D.Cross(direction, ac);
        var det = D.Dot(ab, h); t = u = v = 0;
        if (Math.Abs(det) <= 1e-12 * Math.Sqrt(ab.LengthSquared * h.LengthSquared)) return false;
        var s = p - a;
        u = D.Dot(s, h) / det;
        if (u < -1e-12 || u > 1 + 1e-12) return false;
        var q = D.Cross(s, ab);
        v = D.Dot(direction, q) / det;
        if (v < -1e-12 || u + v > 1 + 1e-12) return false;
        t = D.Dot(ac, q) / det;
        return true;
    }

    private static double PointTriangleSquared(D p, D a, D b, D c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        var d1 = D.Dot(ab, ap); var d2 = D.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared;
        var bp = p - b; var d3 = D.Dot(ab, bp); var d4 = D.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).LengthSquared;
        var cp = p - c; var d5 = D.Dot(ab, cp); var d6 = D.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).LengthSquared;
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).LengthSquared;
        var denominator = va + vb + vc;
        if (Math.Abs(denominator) <= 1e-30)
            return Math.Min(SegmentDistanceSquared(p, p, a, b), Math.Min(SegmentDistanceSquared(p, p, b, c), SegmentDistanceSquared(p, p, c, a)));
        return (p - (a + ab * (vb / denominator) + ac * (vc / denominator))).LengthSquared;
    }

    private static double SegmentDistanceSquared(D p, D q, D r, D s)
    {
        var u = q - p; var v = s - r; var w = p - r;
        var a = u.LengthSquared; var b = D.Dot(u, v); var c = v.LengthSquared;
        var d = D.Dot(u, w); var e = D.Dot(v, w);
        if (a <= 1e-24 && c <= 1e-24) return w.LengthSquared;
        var x = a <= 1e-24 ? 0 : c <= 1e-24 ? Math.Clamp(-d / a, 0, 1) :
            a * c - b * b > 1e-24 ? Math.Clamp((b * e - c * d) / (a * c - b * b), 0, 1) : 0;
        var y = c <= 1e-24 ? 0 : (b * x + e) / c;
        if (y < 0) { y = 0; x = a <= 1e-24 ? 0 : Math.Clamp(-d / a, 0, 1); }
        else if (y > 1) { y = 1; x = a <= 1e-24 ? 0 : Math.Clamp((b - d) / a, 0, 1); }
        return (w + u * x - v * y).LengthSquared;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Bounds3 Union(Bounds3 a, Bounds3 b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    private static bool InBounds(D p, Bounds3 b) => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z;
    private static double BoxDistanceSquared(Bounds3 a, Bounds3 b)
    {
        var delta = Vector3.Max(Vector3.Zero, Vector3.Max(a.Min - b.Max, b.Min - a.Max));
        return (double)delta.X * delta.X + (double)delta.Y * delta.Y + (double)delta.Z * delta.Z;
    }
    private readonly record struct D(double X, double Y, double Z)
    {
        public D(Vector3 p) : this(p.X, p.Y, p.Z) { }
        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
        public double LengthSquared => Dot(this, this);
        public static D operator +(D a, D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D operator -(D a, D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D operator *(D a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        public static double Dot(D a, D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static D Cross(D a, D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static D Transform(D p, Matrix4x4 m) => new(p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42, p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);
    }
}
