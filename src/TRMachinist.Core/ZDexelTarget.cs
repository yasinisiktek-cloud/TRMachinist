using System.Numerics;

namespace TRMachinist.Core;

public readonly record struct ZDexelTargetComparison(
    double TargetVolume,
    double MissingTargetVolume,
    double ExcessStockVolume);

/// <summary>
/// Closed target STL sampled on the exact XY rays of a TripleDexelStock.  It is
/// used only for verification: the target never clamps or repairs the simulated
/// stock, so a real gouge remains visible and measurable.
/// </summary>
public sealed class ZDexelTarget
{
    internal readonly struct TargetRay
    {
        private readonly DexelSpan _single;
        private readonly DexelSpan[]? _multiple;

        public TargetRay(DexelSpan single)
        {
            _single = single;
            _multiple = null;
            Count = 1;
        }

        public TargetRay(DexelSpan[] spans)
        {
            if (spans.Length == 1)
            {
                _single = spans[0];
                _multiple = null;
                Count = 1;
            }
            else
            {
                _single = default;
                _multiple = spans.Length == 0 ? null : spans;
                Count = spans.Length;
            }
        }

        public int Count { get; }
        public double Length
        {
            get
            {
                if (Count == 0) return 0;
                if (_multiple is null) return _single.Length;
                var length = 0.0;
                for (var index = 0; index < _multiple.Length; index++)
                    length += _multiple[index].Length;
                return length;
            }
        }
        public DexelSpan this[int index] => _multiple is null
            ? index == 0 && Count == 1 ? _single : throw new ArgumentOutOfRangeException(nameof(index))
            : _multiple[index];
        public double Top(double fallback) => Count == 0 ? fallback : this[Count - 1].Max;
        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly TargetRay _ray;
            private int _index;

            internal Enumerator(TargetRay ray)
            {
                _ray = ray;
                _index = -1;
            }

            public DexelSpan Current => _ray[_index];
            public bool MoveNext() => ++_index < _ray.Count;
        }
    }

    private struct HitAccumulator
    {
        private double _first;
        private double _second;
        private List<double>? _overflow;
        private int _count;

        public void Add(double value)
        {
            if (_count == 0) _first = value;
            else if (_count == 1) _second = value;
            else
            {
                _overflow ??= new List<double>(4) { _first, _second };
                _overflow.Add(value);
            }
            _count++;
        }

        public TargetRay BuildRay()
        {
            if (_count < 2) return default;
            if (_overflow is null)
            {
                var minimum = Math.Min(_first, _second);
                var maximum = Math.Max(_first, _second);
                return maximum > minimum + 1e-8
                    ? new TargetRay(new DexelSpan(minimum, maximum))
                    : default;
            }

            var sorted = _overflow.ToArray();
            Array.Sort(sorted);
            var uniqueCount = 0;
            for (var index = 0; index < sorted.Length; index++)
                if (uniqueCount == 0 || Math.Abs(sorted[index] - sorted[uniqueCount - 1]) > 1e-5)
                    sorted[uniqueCount++] = sorted[index];

            var validSpanCount = 0;
            for (var index = 0; index + 1 < uniqueCount; index += 2)
                if (sorted[index + 1] > sorted[index] + 1e-8)
                    validSpanCount++;
            if (validSpanCount == 0) return default;
            if (validSpanCount == 1)
            {
                for (var index = 0; index + 1 < uniqueCount; index += 2)
                    if (sorted[index + 1] > sorted[index] + 1e-8)
                        return new TargetRay(new DexelSpan(sorted[index], sorted[index + 1]));
            }

            var spans = new DexelSpan[validSpanCount];
            var destination = 0;
            for (var index = 0; index + 1 < uniqueCount; index += 2)
                if (sorted[index + 1] > sorted[index] + 1e-8)
                    spans[destination++] = new DexelSpan(sorted[index], sorted[index + 1]);
            return new TargetRay(spans);
        }
    }

    private readonly TargetRay[] _rays;

    internal ZDexelTarget(TriangleMeshData mesh, IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        X = x.ToArray();
        Y = y.ToArray();
        XWeights = IntegrationWeights(X);
        YWeights = IntegrationWeights(Y);
        // Most closed-part Z rays have exactly two crossings. Store those two
        // values inline in one contiguous array; allocate a list only for rays
        // that genuinely cross a complex target more than twice.
        var hits = new HitAccumulator[X.Length * Y.Length];

        for (var triangle = 0; triangle < mesh.Indices.Length; triangle += 3)
        {
            var a = Point(mesh, mesh.Indices[triangle]);
            var b = Point(mesh, mesh.Indices[triangle + 1]);
            var c = Point(mesh, mesh.Indices[triangle + 2]);
            var denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
            if (Math.Abs(denominator) <= 1e-12) continue; // vertical face: no isolated Z crossing

            var firstX = LowerBound(X, Math.Min(a.X, Math.Min(b.X, c.X)) - 1e-7);
            var lastX = UpperBound(X, Math.Max(a.X, Math.Max(b.X, c.X)) + 1e-7) - 1;
            var firstY = LowerBound(Y, Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 1e-7);
            var lastY = UpperBound(Y, Math.Max(a.Y, Math.Max(b.Y, c.Y)) + 1e-7) - 1;
            for (var yi = firstY; yi <= lastY; yi++)
            for (var xi = firstX; xi <= lastX; xi++)
            {
                var px = X[xi];
                var py = Y[yi];
                var wa = (((b.Y - c.Y) * (px - c.X)) + ((c.X - b.X) * (py - c.Y))) / denominator;
                var wb = (((c.Y - a.Y) * (px - c.X)) + ((a.X - c.X) * (py - c.Y))) / denominator;
                var wc = 1.0 - wa - wb;
                const double epsilon = 1e-8;
                if (wa < -epsilon || wb < -epsilon || wc < -epsilon) continue;
                hits[(yi * X.Length) + xi].Add((wa * a.Z) + (wb * b.Z) + (wc * c.Z));
            }
        }

        _rays = new TargetRay[hits.Length];
        for (var ray = 0; ray < hits.Length; ray++)
            _rays[ray] = hits[ray].BuildRay();
    }

    internal double[] X { get; }
    internal double[] Y { get; }
    internal double[] XWeights { get; }
    internal double[] YWeights { get; }
    internal TargetRay Ray(int x, int y) => _rays[(y * X.Length) + x];
    internal double Top(int x, int y, double fallback) =>
        _rays[(y * X.Length) + x].Top(fallback);

    // A surface is a gouge only when it lies within occupied target intervals,
    // with room on every side. The old highest-Z comparison painted valid
    // cavity floors and side openings red beneath unrelated upper material.
    // Neighbouring rays conservatively account for the sampled XY boundary;
    // this classification never clamps stock or changes measured lost volume.
    internal bool IsInterior(Vector3 point, double margin)
    {
        if (point.X - margin < X[0] || point.X + margin > X[^1] ||
            point.Y - margin < Y[0] || point.Y + margin > Y[^1]) return false;
        Span<int> xs = stackalloc int[3] {
            Math.Max(0, UpperBound(X, point.X - margin) - 1),
            LowerBound(X, point.X), LowerBound(X, point.X + margin) };
        Span<int> ys = stackalloc int[3] {
            Math.Max(0, UpperBound(Y, point.Y - margin) - 1),
            LowerBound(Y, point.Y), LowerBound(Y, point.Y + margin) };
        foreach (var y in ys)
        foreach (var x in xs)
        {
            var occupied = false;
            foreach (var span in Ray(x, y))
                if (point.Z - margin > span.Min && point.Z + margin < span.Max)
                { occupied = true; break; }
            if (!occupied) return false;
        }
        return true;
    }

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

    private static double[] IntegrationWeights(IReadOnlyList<double> coordinates)
    {
        var weights = new double[coordinates.Count];
        for (var index = 0; index < coordinates.Count; index++)
        {
            var left = index == 0 ? coordinates[0] : (coordinates[index - 1] + coordinates[index]) * 0.5;
            var right = index == coordinates.Count - 1
                ? coordinates[^1]
                : (coordinates[index] + coordinates[index + 1]) * 0.5;
            weights[index] = right - left;
        }
        return weights;
    }
}
