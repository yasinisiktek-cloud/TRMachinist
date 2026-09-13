using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// Produces cutter, shank and holder collision regions from the geometry that
/// is actually rendered.  A single assembly-wide radial box is not suitable:
/// it extends the holder diameter down through the shank and creates false
/// stock contacts during otherwise valid cutting moves.
/// </summary>
public static class ToolCollisionGeometry
{
    // Two closed solid boxes that only share the cutter/flute boundary are not
    // penetrating.  Leave a microscopic open seam so SAT classifies that pose
    // as clearance-only while still detecting any material penetration.
    public const float AxialContactToleranceMm = 0.01f;

    // A holder is not a single constant-diameter cylinder.  In particular,
    // applying the flange radius to the complete taper length creates false
    // stock contacts while the real lower taper still has ample clearance.
    // Four millimetre axial envelopes keep the broad phase conservative while
    // following the rendered cutter assembly closely enough for live IPW.
    private const float MaximumEnvelopeLengthMm = 4f;
    private const int MaximumEnvelopeCountPerBand = 48;

    public readonly record struct Region(string Kind, Bounds3 Bounds);

    public static IReadOnlyList<Region> FromNxAssemblyMesh(TriangleMeshData mesh, JobTool tool)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(tool);

        var extent = Math.Max(0.001f, mesh.Bounds.Max.X - mesh.Bounds.Min.X);
        var cutterEnd = mesh.Bounds.Min.X + (float)Math.Clamp(
            tool.FluteLength > 0.001 ? tool.FluteLength : extent * 0.22,
            0.5,
            extent * 0.65);
        var holderStart = mesh.Bounds.Min.X + (float)Math.Clamp(
            tool.Length > 0.001 ? tool.Length : extent * 0.72,
            cutterEnd - mesh.Bounds.Min.X + 0.5,
            extent * 0.94);

        var result = new List<Region>(24);
        AddAxialBand(result, mesh, "cutter", mesh.Bounds.Min.X, cutterEnd, insetStart: false, includeUpperBoundary: true, segmented: false);
        AddAxialBand(result, mesh, "shank", cutterEnd, holderStart, insetStart: true, includeUpperBoundary: false, segmented: true);
        AddAxialBand(result, mesh, "holder", holderStart, mesh.Bounds.Max.X, insetStart: false, includeUpperBoundary: true, segmented: true);
        return result;
    }

    public static IReadOnlyList<Region> FromParametricParts(
        IReadOnlyList<ParametricToolMeshBuilder.ToolMeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var result = new List<Region>(24);
        AddMerged("cutter", parts.Where(part => part.Role.Equals("cutter", StringComparison.OrdinalIgnoreCase)));
        AddSegmented("shank", parts.Where(part => part.Role.Equals("shank", StringComparison.OrdinalIgnoreCase)), insetStart: true);
        AddSegmented("holder", parts.Where(part => part.Role.StartsWith("holder", StringComparison.OrdinalIgnoreCase)));
        return result;

        void AddMerged(
            string kind,
            IEnumerable<ParametricToolMeshBuilder.ToolMeshPart> selected,
            bool insetStart = false)
        {
            var meshes = selected.Select(part => part.Mesh).ToArray();
            if (meshes.Length == 0) return;
            var min = meshes[0].Bounds.Min;
            var max = meshes[0].Bounds.Max;
            for (var index = 1; index < meshes.Length; index++)
            {
                min = Vector3.Min(min, meshes[index].Bounds.Min);
                max = Vector3.Max(max, meshes[index].Bounds.Max);
            }
            if (insetStart) min.X = Math.Min(max.X, min.X + AxialContactToleranceMm);
            if (max.X - min.X > 1e-4f) result.Add(new Region(kind, new Bounds3(min, max)));
        }

        void AddSegmented(
            string kind,
            IEnumerable<ParametricToolMeshBuilder.ToolMeshPart> selected,
            bool insetStart = false)
        {
            var first = true;
            foreach (var part in selected)
            {
                AddAxialBand(
                    result,
                    part.Mesh,
                    kind,
                    part.Mesh.Bounds.Min.X,
                    part.Mesh.Bounds.Max.X,
                    insetStart && first,
                    includeUpperBoundary: true,
                    segmented: true);
                first = false;
            }
        }
    }

    private static void AddAxialBand(
        ICollection<Region> result,
        TriangleMeshData mesh,
        string kind,
        float bandMin,
        float bandMax,
        bool insetStart,
        bool includeUpperBoundary,
        bool segmented)
    {
        bandMin = Math.Min(bandMax, bandMin + (insetStart ? AxialContactToleranceMm : 0));
        var extent = bandMax - bandMin;
        if (extent <= 1e-4f) return;

        var segmentCount = segmented
            ? Math.Clamp((int)Math.Ceiling(extent / MaximumEnvelopeLengthMm), 1, MaximumEnvelopeCountPerBand)
            : 1;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentMin = bandMin + (extent * segment / segmentCount);
            var segmentMax = bandMin + (extent * (segment + 1) / segmentCount);
            AddAxialEnvelope(
                result,
                mesh,
                kind,
                segmentMin,
                segmentMax,
                includeUpperBoundary && segment == segmentCount - 1);
        }
    }

    private static void AddAxialEnvelope(
        ICollection<Region> result,
        TriangleMeshData mesh,
        string kind,
        float segmentMin,
        float segmentMax,
        bool includeUpperBoundary)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var found = false;

        for (var offset = 0; offset + 2 < mesh.Indices.Length; offset += 3)
        {
            var first = Position(mesh, mesh.Indices[offset]);
            var second = Position(mesh, mesh.Indices[offset + 1]);
            var third = Position(mesh, mesh.Indices[offset + 2]);
            var triangleMinX = Math.Min(first.X, Math.Min(second.X, third.X));
            var triangleMaxX = Math.Max(first.X, Math.Max(second.X, third.X));
            if (triangleMaxX < segmentMin ||
                (includeUpperBoundary ? triangleMinX > segmentMax : triangleMinX >= segmentMax)) continue;

            minimum = Vector3.Min(minimum, Vector3.Min(first, Vector3.Min(second, third)));
            maximum = Vector3.Max(maximum, Vector3.Max(first, Vector3.Max(second, third)));
            found = true;
        }

        if (!found) return;
        minimum.X = Math.Max(minimum.X, segmentMin);
        maximum.X = Math.Min(maximum.X, segmentMax);
        if (maximum.X - minimum.X <= 1e-4f) return;
        result.Add(new Region(kind, new Bounds3(minimum, maximum)));
    }

    private static Vector3 Position(TriangleMeshData mesh, int index)
    {
        var start = index * 3;
        return new Vector3(mesh.Positions[start], mesh.Positions[start + 1], mesh.Positions[start + 2]);
    }
}
