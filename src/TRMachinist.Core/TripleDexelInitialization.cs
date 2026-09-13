using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private enum Axis3 { X, Y, Z }

    private static DexelSpan[][] FullRayTemplates(int rayCount, double minimum, double maximum)
    {
        // These arrays are immutable templates. Sharing the same one-span
        // template avoids millions of identical one-element arrays for a box.
        var template = new[] { new DexelSpan(minimum, maximum) };
        var rays = new DexelSpan[rayCount][];
        Array.Fill(rays, template);
        return rays;
    }

    /// <summary>
    /// Recognizes any correctly tessellated axis-aligned rectangular blank by
    /// geometry, not by filename or part dimensions. Each triangle must lie on
    /// one of the six bounds planes and every face must cover its exact area.
    /// </summary>
    private static bool IsAxisAlignedBoxMesh(TriangleMeshData mesh, Bounds3 bounds)
    {
        var size = bounds.Size;
        var scale = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var coordinateTolerance = Math.Max(1e-5, scale * 1e-6);
        var areas = new double[6];

        for (var triangle = 0; triangle + 2 < mesh.Indices.Length; triangle += 3)
        {
            var a = Point(mesh, mesh.Indices[triangle]);
            var b = Point(mesh, mesh.Indices[triangle + 1]);
            var c = Point(mesh, mesh.Indices[triangle + 2]);
            if (!InsideBounds(a) || !InsideBounds(b) || !InsideBounds(c)) return false;

            var area = Vector3.Cross(b - a, c - a).Length() * 0.5;
            if (area <= coordinateTolerance * coordinateTolerance) continue;

            var face = FaceIndex(a, b, c);
            if (face < 0) return false;
            areas[face] += area;
        }

        var expected = new double[]
        {
            size.Y * size.Z, size.Y * size.Z,
            size.X * size.Z, size.X * size.Z,
            size.X * size.Y, size.X * size.Y
        };
        for (var face = 0; face < areas.Length; face++)
        {
            var areaTolerance = Math.Max(1e-4, expected[face] * 1e-4);
            if (Math.Abs(areas[face] - expected[face]) > areaTolerance) return false;
        }
        return true;

        bool InsideBounds(Vector3 point) =>
            point.X >= bounds.Min.X - coordinateTolerance && point.X <= bounds.Max.X + coordinateTolerance &&
            point.Y >= bounds.Min.Y - coordinateTolerance && point.Y <= bounds.Max.Y + coordinateTolerance &&
            point.Z >= bounds.Min.Z - coordinateTolerance && point.Z <= bounds.Max.Z + coordinateTolerance;

        int FaceIndex(Vector3 a, Vector3 b, Vector3 c)
        {
            if (OnPlane(a.X, b.X, c.X, bounds.Min.X)) return 0;
            if (OnPlane(a.X, b.X, c.X, bounds.Max.X)) return 1;
            if (OnPlane(a.Y, b.Y, c.Y, bounds.Min.Y)) return 2;
            if (OnPlane(a.Y, b.Y, c.Y, bounds.Max.Y)) return 3;
            if (OnPlane(a.Z, b.Z, c.Z, bounds.Min.Z)) return 4;
            if (OnPlane(a.Z, b.Z, c.Z, bounds.Max.Z)) return 5;
            return -1;
        }

        bool OnPlane(float a, float b, float c, float plane) =>
            Math.Abs(a - plane) <= coordinateTolerance &&
            Math.Abs(b - plane) <= coordinateTolerance &&
            Math.Abs(c - plane) <= coordinateTolerance;
    }

    /// <summary>
    /// Samples a closed STL directly into one dexel bundle. Each triangle is
    /// projected onto the transverse grid, so setup cost scales with covered
    /// pixels rather than rays multiplied by every triangle.
    /// </summary>
    private static DexelSpan[][] RasterizeInitialStock(
        TriangleMeshData mesh,
        Axis3 longitudinalAxis,
        IReadOnlyList<double> uCoordinates,
        IReadOnlyList<double> vCoordinates)
    {
        var hits = Enumerable.Range(0, uCoordinates.Count * vCoordinates.Count)
            .Select(_ => new List<double>(8))
            .ToArray();

        for (var triangle = 0; triangle + 2 < mesh.Indices.Length; triangle += 3)
        {
            var pa = Point(mesh, mesh.Indices[triangle]);
            var pb = Point(mesh, mesh.Indices[triangle + 1]);
            var pc = Point(mesh, mesh.Indices[triangle + 2]);
            var a = ProjectPoint(pa, longitudinalAxis);
            var b = ProjectPoint(pb, longitudinalAxis);
            var c = ProjectPoint(pc, longitudinalAxis);
            var denominator = ((b.V - c.V) * (a.U - c.U)) + ((c.U - b.U) * (a.V - c.V));
            if (Math.Abs(denominator) <= 1e-12) continue;

            var firstU = LowerBound(uCoordinates, Math.Min(a.U, Math.Min(b.U, c.U)) - 1e-7);
            var lastU = UpperBound(uCoordinates, Math.Max(a.U, Math.Max(b.U, c.U)) + 1e-7) - 1;
            var firstV = LowerBound(vCoordinates, Math.Min(a.V, Math.Min(b.V, c.V)) - 1e-7);
            var lastV = UpperBound(vCoordinates, Math.Max(a.V, Math.Max(b.V, c.V)) + 1e-7) - 1;
            if (firstU > lastU || firstV > lastV) continue;

            for (var vIndex = firstV; vIndex <= lastV; vIndex++)
            for (var uIndex = firstU; uIndex <= lastU; uIndex++)
            {
                var u = uCoordinates[uIndex];
                var v = vCoordinates[vIndex];
                var wa = (((b.V - c.V) * (u - c.U)) + ((c.U - b.U) * (v - c.V))) / denominator;
                var wb = (((c.V - a.V) * (u - c.U)) + ((a.U - c.U) * (v - c.V))) / denominator;
                var wc = 1.0 - wa - wb;
                const double epsilon = 1e-8;
                if (wa < -epsilon || wb < -epsilon || wc < -epsilon) continue;
                hits[(vIndex * uCoordinates.Count) + uIndex].Add(
                    (wa * a.Longitudinal) + (wb * b.Longitudinal) + (wc * c.Longitudinal));
            }
        }

        var rays = new DexelSpan[hits.Length][];
        for (var ray = 0; ray < hits.Length; ray++)
        {
            var sorted = hits[ray].OrderBy(value => value).ToArray();
            var unique = new List<double>(sorted.Length);
            foreach (var value in sorted)
                if (unique.Count == 0 || Math.Abs(value - unique[^1]) > 1e-5)
                    unique.Add(value);

            var spans = new List<DexelSpan>(unique.Count / 2);
            for (var index = 0; index + 1 < unique.Count; index += 2)
                if (unique[index + 1] > unique[index] + 1e-8)
                    spans.Add(new DexelSpan(unique[index], unique[index + 1]));
            rays[ray] = spans.ToArray();
        }
        return rays;
    }

    private void EnsureRasterizedStockIsUsable()
    {
        var xLength = _initialXRays.Sum(ray => ray.Sum(span => span.Length));
        var yLength = _initialYRays.Sum(ray => ray.Sum(span => span.Length));
        var zLength = _initialZRays.Sum(ray => ray.Sum(span => span.Length));
        if (xLength <= 1e-7 || yLength <= 1e-7 || zLength <= 1e-7)
            throw new InvalidDataException(
                "Blank STL kapalı bir hacim olarak üç eksende örneklenemedi. STL yüzeyinin kapalı ve bozuk üçgensiz olduğunu denetleyin.");
    }

    private static void ValidateInitialStockMesh(TriangleMeshData mesh, Bounds3 bounds)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Positions.Length < 9 || mesh.Indices.Length < 3 || mesh.Indices.Length % 3 != 0)
            throw new InvalidDataException("Blank STL geçerli bir üçgen ağı içermiyor.");
        if (mesh.Indices.Any(index => index < 0 || (index * 3) + 2 >= mesh.Positions.Length))
            throw new InvalidDataException("Blank STL geçersiz bir köşe indeksi içeriyor.");

        var tolerance = Math.Max(1e-4, Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y, bounds.Size.Z)) * 1e-5);
        if (Vector3.Distance(mesh.Bounds.Min, bounds.Min) > tolerance ||
            Vector3.Distance(mesh.Bounds.Max, bounds.Max) > tolerance)
            throw new ArgumentException("Blank STL sınırları dexel stok sınırlarıyla uyuşmuyor.", nameof(mesh));
    }

    private readonly record struct AxisPoint(double Longitudinal, double U, double V);

    private static AxisPoint ProjectPoint(Vector3 point, Axis3 axis) => axis switch
    {
        Axis3.X => new AxisPoint(point.X, point.Y, point.Z),
        Axis3.Y => new AxisPoint(point.Y, point.X, point.Z),
        _ => new AxisPoint(point.Z, point.X, point.Y)
    };

    private static Vector3 Point(TriangleMeshData mesh, int vertex)
    {
        var offset = vertex * 3;
        return new Vector3(mesh.Positions[offset], mesh.Positions[offset + 1], mesh.Positions[offset + 2]);
    }

    private static int LowerBound(IReadOnlyList<double> values, double wanted)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] < wanted) low = middle + 1; else high = middle;
        }
        return Math.Clamp(low, 0, values.Count - 1);
    }

    private static int UpperBound(IReadOnlyList<double> values, double wanted)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] <= wanted) low = middle + 1; else high = middle;
        }
        return Math.Clamp(low, 0, values.Count);
    }
}
