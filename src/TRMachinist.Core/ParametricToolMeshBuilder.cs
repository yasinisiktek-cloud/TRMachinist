using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// Builds the cutter, shank and holder directly from NX tool-builder section data.
/// The axis convention matches the simulator spindle: tool tip at X=0, assembly along +X.
/// </summary>
public static class ParametricToolMeshBuilder
{
    private readonly record struct Ring(float X, float Radius);

    public sealed record ToolMeshPart(string Role, TriangleMeshData Mesh);

    /// <summary>
    /// Returns cutter, shank and holder as distinct meshes in the same tool-local
    /// coordinate system.  Keeping the three NX tool-builder regions separate lets
    /// the viewer render the assembly with the same visual hierarchy used by NX
    /// instead of presenting the complete tool as one featureless cylinder.
    /// </summary>
    public static IReadOnlyList<ToolMeshPart> BuildParts(JobTool tool, int sides = 36)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        sides = Math.Clamp(sides, 12, 96);

        var diameter = Positive(tool.Diameter, 10);
        var radius = (float)(diameter * 0.5);
        var totalLength = Positive(tool.Length, 60);
        var fluteLength = Math.Clamp(Positive(tool.FluteLength, Math.Min(totalLength, totalLength * 0.4)), 0.001, totalLength);
        var parts = new List<ToolMeshPart>();

        var cutter = new List<Ring>();
        AddCutterProfile(cutter, tool, radius, (float)fluteLength);
        parts.Add(new ToolMeshPart("cutter", Revolve(cutter, sides)));

        var x = cutter[^1].X;
        var currentRadius = cutter[^1].Radius;
        var shank = new List<Ring>();
        AddRing(shank, x, currentRadius);
        if (tool.ShankSections.Count > 0)
        {
            AppendSections(shank, tool.ShankSections, ref x, ref currentRadius);
        }
        else if (totalLength > x + 0.001)
        {
            currentRadius = (float)(Positive(tool.ShankDiameter, diameter) * 0.5);
            AddRing(shank, x, currentRadius);
            x = (float)totalLength;
            AddRing(shank, x, currentRadius);
        }
        if (HasAxialLength(shank)) parts.Add(new ToolMeshPart("shank", Revolve(shank, sides)));

        if (tool.HolderSections.Count > 0)
        {
            // NX toolInsertion is the length of the cutter/shank inserted into the
            // holder.  It is an overlap, not an extra distance from the tool tip.
            // Example: a 75 mm tool inserted 20 mm into a 59.8 mm holder has a
            // 114.8 mm gauge assembly, not 134.8 mm.
            var holderStart = tool.ToolInsertion > 0
                ? MathF.Max(0, x - (float)tool.ToolInsertion)
                : x;
            x = holderStart;
            for (var index = 0; index < tool.HolderSections.Count; index++)
            {
                var section = tool.HolderSections[index];
                if (section.Diameter <= 0 || section.Length <= 0) continue;

                var holder = new List<Ring>();
                AddRing(holder, x, currentRadius);
                AppendSection(holder, section, ref x, ref currentRadius);
                if (!HasAxialLength(holder)) continue;

                var role = index switch
                {
                    0 => "holder_taper",
                    _ when index == tool.HolderSections.Count - 1 => "holder_flange",
                    _ => "holder_body"
                };
                parts.Add(new ToolMeshPart(role, Revolve(holder, sides)));
            }
        }

        return parts;
    }

    public static TriangleMeshData Build(JobTool tool, int sides = 36)
    {
        var parts = BuildParts(tool, sides);
        if (parts.Count == 0) throw new InvalidDataException("Parametrik takım profili boş.");
        return Merge(parts.Select(x => x.Mesh));
    }

    private static TriangleMeshData Merge(IEnumerable<TriangleMeshData> meshes)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<int>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        foreach (var mesh in meshes)
        {
            var vertexOffset = positions.Count / 3;
            positions.AddRange(mesh.Positions);
            normals.AddRange(mesh.Normals);
            indices.AddRange(mesh.Indices.Select(index => index + vertexOffset));
            min = Vector3.Min(min, mesh.Bounds.Min);
            max = Vector3.Max(max, mesh.Bounds.Max);
        }

        return new TriangleMeshData
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
            Bounds = new Bounds3(min, max)
        };
    }

    private static void AddCutterProfile(List<Ring> profile, JobTool tool, float radius, float fluteLength)
    {
        var kind = (tool.Subtype + " " + tool.Type).ToUpperInvariant();
        if (ToolCuttingProfileFactory.Create(tool) is { IsSupported: true } cutting)
        {
            {
                foreach (var section in cutting.Layers)
                {
                    AddRing(profile, (float)section.AxialOffset, (float)(section.StartRadius ?? section.Radius));
                    AddRing(profile, (float)(section.AxialOffset + section.Length), (float)section.Radius);
                }
                return;
            }
        }
        if (kind.Contains("BALL") || kind.Contains("SPHER"))
        {
            const int ballSteps = 8;
            for (var i = 0; i <= ballSteps; i++)
            {
                var x = radius * i / ballSteps;
                var dx = x - radius;
                var ringRadius = MathF.Sqrt(MathF.Max(0, (radius * radius) - (dx * dx)));
                AddRing(profile, x, ringRadius);
            }
            AddRing(profile, MathF.Max(fluteLength, radius), radius);
            return;
        }

        if (kind.Contains("DRILL") || kind.Contains("SPOT") || tool.TipAngle > 0.001)
        {
            var included = tool.TipAngle > 1 && tool.TipAngle < 179 ? tool.TipAngle : 118;
            var halfAngle = (float)(included * Math.PI / 360.0);
            var pointLength = radius / MathF.Max(0.05f, MathF.Tan(halfAngle));
            pointLength = MathF.Min(pointLength, fluteLength * 0.8f);
            AddRing(profile, 0, 0);
            AddRing(profile, pointLength, radius);
            AddRing(profile, fluteLength, radius);
            return;
        }

        AddRing(profile, 0, radius);
        AddRing(profile, fluteLength, radius);
    }

    private static void AppendSections(
        List<Ring> profile,
        IReadOnlyList<ToolProfileSection> sections,
        ref float x,
        ref float currentRadius)
    {
        foreach (var section in sections)
        {
            AppendSection(profile, section, ref x, ref currentRadius);
        }
    }

    private static void AppendSection(
        List<Ring> profile,
        ToolProfileSection section,
        ref float x,
        ref float currentRadius)
    {
        if (section.Diameter <= 0 || section.Length <= 0) return;
        var lowerRadius = (float)(section.Diameter * 0.5);
        AddRing(profile, x, lowerRadius);
        var deltaDiameter = 2.0 * section.Length * Math.Tan(section.TaperAngle * Math.PI / 180.0);
        var upperDiameter = section.Diameter + deltaDiameter;
        if (!double.IsFinite(upperDiameter) || upperDiameter <= 0 || upperDiameter > 5000) upperDiameter = section.Diameter;
        x += (float)section.Length;
        currentRadius = (float)(upperDiameter * 0.5);
        AddRing(profile, x, currentRadius);
    }

    private static void AddRing(List<Ring> profile, float x, float radius)
    {
        radius = MathF.Max(0, radius);
        if (profile.Count > 0)
        {
            var last = profile[^1];
            if (MathF.Abs(last.X - x) < 1e-5f && MathF.Abs(last.Radius - radius) < 1e-5f) return;
        }
        profile.Add(new Ring(x, radius));
    }

    private static bool HasAxialLength(IReadOnlyList<Ring> profile) =>
        profile.Count >= 2 && profile.Max(x => x.X) - profile.Min(x => x.X) > 0.0001f;

    private static TriangleMeshData Revolve(IReadOnlyList<Ring> profile, int sides)
    {
        if (profile.Count < 2) throw new InvalidDataException("Parametrik takım profili boş.");
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<int>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var p = 0; p < profile.Count - 1; p++)
        {
            var a = profile[p];
            var b = profile[p + 1];
            for (var side = 0; side < sides; side++)
            {
                var a0 = side * MathF.Tau / sides;
                var a1 = (side + 1) * MathF.Tau / sides;
                var p00 = Point(a, a0);
                var p01 = Point(a, a1);
                var p10 = Point(b, a0);
                var p11 = Point(b, a1);
                // Rings advance along +X. Outward winding must agree with the
                // end caps; inward side walls disappear under normal back-face culling.
                AddTriangle(p00, p11, p10);
                AddTriangle(p00, p01, p11);
            }
        }

        AddCap(profile[0], -Vector3.UnitX);
        AddCap(profile[^1], Vector3.UnitX);

        return new TriangleMeshData
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
            Bounds = new Bounds3(min, max)
        };

        Vector3 Point(Ring ring, float angle) => new(ring.X, MathF.Cos(angle) * ring.Radius, MathF.Sin(angle) * ring.Radius);

        void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            var cross = Vector3.Cross(p1 - p0, p2 - p0);
            var normal = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitX;
            var start = positions.Count / 3;
            AddVertex(p0, normal); AddVertex(p1, normal); AddVertex(p2, normal);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        }

        void AddVertex(Vector3 point, Vector3 normal)
        {
            positions.Add(point.X); positions.Add(point.Y); positions.Add(point.Z);
            normals.Add(normal.X); normals.Add(normal.Y); normals.Add(normal.Z);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }

        void AddCap(Ring ring, Vector3 normal)
        {
            if (ring.Radius <= 0.000001f) return;
            var center = new Vector3(ring.X, 0, 0);
            for (var side = 0; side < sides; side++)
            {
                var p0 = Point(ring, side * MathF.Tau / sides);
                var p1 = Point(ring, (side + 1) * MathF.Tau / sides);
                if (normal.X < 0) AddTriangle(center, p1, p0);
                else AddTriangle(center, p0, p1);
            }
        }
    }

    private static double Positive(double value, double fallback) => value > 0 && double.IsFinite(value) ? value : fallback;
}
