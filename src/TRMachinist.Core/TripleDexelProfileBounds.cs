using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private readonly record struct ProfileRayBounds(Vector3 Minimum, Vector3 Maximum)
    {
        public bool Contains(Vector3 origin) => origin.X >= Minimum.X && origin.X <= Maximum.X &&
            origin.Y >= Minimum.Y && origin.Y <= Maximum.Y && origin.Z >= Minimum.Z && origin.Z <= Maximum.Z;
    }

    // Broad phase only. The finite cylinder/cone query remains the cutting
    // evidence. A profile section usually occupies a small fraction of the
    // complete cutter box, especially near a curved nose.
    // Applied to individual static samples, never to continuous sweep queries.
    private static ProfileRayBounds PrepareStaticProfileRayBounds(in PreparedCylinderRay kernel)
    {
        // Existing constant-radius kernels use float projection arithmetic.
        // Do not impose the double cone's geometric bound on that numerical
        // contract. Keep their original query until independently migrated.
        if (kernel.RadiusSlope == 0)
            return new(new(float.NegativeInfinity), new(float.PositiveInfinity));
        double norm = Math.Sqrt((double)kernel.Axis.X * kernel.Axis.X +
            (double)kernel.Axis.Y * kernel.Axis.Y + (double)kernel.Axis.Z * kernel.Axis.Z);
        if (!(norm > 0) || !double.IsFinite(norm))
            return new(new(float.NegativeInfinity), new(float.PositiveInfinity));
        double firstRadius = kernel.StartRadius;
        double lastRadius = kernel.StartRadius + kernel.RadiusSlope * kernel.Length;
        double radius = Math.Max(Math.Abs(firstRadius), Math.Abs(lastRadius));
        var x = Extent(kernel.Tip.X, kernel.Axis.X / norm, kernel.Direction.X, kernel.Length, firstRadius, lastRadius, radius);
        var y = Extent(kernel.Tip.Y, kernel.Axis.Y / norm, kernel.Direction.Y, kernel.Length, firstRadius, lastRadius, radius);
        var z = Extent(kernel.Tip.Z, kernel.Axis.Z / norm, kernel.Direction.Z, kernel.Length, firstRadius, lastRadius, radius);
        return new(new(x.Min, y.Min, z.Min), new(x.Max, y.Max, z.Max));

        static (float Min, float Max) Extent(double tip, double axis, float direction,
            double length, double firstRadius, double lastRadius, double radius)
        {
            // A stock ray's longitudinal origin is arbitrary; reject only
            // coordinates perpendicular to that ray.
            if (direction != 0) return (float.NegativeInfinity, float.PositiveInfinity);
            double disk = Math.Sqrt(Math.Max(0, 1 - axis * axis));
            double back = tip + axis * length;
            double low = Math.Min(tip - disk * firstRadius, back - disk * lastRadius);
            double high = Math.Max(tip + disk * firstRadius, back + disk * lastRadius);
            // Conservative allowance for float input coordinates and offsets,
            // including large work-coordinate origins.
            double padding = 1e-4 + 8 * 1.1920928955078125e-7 * (Math.Abs(tip) + Math.Abs(length) + radius + 1);
            return (float.BitDecrement((float)(low - padding)), float.BitIncrement((float)(high + padding)));
        }
    }
}
