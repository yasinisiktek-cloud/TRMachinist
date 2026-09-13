using System.Numerics;
using TRMachinist.Core;

internal static class MeshCollisionTests
{
    public static void ConcaveGap()
    {
        var shell = new MeshCollisionGeometry(SquareTube());
        Require(shell.ClosedComponentCount == 1, "The annular prism must be a closed concave shell.");
        var ring = shell.CreateInstance();
        var probe = new MeshCollisionGeometry(Box(new(-1), new(1))).CreateInstance();
        Require(OrientedBounds3.Transform(shell.Bounds, Matrix4x4.Identity).Intersects(
            OrientedBounds3.Transform(new Bounds3(new(-1), new(1)), Matrix4x4.Identity)), "Broad phase must overlap across the hole.");
        Require(!ring.Intersects(probe, 1.99), "Empty hole was filled by its bounding box or containment ray.");
        Require(ring.Intersects(probe, 2.01), "Nearest inner wall was missed by clearance query.");
        probe.UpdateWorld(Matrix4x4.CreateTranslation(3, 0, 0));
        Require(ring.Intersects(probe), "Probe crossing the inner wall was missed.");
        var common = Matrix4x4.CreateFromYawPitchRoll(.48f, -.8f, 1.1f) * Matrix4x4.CreateTranslation(270, -610, 90);
        ring.UpdateWorld(common); probe.UpdateWorld(common);
        Require(!ring.Intersects(probe, 1.99), "Rotated cavity became solid.");
    }

    public static void Clearance()
    {
        var mesh = new MeshCollisionGeometry(Box(new(-1), new(1)));
        var a = mesh.CreateInstance(); var b = mesh.CreateInstance();
        b.UpdateWorld(Matrix4x4.CreateTranslation(2.05f, 0, 0));
        Require(!a.Intersects(b), "Positive air gap became real collision.");
        Require(a.Intersects(b, .25), "Safety proximity was lost.");
        Require(!CollisionAlertPolicy.ShouldStopPlayback([true]), "Clearance must only warn.");
        b.UpdateWorld(Matrix4x4.CreateTranslation(2.2f, 2.2f, 0));
        Require(!a.Intersects(b, .25) && a.Intersects(b, .29), "Diagonal gap must use Euclidean surface distance.");
        b.UpdateWorld(Matrix4x4.CreateTranslation(2, 0, 0));
        Require(a.Intersects(b), "Actual machine-face contact was missed.");
        b.UpdateWorld(Matrix4x4.CreateTranslation(1.98f, 0, 0));
        Require(a.Intersects(b), "Small physical overlap was missed.");
    }

    public static void Containment()
    {
        var outer = new MeshCollisionGeometry(Box(new(-5), new(5))).CreateInstance();
        var inner = new MeshCollisionGeometry(Box(new(-1), new(1))).CreateInstance();
        Require(outer.Intersects(inner) && inner.Intersects(outer), "Full containment without triangle crossings was missed.");
        var disconnected = new MeshCollisionGeometry(Combine(Box(new(40), new(42)), Box(new(-1), new(1))));
        Require(disconnected.ClosedComponentCount == 2, "Disconnected shells were not retained.");
        Require(outer.Intersects(disconnected.CreateInstance()), "Only the first shell was checked for containment.");
        var innerBoundary = Box(new(-3), new(3));
        var reversed = innerBoundary.Indices.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] }).ToArray();
        var cavity = new TriangleMeshData { Positions = innerBoundary.Positions, Normals = innerBoundary.Normals,
            Indices = reversed, Bounds = innerBoundary.Bounds };
        var hollow = new MeshCollisionGeometry(Combine(Box(new(-5), new(5)), cavity)).CreateInstance();
        Require(!hollow.Intersects(inner), "A disconnected inner cavity was filled by the outer shell.");
        inner.UpdateWorld(Matrix4x4.CreateTranslation(3.5f, 0, 0));
        Require(hollow.Intersects(inner), "A real crossing of the cavity wall was lost.");
        var shift = Matrix4x4.CreateFromYawPitchRoll(.3f, -.7f, .19f) * Matrix4x4.CreateTranslation(-800, 20, 300);
        outer.UpdateWorld(shift); inner.UpdateWorld(shift);
        Require(outer.Intersects(inner), "Transformed full containment was missed.");
    }

    public static void OpenSurfaces()
    {
        var box = Box(new(-5), new(5));
        // Remove one face: its apparent bounding volume is not a closed solid.
        var open = new MeshCollisionGeometry(new TriangleMeshData { Positions = box.Positions, Normals = box.Normals,
            Indices = box.Indices.Skip(6).ToArray(), Bounds = box.Bounds });
        Require(open.ClosedComponentCount == 0, "An open sheet was accepted as closed volume.");
        var small = new MeshCollisionGeometry(Box(new(-1), new(1))).CreateInstance();
        Require(!open.CreateInstance().Intersects(small), "An open sheet invented an interior volume.");
        small.UpdateWorld(Matrix4x4.CreateTranslation(5, 0, 0));
        Require(open.CreateInstance().Intersects(small), "Open surface crossing must still be detected.");
    }

    public static void RigidTransforms()
    {
        var bounds = new Bounds3(new(-1, -2, -3), new(1, 2, 3));
        var geometry = new MeshCollisionGeometry(Box(bounds.Min, bounds.Max));
        var a = geometry.CreateInstance(); var b = geometry.CreateInstance();
        var random = new Random(25791);
        for (var i = 0; i < 500; i++)
        {
            Matrix4x4 Pose() => Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6,
                (float)random.NextDouble() * 6, (float)random.NextDouble() * 6) *
                Matrix4x4.CreateTranslation((float)random.NextDouble() * 12 - 6, (float)random.NextDouble() * 12 - 6,
                    (float)random.NextDouble() * 12 - 6);
            var pa = Pose(); var pb = Pose(); a.UpdateWorld(pa); b.UpdateWorld(pb);
            var expected = OrientedBounds3.Transform(bounds, pa).Intersects(OrientedBounds3.Transform(bounds, pb));
            Require(a.Intersects(b) == expected && b.Intersects(a) == expected, $"Triangle/OBB oracle disagreement at random pose {i}.");
        }
        a.UpdateWorld(Matrix4x4.Identity); b.UpdateWorld(Matrix4x4.Identity);
        Require(a.Intersects(b), "Reset retained stale transformed geometry.");
        b.UpdateWorld(Matrix4x4.CreateTranslation(100, 0, 0));
        Require(!a.Intersects(b), "Instances sharing a mesh leaked transform caches.");
    }

    public static void CrossingTriangles()
    {
        var a = new MeshCollisionGeometry(Facets([new(-3,-3,0),new(3,-3,0),new(0,3,0)])).CreateInstance();
        var b = new MeshCollisionGeometry(Facets([new(0,0,-3),new(0,0,3),new(4,0,2)])).CreateInstance();
        Require(a.Intersects(b), "Non-coplanar edge/face crossing was missed.");
        var c = new MeshCollisionGeometry(Facets([new(-3,1,0),new(3,1,0),new(0,-4,0)])).CreateInstance();
        Require(a.Intersects(c), "Coplanar edge crossings were missed.");
        c.UpdateWorld(Matrix4x4.CreateTranslation(0, 0, .001f));
        Require(!a.Intersects(c) && a.Intersects(c, .002), "Nearly coplanar gap was promoted to contact.");
        var withDegenerate = new MeshCollisionGeometry(Facets([new(-3,-3,0),new(3,-3,0),new(0,3,0),new(1),new(1),new(1)]));
        Require(withDegenerate.TriangleCount == 1 && withDegenerate.CreateInstance().Intersects(b), "Degenerate facet handling changed real crossings.");
    }

    private static TriangleMeshData Box(Vector3 min, Vector3 max)
    {
        Vector3[] v = [new(min.X,min.Y,min.Z),new(max.X,min.Y,min.Z),new(max.X,max.Y,min.Z),new(min.X,max.Y,min.Z),
            new(min.X,min.Y,max.Z),new(max.X,min.Y,max.Z),new(max.X,max.Y,max.Z),new(min.X,max.Y,max.Z)];
        int[] ids = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
        return Facets(ids.Select(i => v[i]).ToArray());
    }
    private static TriangleMeshData SquareTube()
    {
        Vector3[] outer = [new(-5,-5,0),new(5,-5,0),new(5,5,0),new(-5,5,0)];
        Vector3[] inner = [new(-3,-3,0),new(3,-3,0),new(3,3,0),new(-3,3,0)];
        var vertices = new List<Vector3>();
        var up = Vector3.UnitZ * 2; var down = -up;
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            Quad(outer[i]+down,outer[j]+down,outer[j]+up,outer[i]+up);
            Quad(inner[j]+down,inner[i]+down,inner[i]+up,inner[j]+up);
            Quad(outer[i]+up,outer[j]+up,inner[j]+up,inner[i]+up);
            Quad(outer[j]+down,outer[i]+down,inner[i]+down,inner[j]+down);
        }
        return Facets(vertices.ToArray());
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { vertices.AddRange([a,b,c,a,c,d]); }
    }
    private static TriangleMeshData Combine(params TriangleMeshData[] meshes) =>
        Facets(meshes.SelectMany(m => m.Indices.Select(i => new Vector3(m.Positions[i*3],m.Positions[i*3+1],m.Positions[i*3+2]))).ToArray());
    private static TriangleMeshData Facets(Vector3[] vertices) => new()
    {
        Positions = vertices.SelectMany(v => new[] {v.X,v.Y,v.Z}).ToArray(), Normals = new float[vertices.Length*3],
        Indices = Enumerable.Range(0,vertices.Length).ToArray(),
        Bounds = new Bounds3(vertices.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min),
            vertices.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max))
    };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
