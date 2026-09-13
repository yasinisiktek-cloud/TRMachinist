using System.Numerics;

namespace TRMachinist.Core;

public static class CoordinateTransforms
{
    public static Matrix4x4 BuildPlacement(CoordinateFrame sourceMount, CoordinateFrame targetMount)
    {
        var source = FrameMatrix(sourceMount);
        if (!Matrix4x4.Invert(source, out var inverseSource))
            throw new InvalidDataException("Seçilen machineMountCsys matrisi terslenemiyor.");
        return inverseSource * FrameMatrix(targetMount);
    }

    public static Matrix4x4 FrameMatrix(CoordinateFrame frame) => new(
        frame.XAxis.X, frame.XAxis.Y, frame.XAxis.Z, 0,
        frame.YAxis.X, frame.YAxis.Y, frame.YAxis.Z, 0,
        frame.ZAxis.X, frame.ZAxis.Y, frame.ZAxis.Z, 0,
        frame.Origin.X, frame.Origin.Y, frame.Origin.Z, 1);

    public static Vector3 TransformPoint(Vector3 point, Matrix4x4 placement) => Vector3.Transform(point, placement);

    /// <summary>
    /// Applies a user setup correction in the target mount frame's X/Y/Z axes.
    /// Only the target origin changes; orientation, scale and the source CSYS
    /// contract remain untouched.
    /// </summary>
    public static Matrix4x4 ApplyTargetFrameOffset(
        Matrix4x4 placement,
        CoordinateFrame targetMount,
        Vector3 localOffset)
    {
        var delta = (targetMount.XAxis * localOffset.X)
                    + (targetMount.YAxis * localOffset.Y)
                    + (targetMount.ZAxis * localOffset.Z);
        placement.M41 += delta.X;
        placement.M42 += delta.Y;
        placement.M43 += delta.Z;
        return placement;
    }
}
