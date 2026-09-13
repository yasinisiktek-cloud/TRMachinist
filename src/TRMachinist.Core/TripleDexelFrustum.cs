using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private readonly record struct PreparedFrustumBasis(D3 Axis, D3 RadialDirection,
        double AxialDirection, double RadiusDirection, double Quadratic, bool IsLinear);

    private static PreparedFrustumBasis PrepareFrustumBasis(Vector3 axis, Vector3 direction, double slope)
    {
        var inverseLength = 1 / Math.Sqrt((double)axis.X * axis.X +
            (double)axis.Y * axis.Y + (double)axis.Z * axis.Z);
        double ax=axis.X*inverseLength, ay=axis.Y*inverseLength, az=axis.Z*inverseLength;
        double axialDirection=direction.X*ax+direction.Y*ay+direction.Z*az;
        double dx=direction.X-ax*axialDirection, dy=direction.Y-ay*axialDirection, dz=direction.Z-az*axialDirection;
        double directionSquared=dx*dx+dy*dy+dz*dz;
        double radiusDirection=slope*axialDirection;
        double a=directionSquared-radiusDirection*radiusDirection;
        return new(new(ax,ay,az),new(dx,dy,dz),axialDirection,radiusDirection,a,
            Math.Abs(a)<=1e-12*Math.Max(1,directionSquared+radiusDirection*radiusDirection));
    }

    // Exact line/finite-frustum intersection. No axial staircase or bounding
    // cylinder is used as cutting evidence. Existing cylindrical kernels stay
    // on their original arithmetic and fast paths.
    private static bool PreparedFrustumLineInterval(
        Vector3 origin, in PreparedCylinderRay kernel,
        out double minimum, out double maximum)
    {
        // Solve around the tool, not around world coordinate zero. This avoids
        // cancelling two large squared coordinates for a remote work origin.
        var ox = (double)origin.X - kernel.Tip.X;
        var oy = (double)origin.Y - kernel.Tip.Y;
        var oz = (double)origin.Z - kernel.Tip.Z;
        var anchor = -(ox * kernel.Direction.X + oy * kernel.Direction.Y + oz * kernel.Direction.Z);
        var intersects = FrustumLocalLineInterval(kernel,
            ox + anchor * kernel.Direction.X, oy + anchor * kernel.Direction.Y,
            oz + anchor * kernel.Direction.Z, out minimum, out maximum);
        if (intersects) { minimum += anchor; maximum += anchor; }
        return intersects;
    }

    private static bool FrustumLocalLineInterval(in PreparedCylinderRay kernel,
        double ox, double oy, double oz, out double minimum, out double maximum)
    {
        const double epsilon = 1e-10;
        var basis=kernel.FrustumBasis;
        var ax=basis.Axis.X; var ay=basis.Axis.Y; var az=basis.Axis.Z;
        var axialOrigin = ox * ax + oy * ay + oz * az;
        var axialDirection = basis.AxialDirection;
        minimum = double.NegativeInfinity;
        maximum = double.PositiveInfinity;
        if (Math.Abs(axialDirection) <= epsilon)
        {
            if (axialOrigin < -epsilon || axialOrigin > kernel.Length + epsilon) return false;
        }
        else
        {
            minimum = -axialOrigin / axialDirection;
            maximum = (kernel.Length - axialOrigin) / axialDirection;
            if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
        }

        var rx = ox - ax * axialOrigin;
        var ry = oy - ay * axialOrigin;
        var rz = oz - az * axialOrigin;
        var dx=basis.RadialDirection.X; var dy=basis.RadialDirection.Y; var dz=basis.RadialDirection.Z;
        var radiusAtOrigin = kernel.StartRadius + kernel.RadiusSlope * axialOrigin;
        var radiusDirection = basis.RadiusDirection;
        var a = basis.Quadratic;
        var b = 2 * (rx * dx + ry * dy + rz * dz - radiusAtOrigin * radiusDirection);
        var c = rx * rx + ry * ry + rz * rz - radiusAtOrigin * radiusAtOrigin;
        // Clip the finite axial slab by a*t²+b*t+c <= 0.
        if (basis.IsLinear)
        {
            if (Math.Abs(b) <= 1e-12)
                return c <= epsilon && maximum > minimum + epsilon;
            var root = -c / b;
            if (b > 0) maximum = Math.Min(maximum, root);
            else minimum = Math.Max(minimum, root);
            return maximum > minimum + epsilon;
        }

        var discriminant = b * b - 4 * a * c;
        var tolerance = 1e-12 * Math.Max(1, b * b + Math.Abs(4 * a * c));
        if (discriminant < -tolerance)
            return a < 0 && maximum > minimum + epsilon;
        var squareRoot = Math.Sqrt(Math.Max(0, discriminant));
        // Stable quadratic roots also retain the near intersection at large
        // machine coordinates; subtracting two nearly equal values loses it.
        var q = -0.5 * (b + Math.CopySign(squareRoot, b));
        var first = q / a;
        var second = q == 0 ? -b / (2 * a) : c / q;
        if (first > second) (first, second) = (second, first);
        if (a > 0)
        {
            minimum = Math.Max(minimum, first);
            maximum = Math.Min(maximum, second);
        }
        else
        {
            // The unbounded double cone has two branches. Nonnegative end
            // radii and the finite slab select at most one (convex) interval.
            if (Math.Min(maximum, first) > minimum + epsilon)
                maximum = Math.Min(maximum, first);
            else minimum = Math.Max(minimum, second);
        }
        return maximum > minimum + epsilon;
    }
}
