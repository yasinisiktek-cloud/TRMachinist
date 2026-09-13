using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// Resolves the canonical machine coordinate system stored by the TRMAC exporter.
/// Geometry occurrence transforms are only for mapping STL prototype coordinates
/// into this canonical system; they must never be applied to KIM axes or junctions.
/// </summary>
public static class MachineCoordinateResolver
{
    public static Vector3 ResolveAxisVector(MachinePackage machine, Vector3 machineVector) =>
        NormalizeOrFallback(machineVector, machineVector);

    /// <summary>
    /// Returns the physical axis pivot authored in NX Machine Tool Builder.
    /// PART_MOUNT_JCT is the workpiece attachment frame; it must not replace a
    /// different physical B-axis junction.
    /// </summary>
    public static Vector3 ResolveAxisPivot(MachinePackage machine, AxisDefinition axis) =>
        machine.Junctions.FirstOrDefault(x =>
            x.Name.Equals(axis.Junction, StringComparison.OrdinalIgnoreCase))?.Origin ?? Vector3.Zero;

    public static CoordinateFrame ResolveTableFrame(MachinePackage machine, Bounds3? tableBounds = null)
    {
        var junction = machine.Junctions.FirstOrDefault(x =>
            x.Name.Equals("PART_MOUNT_JCT", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(".trmac içinde PART_MOUNT_JCT tabla merkezi yok.");

        var m = junction.Orientation;
        return new CoordinateFrame(
            junction.Name,
            junction.Origin,
            NormalizeOrFallback(new Vector3(m.M11, m.M12, m.M13), Vector3.UnitX),
            NormalizeOrFallback(new Vector3(m.M21, m.M22, m.M23), Vector3.UnitY),
            NormalizeOrFallback(new Vector3(m.M31, m.M32, m.M33), Vector3.UnitZ),
            "trmac:canonical-kim-junction");
    }

    public static Vector3 TransformWorkpiecePoint(MachinePackage machine, Vector3 point, double b, double c)
        => Vector3.Transform(point, BuildWorkpieceTransform(machine, b, c));

    public static Vector3 InverseTransformWorkpiecePoint(MachinePackage machine, Vector3 point, double b, double c)
    {
        var transform = BuildWorkpieceTransform(machine, b, c);
        if (!Matrix4x4.Invert(transform, out var inverse))
            throw new InvalidDataException("B/C iş parçası dönüş matrisi terslenemiyor.");
        return Vector3.Transform(point, inverse);
    }

    /// <summary>
    /// Returns the exact local transform consumed by a kinematic scene node.
    /// The same matrix is also used by the TCP/work-frame resolver so rendered
    /// geometry and calculated machine coordinates cannot drift to different pivots.
    /// </summary>
    public static Matrix4x4 BuildAxisDeltaTransform(MachinePackage machine, AxisDefinition axis, double position)
    {
        var vector = ResolveAxisVector(machine, axis.Vector);
        var delta = position - axis.InitialPosition;
        if (vector.LengthSquared() <= 1e-12f || Math.Abs(delta) < 1e-12) return Matrix4x4.Identity;

        if (!axis.IsRotary)
            return Matrix4x4.CreateTranslation(vector * (float)delta);

        var pivot = ResolveAxisPivot(machine, axis);
        var radians = (float)(delta * Math.PI / 180.0);
        var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(vector), radians);
        return Matrix4x4.CreateTranslation(-pivot)
            * rotation
            * Matrix4x4.CreateTranslation(pivot);
    }

    public static Matrix4x4 BuildWorkpieceTransform(MachinePackage machine, double b, double c)
    {
        var cAxis = FindRotaryAxis(machine, 'C');
        var bAxis = FindRotaryAxis(machine, 'B');
        var cTransform = cAxis is null ? Matrix4x4.Identity : BuildAxisDeltaTransform(machine, cAxis, c);
        var bTransform = bAxis is null ? Matrix4x4.Identity : BuildAxisDeltaTransform(machine, bAxis, b);
        // MACHINE_BASE -> B_AXIS -> C-AXIS -> SETUP means local C is applied
        // first and the parent B transform is applied second.
        return cTransform * bTransform;
    }

    /// <summary>
    /// Resolves the SINUMERIK CYCLE800 A/B plane angles to the physical B/C
    /// targets of a dual-table machine whose rotary vectors are -Y and -Z.
    /// The public smoke tests compare this mapping with synthetic matrix
    /// normal-alignment reference values. Machine-specific validation remains
    /// necessary before using results outside simulation.
    /// </summary>
    public static bool TryResolveCycle800DualTableAngles(
        MachinePackage machine,
        double planeA,
        double planeB,
        double planeC,
        out double targetB,
        out double targetC)
    {
        return TryResolveCycle800DualTablePose(
            machine, planeA, planeB, planeC,
            out targetB, out targetC, out _);
    }

    public static bool TryResolveCycle800DualTablePose(
        MachinePackage machine,
        double planeA,
        double planeB,
        double planeC,
        out double targetB,
        out double targetC,
        out double frameRotationZ)
    {
        targetB = 0;
        targetC = 0;
        frameRotationZ = 0;
        var bAxis = FindRotaryAxis(machine, 'B');
        var cAxis = FindRotaryAxis(machine, 'C');
        if (bAxis is null || cAxis is null) return false;

        var bVector = ResolveAxisVector(machine, bAxis.Vector);
        var cVector = ResolveAxisVector(machine, cAxis.Vector);
        if (Vector3.Distance(bVector, -Vector3.UnitY) > 0.001f ||
            Vector3.Distance(cVector, -Vector3.UnitZ) > 0.001f ||
            Math.Abs(planeC) > 0.000001)
            return false;

        var a = planeA * Math.PI / 180.0;
        var b = planeB * Math.PI / 180.0;
        var cosInclination = Math.Clamp(Math.Cos(a) * Math.Cos(b), -1.0, 1.0);
        targetB = Math.Acos(cosInclination) * 180.0 / Math.PI;
        targetC = NormalizeDegrees(Math.Atan2(
            -Math.Sin(a) * Math.Cos(b),
            Math.Sin(b)) * 180.0 / Math.PI);
        return TryResolveCycle800FrameRotation(
            planeA, planeB, planeC, targetB, targetC, out frameRotationZ);
    }

    /// <summary>
    /// Decomposes the programmed X/Y/Z swivel plane against the physical B/C
    /// table pose.  The remaining pure-Z rotation is the SINUMERIK active-frame
    /// rotation seen in $P_ACTFRAME[Z,RT].
    /// </summary>
    public static bool TryResolveCycle800FrameRotation(
        double planeA,
        double planeB,
        double planeC,
        double physicalB,
        double physicalC,
        out double frameRotationZ)
    {
        // Decompose the programmed swivel plane by the physical table pose.
        // The previous tan(A) shortcut discarded the Euler quadrant once |A|
        // crossed 90 degrees.  This made the residual frame 180 degrees
        // wrong for high-tilt orientations even though B/C were
        // correct.  Matrix decomposition preserves that quadrant and leaves
        // the controller's pure in-plane Z rotation.
        var programmedPlane =
            Matrix4x4.CreateRotationY(ToRadians(planeB)) *
            Matrix4x4.CreateRotationX(ToRadians(planeA)) *
            Matrix4x4.CreateRotationZ(ToRadians(planeC));
        var physicalTable =
            Matrix4x4.CreateFromAxisAngle(-Vector3.UnitZ, ToRadians(physicalC)) *
            Matrix4x4.CreateFromAxisAngle(-Vector3.UnitY, ToRadians(physicalB));
        var residual = programmedPlane * physicalTable;

        frameRotationZ = Math.Atan2(residual.M12, residual.M11) * 180.0 / Math.PI;
        if (frameRotationZ > 180) frameRotationZ -= 360;
        if (frameRotationZ <= -180) frameRotationZ += 360;
        return true;
    }

    /// <summary>
    /// Builds the controller's active machine-space work frame after CYCLE800.
    /// Its origin follows the physical B/C table around the authored pivots;
    /// its axes contain only the residual in-plane rotation reported by the
    /// controller.  XYZ mapping must consume this frame directly and must not
    /// apply the physical table transform a second time.
    /// </summary>
    public static CoordinateFrame BuildIndexedWorkFrame(
        MachinePackage machine,
        CoordinateFrame baseFrame,
        double b,
        double c,
        double frameRotationZ)
    {
        var radians = ToRadians(frameRotationZ);
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        return new CoordinateFrame(
            baseFrame.Label + ":CYCLE800",
            TransformWorkpiecePoint(machine, baseFrame.Origin, b, c),
            NormalizeOrFallback(baseFrame.XAxis * cosine + baseFrame.YAxis * sine, baseFrame.XAxis),
            NormalizeOrFallback(baseFrame.XAxis * -sine + baseFrame.YAxis * cosine, baseFrame.YAxis),
            baseFrame.ZAxis,
            "sinumerik:CYCLE800-controller-active-frame");
    }

    private static float ToRadians(double degrees) => (float)(degrees * Math.PI / 180.0);

    private static double NormalizeDegrees(double value)
    {
        value %= 360.0;
        if (value < 0) value += 360.0;
        return Math.Abs(value - 360.0) < 0.000001 ? 0 : value;
    }

    private static AxisDefinition? FindRotaryAxis(MachinePackage machine, char address) =>
        machine.Axes.FirstOrDefault(x =>
            x.IsRotary && x.Name.Length > 0 && char.ToUpperInvariant(x.Name[0]) == address);

    private static Vector3 NormalizeOrFallback(Vector3 vector, Vector3 fallback) =>
        vector.LengthSquared() > 1e-12f ? Vector3.Normalize(vector) : fallback;
}
