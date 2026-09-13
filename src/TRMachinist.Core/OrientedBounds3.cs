using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// A rigidly transformed local bounding box.  The SAT test is deliberately
/// independent from WPF/DirectX so the collision contract can be smoke-tested
/// without opening a graphics device.
/// </summary>
public readonly record struct OrientedBounds3(
    Vector3 Center,
    Vector3 AxisX,
    Vector3 AxisY,
    Vector3 AxisZ,
    Vector3 HalfSize)
{
    public static OrientedBounds3 Transform(Bounds3 localBounds, Matrix4x4 transform)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, transform);
        var y = Vector3.TransformNormal(Vector3.UnitY, transform);
        var z = Vector3.TransformNormal(Vector3.UnitZ, transform);
        var sx = Math.Max(x.Length(), 1e-9f);
        var sy = Math.Max(y.Length(), 1e-9f);
        var sz = Math.Max(z.Length(), 1e-9f);
        return new OrientedBounds3(
            Vector3.Transform(localBounds.Center, transform),
            x / sx,
            y / sy,
            z / sz,
            new Vector3(
                Math.Abs(localBounds.Size.X) * 0.5f * sx,
                Math.Abs(localBounds.Size.Y) * 0.5f * sy,
                Math.Abs(localBounds.Size.Z) * 0.5f * sz));
    }

    public Bounds3 ToAxisAlignedBounds()
    {
        var half = Vector3.Abs(AxisX) * HalfSize.X +
                   Vector3.Abs(AxisY) * HalfSize.Y +
                   Vector3.Abs(AxisZ) * HalfSize.Z;
        return new Bounds3(Center - half, Center + half);
    }

    /// <summary>
    /// Tests all 15 separating axes for two OBBs.  clearanceMm is the total
    /// requested air gap between the boxes, not an expansion per body.
    /// </summary>
    public bool Intersects(OrientedBounds3 other, double clearanceMm = 0)
    {
        Span<Vector3> a = stackalloc Vector3[3] { AxisX, AxisY, AxisZ };
        Span<Vector3> b = stackalloc Vector3[3] { other.AxisX, other.AxisY, other.AxisZ };
        var margin = (float)Math.Max(0, clearanceMm) * 0.5f;
        Span<float> ae = stackalloc float[3] { HalfSize.X + margin, HalfSize.Y + margin, HalfSize.Z + margin };
        Span<float> be = stackalloc float[3] { other.HalfSize.X + margin, other.HalfSize.Y + margin, other.HalfSize.Z + margin };
        Span<float> r = stackalloc float[9];
        Span<float> absR = stackalloc float[9];
        const float parallelEpsilon = 1e-6f;
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var index = i * 3 + j;
            r[index] = Vector3.Dot(a[i], b[j]);
            absR[index] = Math.Abs(r[index]) + parallelEpsilon;
        }

        var delta = other.Center - Center;
        Span<float> t = stackalloc float[3]
            { Vector3.Dot(delta, a[0]), Vector3.Dot(delta, a[1]), Vector3.Dot(delta, a[2]) };

        for (var i = 0; i < 3; i++)
        {
            var row = i * 3;
            var radiusB = be[0] * absR[row] + be[1] * absR[row + 1] + be[2] * absR[row + 2];
            if (Math.Abs(t[i]) > ae[i] + radiusB) return false;
        }

        for (var j = 0; j < 3; j++)
        {
            var projection = Math.Abs(t[0] * r[j] + t[1] * r[3 + j] + t[2] * r[6 + j]);
            var radiusA = ae[0] * absR[j] + ae[1] * absR[3 + j] + ae[2] * absR[6 + j];
            if (projection > radiusA + be[j]) return false;
        }

        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var i1 = (i + 1) % 3;
            var i2 = (i + 2) % 3;
            var j1 = (j + 1) % 3;
            var j2 = (j + 2) % 3;
            var projection = Math.Abs(t[i2] * r[i1 * 3 + j] - t[i1] * r[i2 * 3 + j]);
            var radiusA = ae[i1] * absR[i2 * 3 + j] + ae[i2] * absR[i1 * 3 + j];
            var radiusB = be[j1] * absR[i * 3 + j2] + be[j2] * absR[i * 3 + j1];
            if (projection > radiusA + radiusB) return false;
        }

        return true;
    }
}
