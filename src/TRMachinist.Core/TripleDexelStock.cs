using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// One occupied interval on a dexel ray.  End points are stored as doubles so
/// repeated cuts remain deterministic and do not accumulate float drift.
/// </summary>
public readonly record struct DexelSpan(double Min, double Max)
{
    public double Length => Math.Max(0, Max - Min);
}

public readonly record struct TripleDexelVolume(double X, double Y, double Z)
{
    public double Mean => (X + Y + Z) / 3.0;
    public double Spread => Math.Max(X, Math.Max(Y, Z)) - Math.Min(X, Math.Min(Y, Z));
}

public readonly record struct TripleDexelCutResult(
    TripleDexelVolume Removed,
    TripleDexelVolume Remaining,
    bool WasRapid)
{
    public bool RemovedNothing => Math.Abs(Removed.X) < 1e-9 &&
                                  Math.Abs(Removed.Y) < 1e-9 &&
                                  Math.Abs(Removed.Z) < 1e-9;
}

public readonly record struct LayeredCutterPose(Vector3 Tip, Vector3 Axis);

/// <summary>
/// Independent three-direction interval stock.  G0 is classified before
/// geometry and therefore cannot alter stock.  In addition to the original
/// constant-Z audit primitive, the stock accepts a sampled, arbitrarily
/// oriented finite cylindrical cutter sweep for NC-driven material removal.
/// </summary>
public sealed partial class TripleDexelStock
{
    private const int RecentFiniteCylinderStampCapacity = 65_536;
    private readonly record struct FiniteCylinderStampKey(
        Vector3 Tip,
        Vector3 Axis,
        long RadiusBits,
        long LengthBits);

    private readonly record struct ProfileLayerSpan(
        double Minimum,
        double Maximum,
        double Radius)
    {
        internal double? StartRadius { get; init; }
        internal double MaximumRadius => Math.Max(Radius, StartRadius ?? Radius);
        internal double RadiusAt(double coordinate) => StartRadius is double start
            ? start + (Radius - start) * Math.Clamp((coordinate - Minimum) / (Maximum - Minimum), 0, 1)
            : Radius;
        internal bool IntervalAt(double distanceSquared, out double lo, out double hi)
        {
            lo = Minimum; hi = Maximum;
            if (distanceSquared > MaximumRadius * MaximumRadius + 1e-9) return false;
            if (StartRadius is double start && start != Radius)
            {
                var distance = Math.Sqrt(Math.Max(0, distanceSquared));
                var crossing = Minimum + (Maximum - Minimum) * Math.Clamp((distance - start) / (Radius - start), 0, 1);
                if (Radius > start) lo = crossing; else hi = crossing;
            }
            return hi > lo;
        }
    }

    private static bool IsContinuousIncreasingProfile(IReadOnlyList<CutterCylinderLayer> layers)
    {
        if (layers.Count == 0) return false;
        double end = layers[0].AxialOffset, radius = layers[0].StartRadius ?? layers[0].Radius;
        foreach (var layer in layers)
        {
            var start = layer.StartRadius ?? layer.Radius;
            if (Math.Abs(layer.AxialOffset - end) > 1e-8 || Math.Abs(start - radius) > 1e-8 || layer.Radius < start)
                return false;
            end = layer.AxialOffset + layer.Length; radius = layer.Radius;
        }
        return true;
    }

    private readonly record struct CylinderRayInterval(double Minimum, double Maximum);

    private readonly record struct PreparedCylinderRay(
        Vector3 Tip,
        Vector3 Axis,
        double Length,
        double RadiusSquared,
        double AxialDirection,
        Vector3 RadialDirection,
        double RadialDirectionSquared,
        double StartRadius,
        double RadiusSlope,
        Vector3 Direction,
        PreparedFrustumBasis FrustumBasis);

    private struct DexelRay
    {
        private readonly record struct Segment(DexelSpan Span, int MinimumTag, int MaximumTag);

        // A newly created stock ray almost always contains exactly one span.
        // Three List<T> objects (and their three backing arrays) per ray made a
        // 0.15 mm 150x150x62 triple-dexel stock consume hundreds of megabytes
        // before the first cut. Keep that overwhelmingly common case inline;
        // allocate a list only when a cutter genuinely splits a ray.
        private Segment _single;
        private List<Segment>? _multiple;
        private int _singleCount;

        public DexelRay(double min, double max) : this(new[] { new DexelSpan(min, max) }) { }

        public DexelRay(IEnumerable<DexelSpan> spans)
        {
            _single = default;
            _multiple = null;
            _singleCount = 0;
            foreach (var span in spans.Where(span => span.Length > 1e-10))
            {
                var segment = new Segment(span, 0, 0);
                if (_singleCount == 0 && _multiple is null)
                {
                    _single = segment;
                    _singleCount = 1;
                }
                else
                {
                    _multiple ??= new List<Segment>(2) { _single };
                    _multiple.Add(segment);
                }
            }
        }

        public DexelRay Spans => this;
        public int Count => _multiple?.Count ?? _singleCount;
        public DexelSpan this[int index] => SegmentAt(index).Span;
        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly DexelRay _ray;
            private int _index;

            internal Enumerator(DexelRay ray)
            {
                _ray = ray;
                _index = -1;
            }

            public DexelSpan Current => _ray[_index];
            public bool MoveNext() => ++_index < _ray.Count;
        }

        public double Length
        {
            get
            {
                var length = 0.0;
                for (var index = 0; index < Count; index++)
                    length += SegmentAt(index).Span.Length;
                return length;
            }
        }
        public double Top(double fallback) => Count == 0 ? fallback : SegmentAt(Count - 1).Span.Max;
        // Conservative rejection only; exact subtraction still handles every
        // occupied interval, including split rays and internal cavities.
        public bool MayOverlap(double minimum, double maximum) => Count > 0 &&
            SegmentAt(0).Span.Min <= maximum && SegmentAt(Count - 1).Span.Max >= minimum;
        public int TopTag => Count == 0 ? 0 : SegmentAt(Count - 1).MaximumTag;
        public bool Contains(double coordinate)
        {
            const double epsilon = 1e-8;
            for (var index = 0; index < Count; index++)
            {
                var span = SegmentAt(index).Span;
                if (coordinate >= span.Min - epsilon && coordinate <= span.Max + epsilon)
                    return true;
            }
            return false;
        }

        public bool TryBoundary(double minimum, double maximum, bool minimumOccupied, out double coordinate, out int tag)
        {
            const double epsilon = 1e-8;
            for (var index = 0; index < Count; index++)
            {
                var segment = SegmentAt(index);
                var span = segment.Span;
                if (minimumOccupied && span.Min <= minimum + epsilon && span.Max >= minimum - epsilon &&
                    span.Max >= minimum - epsilon && span.Max <= maximum + epsilon)
                {
                    coordinate = Math.Clamp(span.Max, minimum, maximum);
                    tag = segment.MaximumTag;
                    return true;
                }
                if (!minimumOccupied && span.Min >= minimum - epsilon && span.Min <= maximum + epsilon)
                {
                    coordinate = Math.Clamp(span.Min, minimum, maximum);
                    tag = segment.MinimumTag;
                    return true;
                }
            }

            coordinate = (minimum + maximum) * 0.5;
            tag = 0;
            return false;
        }

        public double Subtract(
            double cutMin,
            double cutMax,
            int cutTag,
            out double changedMin,
            out double changedMax)
        {
            changedMin = double.PositiveInfinity;
            changedMax = double.NegativeInfinity;
            if (cutMax <= cutMin || Count == 0) return 0;
            const double epsilon = 1e-10;
            var removed = 0.0;
            var first = 0;
            if (Count > 8)
            {
                // Spans are ordered and disjoint. Match the subtraction's
                // original epsilon predicate exactly; search skips only spans
                // wholly before the cut, including internal cavities.
                var end = Count;
                while (first < end)
                {
                    var middle = first + (end - first) / 2;
                    if (cutMin >= SegmentAt(middle).Span.Max - epsilon) first = middle + 1;
                    else end = middle;
                }
            }
            for (var index = first; index < Count; index++)
            {
                var segment = SegmentAt(index);
                var span = segment.Span;
                if (cutMax <= span.Min + epsilon) break;
                if (cutMin >= span.Max - epsilon) continue;

                var overlapMin = Math.Max(cutMin, span.Min);
                var overlapMax = Math.Min(cutMax, span.Max);
                if (overlapMax <= overlapMin + epsilon) continue;
                changedMin = Math.Min(changedMin, overlapMin);
                changedMax = Math.Max(changedMax, overlapMax);
                removed += overlapMax - overlapMin;

                var keepLeft = cutMin > span.Min + epsilon;
                var keepRight = cutMax < span.Max - epsilon;

                if (keepLeft && keepRight)
                {
                    var left = new Segment(
                        new DexelSpan(span.Min, cutMin),
                        segment.MinimumTag,
                        cutTag);
                    var right = new Segment(
                        new DexelSpan(cutMax, span.Max),
                        cutTag,
                        segment.MaximumTag);
                    SplitAt(index, left, right);
                    index++;
                }
                else if (keepLeft)
                {
                    SetAt(index, new Segment(
                        new DexelSpan(span.Min, cutMin),
                        segment.MinimumTag,
                        cutTag));
                }
                else if (keepRight)
                {
                    SetAt(index, new Segment(
                        new DexelSpan(cutMax, span.Max),
                        cutTag,
                        segment.MaximumTag));
                }
                else
                {
                    RemoveAt(index);
                    index--;
                }
            }

            CollapseIfCompact();

            if (removed <= 0)
            {
                changedMin = double.PositiveInfinity;
                changedMax = double.NegativeInfinity;
            }
            return Math.Max(0, removed);
        }

        private Segment SegmentAt(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _multiple is null ? _single : _multiple[index];
        }

        private void SetAt(int index, Segment segment)
        {
            if (_multiple is null)
            {
                if (index != 0 || _singleCount != 1) throw new ArgumentOutOfRangeException(nameof(index));
                _single = segment;
            }
            else
            {
                _multiple[index] = segment;
            }
        }

        private void SplitAt(int index, Segment left, Segment right)
        {
            if (_multiple is null)
            {
                if (index != 0 || _singleCount != 1) throw new ArgumentOutOfRangeException(nameof(index));
                _multiple = new List<Segment>(2) { left, right };
                _singleCount = 0;
            }
            else
            {
                _multiple[index] = left;
                _multiple.Insert(index + 1, right);
            }
        }

        private void RemoveAt(int index)
        {
            if (_multiple is null)
            {
                if (index != 0 || _singleCount != 1) throw new ArgumentOutOfRangeException(nameof(index));
                _single = default;
                _singleCount = 0;
            }
            else
            {
                _multiple.RemoveAt(index);
            }
        }

        private void CollapseIfCompact()
        {
            if (_multiple is null) return;
            if (_multiple.Count == 0)
            {
                _single = default;
                _singleCount = 0;
                _multiple = null;
            }
            else if (_multiple.Count == 1)
            {
                _single = _multiple[0];
                _singleCount = 1;
                _multiple = null;
            }
        }
    }

    private sealed class DexelField
    {
        private readonly DexelRay[] _rays;
        private double _volume;
        internal TargetComparisonCache? ComparisonCache;

        public DexelField(
            double longitudinalMin,
            double longitudinalMax,
            double[] u,
            double[] v,
            IReadOnlyList<DexelSpan[]> initialRays)
        {
            LongitudinalMin = longitudinalMin;
            LongitudinalMax = longitudinalMax;
            U = u;
            V = v;
            UWeights = IntegrationWeights(u);
            VWeights = IntegrationWeights(v);
            if (initialRays.Count != u.Length * v.Length)
                throw new ArgumentException("Başlangıç dexel ışını sayısı enine ızgarayla uyuşmuyor.", nameof(initialRays));
            _rays = new DexelRay[initialRays.Count];
            for (var index = 0; index < initialRays.Count; index++)
                _rays[index] = new DexelRay(initialRays[index]);
            _volume = 0;
            for (var vIndex = 0; vIndex < V.Length; vIndex++)
            for (var uIndex = 0; uIndex < U.Length; uIndex++)
                _volume += Ray(uIndex, vIndex).Length * UWeights[uIndex] * VWeights[vIndex];
        }

        public double LongitudinalMin { get; }
        public double LongitudinalMax { get; }
        public double[] U { get; }
        public double[] V { get; }
        public double[] UWeights { get; }
        public double[] VWeights { get; }

        public ref DexelRay Ray(int u, int v) => ref _rays[(v * U.Length) + u];

        // Every subtraction already returns its exactly integrated removed
        // volume.  Keep the remaining value incrementally instead of scanning
        // every dexel ray again on every animation tick.
        public double Volume() => _volume;

        public double Subtract(
            int u,
            int v,
            double min,
            double max,
            int cutTag,
            out double changedMin,
            out double changedMax)
        {
            var removedLength = Ray(u, v).Subtract(
                Math.Max(min, LongitudinalMin),
                Math.Min(max, LongitudinalMax),
                cutTag,
                out changedMin,
                out changedMax);
            var removed = removedLength * UWeights[u] * VWeights[v];
            if (removedLength > 0) ComparisonCache?.Changed((v * U.Length) + u);
            _volume = Math.Max(0, _volume - removed);
            return removed;
        }

        public double SubtractThreadSafe(
            int u,
            int v,
            double min,
            double max,
            int cutTag,
            out double changedMin,
            out double changedMax)
        {
            var removedLength = Ray(u, v).Subtract(
                Math.Max(min, LongitudinalMin),
                Math.Min(max, LongitudinalMax),
                cutTag,
                out changedMin,
                out changedMax);
            var removed = removedLength * UWeights[u] * VWeights[v];
            if (removed <= 0) return 0;
            ComparisonCache?.Changed((v * U.Length) + u);
            double current;
            double updated;
            do
            {
                current = Volatile.Read(ref _volume);
                updated = Math.Max(0, current - removed);
            }
            while (Interlocked.CompareExchange(ref _volume, updated, current) != current);
            return removed;
        }

        private static double[] IntegrationWeights(IReadOnlyList<double> coordinates)
        {
            var weights = new double[coordinates.Count];
            for (var i = 0; i < coordinates.Count; i++)
            {
                var left = i == 0 ? coordinates[0] : (coordinates[i - 1] + coordinates[i]) * 0.5;
                var right = i == coordinates.Count - 1
                    ? coordinates[^1]
                    : (coordinates[i] + coordinates[i + 1]) * 0.5;
                weights[i] = right - left;
            }
            return weights;
        }
    }

    private DexelField _xField = null!; // rays along X; transverse Y/Z
    private DexelField _yField = null!; // rays along Y; transverse X/Z
    private DexelField _zField = null!; // rays along Z; transverse X/Y
    private readonly double[] _xCoordinates;
    private readonly double[] _yCoordinates;
    private readonly double[] _zCoordinates;
    private readonly DexelSpan[][] _initialXRays;
    private readonly DexelSpan[][] _initialYRays;
    private readonly DexelSpan[][] _initialZRays;
    private readonly bool _surfaceCacheEnabled;
    private readonly HashSet<FiniteCylinderStampKey> _recentFiniteCylinderStamps = new();
    private readonly Queue<FiniteCylinderStampKey> _recentFiniteCylinderStampOrder = new();
    private Vector3 _lastLayeredStampTip;
    private Vector3 _lastLayeredStampAxis;
    private CutterCylinderLayer[]? _lastLayeredStampProfile;

    public TripleDexelStock(Bounds3 bounds, double targetPitch) : this(bounds, targetPitch, null) { }

    public TripleDexelStock(
        Bounds3 bounds,
        double targetPitch,
        TriangleMeshData? initialStockMesh,
        bool surfaceCacheEnabled = true)
    {
        if (bounds.Size.X <= 0 || bounds.Size.Y <= 0 || bounds.Size.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Dexel stok sınırları üç eksende de pozitif olmalı.");
        if (!double.IsFinite(targetPitch) || targetPitch <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetPitch));
        Bounds = bounds;
        TargetPitch = targetPitch;
        _surfaceCacheEnabled = surfaceCacheEnabled;
        _xCoordinates = Coordinates(Bounds.Min.X, Bounds.Max.X, TargetPitch);
        _yCoordinates = Coordinates(Bounds.Min.Y, Bounds.Max.Y, TargetPitch);
        _zCoordinates = Coordinates(Bounds.Min.Z, Bounds.Max.Z, TargetPitch);
        if (initialStockMesh is null || IsAxisAlignedBoxMesh(initialStockMesh, bounds))
        {
            _initialXRays = FullRayTemplates(_yCoordinates.Length * _zCoordinates.Length, Bounds.Min.X, Bounds.Max.X);
            _initialYRays = FullRayTemplates(_xCoordinates.Length * _zCoordinates.Length, Bounds.Min.Y, Bounds.Max.Y);
            _initialZRays = FullRayTemplates(_xCoordinates.Length * _yCoordinates.Length, Bounds.Min.Z, Bounds.Max.Z);
        }
        else
        {
            ValidateInitialStockMesh(initialStockMesh, bounds);
            _initialXRays = RasterizeInitialStock(initialStockMesh, Axis3.X, _yCoordinates, _zCoordinates);
            _initialYRays = RasterizeInitialStock(initialStockMesh, Axis3.Y, _xCoordinates, _zCoordinates);
            _initialZRays = RasterizeInitialStock(initialStockMesh, Axis3.Z, _xCoordinates, _yCoordinates);
            EnsureRasterizedStockIsUsable();
        }
        Reset();
    }

    public Bounds3 Bounds { get; }
    public double TargetPitch { get; }
    public int XSampleCount => _zField.U.Length;
    public int YSampleCount => _zField.V.Length;
    public int ZSampleCount => _xField.V.Length;
    public double XPitch => _zField.U[1] - _zField.U[0];
    public double YPitch => _zField.V[1] - _zField.V[0];
    public double ZPitch => _xField.V[1] - _xField.V[0];

    /// <summary>
    /// Chooses a geometry-derived uniform pitch with both a long-axis detail
    /// goal and a total-cell budget. Thin/long blanks therefore keep useful
    /// cross-section samples without exploding a compact cubic part to an
    /// unbounded grid. No workpiece dimensions are special-cased.
    /// </summary>
    public static double RecommendPitch(
        Bounds3 bounds,
        int longAxisSamples = 301,
        int thinAxisSamples = 21,
        long maximumCellCount = 12_000_000)
    {
        var extents = new[] { (double)bounds.Size.X, bounds.Size.Y, bounds.Size.Z };
        if (extents.Any(extent => !double.IsFinite(extent) || extent <= 0))
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if (longAxisSamples < 3 || thinAxisSamples < 3 || maximumCellCount < 1_000)
            throw new ArgumentOutOfRangeException(nameof(longAxisSamples));

        var pitch = Math.Min(extents.Max() / (longAxisSamples - 1), extents.Min() / (thinAxisSamples - 1));
        var cells = extents.Aggregate(1.0, (product, extent) =>
            product * (Math.Ceiling(extent / pitch) + 1));
        if (cells > maximumCellCount)
            pitch *= Math.Cbrt(cells / maximumCellCount) * 1.002;
        return pitch;
    }

    public void Reset()
    {
        _recentFiniteCylinderStamps.Clear();
        _recentFiniteCylinderStampOrder.Clear();
        _lastLayeredStampProfile = null;
        _xField = new DexelField(Bounds.Min.X, Bounds.Max.X, _yCoordinates, _zCoordinates, _initialXRays);
        _yField = new DexelField(Bounds.Min.Y, Bounds.Max.Y, _xCoordinates, _zCoordinates, _initialYRays);
        _zField = new DexelField(Bounds.Min.Z, Bounds.Max.Z, _xCoordinates, _yCoordinates, _initialZRays);
        MarkAllSurfaceChunksDirty();
    }

    public TripleDexelVolume Volume() => new(_xField.Volume(), _yField.Volume(), _zField.Volume());

    /// <summary>
    /// Tests a finite cylindrical tool region against the material that is
    /// still present in the authoritative interval stock.  This is a read-only
    /// narrow phase: it never consults the initial blank mesh and never builds
    /// a display mesh.  The cheapest of the three dexel bundles is selected
    /// from the cylinder envelope, so a separated holder normally exits at the
    /// bounds test and a nearby holder scans only the local stock rays.
    /// </summary>
    public bool IntersectsFiniteCylinder(
        Vector3 tip,
        Vector3 axis,
        double radius,
        double length)
    {
        if (!double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(length) || length <= 0)
            return false;
        if (axis.LengthSquared() <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(axis), "Silindir ekseni sıfır olamaz.");

        axis = Vector3.Normalize(axis);
        var back = tip + (axis * (float)length);
        var padding = new Vector3((float)radius);
        var minimum = Vector3.Min(tip, back) - padding;
        var maximum = Vector3.Max(tip, back) + padding;
        if (!Bounds.Intersects(new Bounds3(minimum, maximum))) return false;

        var (xU0, xU1) = CoordinateRange(_xField.U, minimum.Y, maximum.Y);
        var (xV0, xV1) = CoordinateRange(_xField.V, minimum.Z, maximum.Z);
        var (yU0, yU1) = CoordinateRange(_yField.U, minimum.X, maximum.X);
        var (yV0, yV1) = CoordinateRange(_yField.V, minimum.Z, maximum.Z);
        var (zU0, zU1) = CoordinateRange(_zField.U, minimum.X, maximum.X);
        var (zV0, zV1) = CoordinateRange(_zField.V, minimum.Y, maximum.Y);

        var xArea = RangeArea(xU0, xU1, xV0, xV1);
        var yArea = RangeArea(yU0, yU1, yV0, yV1);
        var zArea = RangeArea(zU0, zU1, zV0, zV1);
        if (xArea <= yArea && xArea <= zArea)
            return ScanX();
        if (yArea <= zArea)
            return ScanY();
        return ScanZ();

        bool ScanX()
        {
            for (var v = xV0; v <= xV1; v++)
            for (var u = xU0; u <= xU1; u++)
            {
                var origin = new Vector3(0, (float)_xField.U[u], (float)_xField.V[v]);
                if (CylinderLineInterval(origin, Vector3.UnitX, tip, axis, length, radius,
                        out var rayMinimum, out var rayMaximum) &&
                    RayOverlaps(_xField.Ray(u, v), rayMinimum, rayMaximum))
                    return true;
            }
            return false;
        }

        bool ScanY()
        {
            for (var v = yV0; v <= yV1; v++)
            for (var u = yU0; u <= yU1; u++)
            {
                var origin = new Vector3((float)_yField.U[u], 0, (float)_yField.V[v]);
                if (CylinderLineInterval(origin, Vector3.UnitY, tip, axis, length, radius,
                        out var rayMinimum, out var rayMaximum) &&
                    RayOverlaps(_yField.Ray(u, v), rayMinimum, rayMaximum))
                    return true;
            }
            return false;
        }

        bool ScanZ()
        {
            for (var v = zV0; v <= zV1; v++)
            for (var u = zU0; u <= zU1; u++)
            {
                var origin = new Vector3((float)_zField.U[u], (float)_zField.V[v], 0);
                if (CylinderLineInterval(origin, Vector3.UnitZ, tip, axis, length, radius,
                        out var rayMinimum, out var rayMaximum) &&
                    RayOverlaps(_zField.Ray(u, v), rayMinimum, rayMaximum))
                    return true;
            }
            return false;
        }

        static bool RayOverlaps(DexelRay ray, double minimum, double maximum)
        {
            const double epsilon = 1e-8;
            for (var index = 0; index < ray.Count; index++)
            {
                var span = ray[index];
                if (Math.Min(span.Max, maximum) >= Math.Max(span.Min, minimum) - epsilon)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Tests for material inside a cylinder inset by the requested amount.
    /// This separates a numerical/tangent boundary touch from measurable
    /// penetration without constructing a mesh or modifying the live stock.
    /// </summary>
    public bool PenetratesFiniteCylinder(
        Vector3 tip,
        Vector3 axis,
        double radius,
        double length,
        double minimumPenetrationMm)
    {
        if (!double.IsFinite(minimumPenetrationMm) || minimumPenetrationMm < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumPenetrationMm));
        if (minimumPenetrationMm <= 1e-9)
            return IntersectsFiniteCylinder(tip, axis, radius, length);
        if (axis.LengthSquared() <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(axis), "Silindir ekseni sıfır olamaz.");

        var innerRadius = radius - minimumPenetrationMm;
        var innerLength = length - (2 * minimumPenetrationMm);
        if (innerRadius <= 1e-9 || innerLength <= 1e-9) return false;

        axis = Vector3.Normalize(axis);
        var innerTip = tip + (axis * (float)minimumPenetrationMm);
        return IntersectsFiniteCylinder(innerTip, axis, innerRadius, innerLength);
    }

    public ZDexelTarget CreateZTarget(TriangleMeshData targetMesh) =>
        new(targetMesh, _zField.U, _zField.V);

    public ZDexelTargetComparison CompareWithTarget(ZDexelTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.X.Length != _zField.U.Length || target.Y.Length != _zField.V.Length)
            throw new ArgumentException("Hedef dexel ızgarası stok ızgarasıyla aynı değil.", nameof(target));

        var targetVolume = new StableSum();
        var missing = new StableSum();
        var excess = new StableSum();
        for (var y = 0; y < _zField.V.Length; y++)
        for (var x = 0; x < _zField.U.Length; x++)
        {
            var targetSpans = target.Ray(x, y);
            var stockRay = _zField.Ray(x, y);
            var stockSpans = stockRay.Spans;
            var targetLength = targetSpans.Length;
            var stockLength = stockRay.Length;
            var overlap = 0.0;
            foreach (var targetSpan in targetSpans)
            foreach (var stockSpan in stockSpans)
                overlap += Math.Max(0, Math.Min(targetSpan.Max, stockSpan.Max) - Math.Max(targetSpan.Min, stockSpan.Min));

            var area = target.XWeights[x] * target.YWeights[y];
            targetVolume.Add(targetLength * area);
            missing.Add(Math.Max(0, targetLength - overlap) * area);
            excess.Add(Math.Max(0, stockLength - overlap) * area);
        }
        return new ZDexelTargetComparison(targetVolume.Value, missing.Value, excess.Value);
    }

    public TripleDexelCutResult ApplyFlatEndMillMove(
        Vector3 startTip,
        Vector3 endTip,
        double radius,
        double fluteLength,
        bool isRapid,
        int cutTag = 0)
    {
        if (isRapid)
            return new TripleDexelCutResult(new TripleDexelVolume(), Volume(), true);
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!double.IsFinite(fluteLength) || fluteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(fluteLength));
        if (Math.Abs(startTip.Z - endTip.Z) > 1e-7)
            throw new NotSupportedException("Alpha4 Test2 yalnız sabit Z düz kesmeyi destekler.");

        var a = new Vector2(startTip.X, startTip.Y);
        var b = new Vector2(endTip.X, endTip.Y);
        var zMin = startTip.Z;
        var zMax = startTip.Z + fluteLength;
        var radiusSquared = radius * radius;
        var removedX = 0.0;
        var removedY = 0.0;
        var removedZ = 0.0;
        var changedMinimum = new Vector3(float.PositiveInfinity);
        var changedMaximum = new Vector3(float.NegativeInfinity);

        // Z rays: point-in-capsule in XY gives the complete cutter interval.
        for (var y = 0; y < _zField.V.Length; y++)
        for (var x = 0; x < _zField.U.Length; x++)
        {
            var point = new Vector2((float)_zField.U[x], (float)_zField.V[y]);
            if (DistanceSquaredToSegment(point, a, b) <= radiusSquared + 1e-9)
            {
                var delta = _zField.Subtract(x, y, zMin, zMax, cutTag, out var changedMin, out var changedMax);
                removedZ += delta;
                if (delta > 0)
                    IncludeChangedBounds(
                        ref changedMinimum,
                        ref changedMaximum,
                        new Vector3(point.X, point.Y, (float)changedMin),
                        new Vector3(point.X, point.Y, (float)changedMax));
            }
        }

        // X rays: fixed Y/Z, solve the convex capsule intersection on X.
        for (var z = 0; z < _xField.V.Length; z++)
        {
            var fixedZ = _xField.V[z];
            if (fixedZ < zMin - 1e-9 || fixedZ > zMax + 1e-9) continue;
            for (var y = 0; y < _xField.U.Length; y++)
            {
                var fixedY = _xField.U[y];
                if (!CapsuleLineInterval(a, b, radius, fixedY, alongX: true, out var min, out var max)) continue;
                var delta = _xField.Subtract(y, z, min, max, cutTag, out var changedMin, out var changedMax);
                removedX += delta;
                if (delta > 0)
                    IncludeChangedBounds(
                        ref changedMinimum,
                        ref changedMaximum,
                        new Vector3((float)changedMin, (float)fixedY, (float)fixedZ),
                        new Vector3((float)changedMax, (float)fixedY, (float)fixedZ));
            }
        }

        // Y rays: fixed X/Z, solve the convex capsule intersection on Y.
        for (var z = 0; z < _yField.V.Length; z++)
        {
            var fixedZ = _yField.V[z];
            if (fixedZ < zMin - 1e-9 || fixedZ > zMax + 1e-9) continue;
            for (var x = 0; x < _yField.U.Length; x++)
            {
                var fixedX = _yField.U[x];
                if (!CapsuleLineInterval(a, b, radius, fixedX, alongX: false, out var min, out var max)) continue;
                var delta = _yField.Subtract(x, z, min, max, cutTag, out var changedMin, out var changedMax);
                removedY += delta;
                if (delta > 0)
                    IncludeChangedBounds(
                        ref changedMinimum,
                        ref changedMaximum,
                        new Vector3((float)fixedX, (float)changedMin, (float)fixedZ),
                        new Vector3((float)fixedX, (float)changedMax, (float)fixedZ));
            }
        }

        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
            MarkSurfaceChunksDirty(new Bounds3(changedMinimum, changedMaximum));

        return new TripleDexelCutResult(
            new TripleDexelVolume(removedX, removedY, removedZ),
            Volume(),
            false);
    }

    /// <summary>
    /// Subtracts the swept cutting portion of a cylindrical tool.  The tool is
    /// represented by a flat-ended finite cylinder from tip toward holder.
    /// Tip and axis are sampled along the NC segment; every stationary cylinder
    /// is intersected analytically with all three dexel bundles.  This keeps the
    /// implementation deterministic while supporting indexed and simultaneous
    /// table motion without treating holder/shank geometry as a cutter.
    /// </summary>
    public TripleDexelCutResult ApplyCylindricalCutterMove(
        Vector3 startTip,
        Vector3 endTip,
        Vector3 startAxis,
        Vector3 endAxis,
        double radius,
        double fluteLength,
        bool isRapid,
        int cutTag = 0)
    {
        if (isRapid)
            return new TripleDexelCutResult(new TripleDexelVolume(), Volume(), true);
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!double.IsFinite(fluteLength) || fluteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(fluteLength));
        if (startAxis.LengthSquared() <= 1e-12f || endAxis.LengthSquared() <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(startAxis), "Kesici ekseni sıfır olamaz.");

        startAxis = Vector3.Normalize(startAxis);
        endAxis = Vector3.Normalize(endAxis);
        var dot = Math.Clamp(Vector3.Dot(startAxis, endAxis), -1f, 1f);
        if (dot >= 0.999999f && TryPrincipalAxis(startAxis, out var principalAxis))
        {
            var travel = endTip - startTip;
            var axialTravel = Vector3.Dot(travel, startAxis);
            var transverseTravel = travel - (startAxis * axialTravel);

            // A plunge/retract parallel to the cutter axis sweeps one longer
            // finite cylinder. This is exact and avoids repeatedly stamping
            // the same transverse ray footprint.
            if (transverseTravel.LengthSquared() <= 1e-10f)
            {
                var minimumOffset = Math.Min(0, axialTravel);
                var maximumOffset = Math.Max(fluteLength, axialTravel + fluteLength);
                var swept = SubtractFiniteCylinder(
                    startTip + (startAxis * (float)minimumOffset),
                    startAxis,
                    radius,
                    maximumOffset - minimumOffset,
                    cutTag);
                return new TripleDexelCutResult(swept, Volume(), false);
            }

            // For a lateral move perpendicular to a Cartesian cutter axis the
            // exact swept volume is a 2D capsule extruded through the flute
            // length. Intersect it analytically with all three dexel bundles.
            if (Math.Abs(axialTravel) <= 1e-6)
            {
                var swept = SubtractAxisAlignedCylinderSweep(
                    startTip, endTip, startAxis, principalAxis,
                    radius, fluteLength, cutTag);
                return new TripleDexelCutResult(swept, Volume(), false);
            }
        }

        var orientationTravel = fluteLength * Math.Acos(dot);
        var linearTravel = Vector3.Distance(startTip, endTip);
        var sampleStep = Math.Max(TargetPitch * 1.5, Math.Min(radius * 0.5, 5.0));
        var sampleCount = Math.Clamp(
            (int)Math.Ceiling(Math.Max(linearTravel, orientationTravel) / sampleStep),
            1,
            256);

        var removed = new TripleDexelVolume();
        for (var i = 0; i <= sampleCount; i++)
        {
            var t = sampleCount == 0 ? 1f : (float)i / sampleCount;
            var tip = Vector3.Lerp(startTip, endTip, t);
            var axis = Vector3.Lerp(startAxis, endAxis, t);
            axis = axis.LengthSquared() <= 1e-12f ? startAxis : Vector3.Normalize(axis);
            var stationary = SubtractFiniteCylinder(tip, axis, radius, fluteLength, cutTag);
            removed = new TripleDexelVolume(
                removed.X + stationary.X,
                removed.Y + stationary.Y,
                removed.Z + stationary.Z);
        }

        return new TripleDexelCutResult(removed, Volume(), false);
    }

    /// <summary>
    /// Subtracts a complete rotational cutting profile.  Ball, drill and
    /// corner-radius tools are represented by nested cylindrical layers.  A
    /// Cartesian, constant-orientation lateral move used to rescan the same
    /// local rays once per layer.  All generated layers share the same flute
    /// back plane, so their union can be evaluated exactly in one pass per
    /// dexel field without changing pitch or surface accuracy.  Indexed or
    /// simultaneous poses fall back to the proven per-layer path.
    /// </summary>
    public TripleDexelCutResult ApplyLayeredCutterMove(
        Vector3 startTip,
        Vector3 endTip,
        Vector3 startAxis,
        Vector3 endAxis,
        IReadOnlyList<CutterCylinderLayer> layers,
        bool isRapid,
        int cutTag = 0,
        bool calculateRemainingVolume = true,
        bool continuousTranslation = true)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var validLayers = layers.Where(layer => layer.IsValid)
            .ToArray();
        if (validLayers.Length == 0)
            return new TripleDexelCutResult(new TripleDexelVolume(), RemainingVolume(), isRapid);
        if (isRapid)
            return new TripleDexelCutResult(new TripleDexelVolume(), RemainingVolume(), true);
        if (startAxis.LengthSquared() <= 1e-12f || endAxis.LengthSquared() <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(startAxis), "Kesici ekseni sıfır olamaz.");

        startAxis = Vector3.Normalize(startAxis);
        endAxis = Vector3.Normalize(endAxis);
        var dot = Math.Clamp(Vector3.Dot(startAxis, endAxis), -1f, 1f);
        var travel = endTip - startTip;
        var axialTravel = Vector3.Dot(travel, startAxis);
        if (dot >= 0.999999f &&
            Math.Abs(axialTravel) <= 1e-6 &&
            TryPrincipalAxis(startAxis, out var principalAxis))
        {
            var baseAxial = AxisCoordinate(principalAxis, startTip);
            var direction = AxisCoordinate(principalAxis, startAxis);
            var spans = validLayers.Select(layer =>
            {
                var first = baseAxial + (direction * layer.AxialOffset);
                var second = baseAxial + (direction * (layer.AxialOffset + layer.Length));
                return new ProfileLayerSpan(
                    Math.Min(first, second),
                    Math.Max(first, second),
                    direction > 0 ? layer.Radius : layer.StartRadius ?? layer.Radius)
                { StartRadius = layer.StartRadius is null ? null : direction > 0 ? layer.StartRadius : layer.Radius };
            }).ToArray();

            // ToolCuttingProfileFactory produces nested layers ending at one
            // common flute-back plane.  This proves every per-ray interval is
            // continuous and can safely be subtracted as one union.
            var backs = validLayers.Select(layer =>
                    baseAxial + (direction * (layer.AxialOffset + layer.Length)))
                .ToArray();
            if (validLayers.All(layer => !layer.IsTapered) && backs.Max() - backs.Min() <= 1e-6 || IsContinuousIncreasingProfile(validLayers))
            {
                var swept = SubtractAxisAlignedLayeredCylinderSweep(
                    startTip, endTip, principalAxis, spans, cutTag);
                return new TripleDexelCutResult(swept, RemainingVolume(), false);
            }
        }

        if (continuousTranslation && validLayers.Any(layer => layer.IsTapered) &&
            Vector3.DistanceSquared(startAxis, endAxis) <= 1e-12f)
        {
            var swept = SubtractFiniteLayeredCylinders(startTip, startAxis, validLayers, cutTag, endTip);
            return new TripleDexelCutResult(swept, RemainingVolume(), false);
        }

        // Arbitrary indexed/simultaneous poses previously traversed every X/Y/Z
        // dexel bundle once per profile layer.  All layers belong to one cutter
        // pose, so intersect every ray with the complete profile in one pass.
        // Disjoint intervals are sorted and merged before subtraction; this is
        // geometrically identical to the former per-layer path and keeps the
        // authoritative pitch unchanged.
        var maximumReach = validLayers.Max(layer =>
            Math.Max(Math.Abs(layer.AxialOffset), Math.Abs(layer.AxialOffset + layer.Length)));
        var orientationTravel = maximumReach * Math.Acos(dot);
        var linearTravel = Vector3.Distance(startTip, endTip);
        var sampleStep = validLayers.Min(layer =>
            layer.MotionStep(TargetPitch));
        var sampleCount = Math.Clamp(
            (int)Math.Ceiling(Math.Max(linearTravel, orientationTravel) / sampleStep),
            1,
            256);

        var removed = new TripleDexelVolume();
        for (var i = 0; i <= sampleCount; i++)
        {
            var t = (float)i / sampleCount;
            var tip = Vector3.Lerp(startTip, endTip, t);
            var axis = Vector3.Lerp(startAxis, endAxis, t);
            axis = axis.LengthSquared() <= 1e-12f ? startAxis : Vector3.Normalize(axis);
            var result = SubtractFiniteLayeredCylinders(tip, axis, validLayers, cutTag);
            removed = new TripleDexelVolume(
                removed.X + result.X,
                removed.Y + result.Y,
                removed.Z + result.Z);
        }
        return new TripleDexelCutResult(removed, RemainingVolume(), false);

        TripleDexelVolume RemainingVolume() =>
            calculateRemainingVolume ? Volume() : new TripleDexelVolume();
    }

    /// <summary>
    /// Subtracts one contiguous sampled cutter path as a single geometric
    /// union. Constant-Z indexed work is evaluated by scanning each affected
    /// dexel ray once for the complete polyline instead of once per sample.
    /// Other orientations retain the proven segment kernel.
    /// </summary>
    public TripleDexelCutResult ApplyLayeredCutterPath(
        IReadOnlyList<LayeredCutterPose> poses,
        IReadOnlyList<CutterCylinderLayer> layers,
        bool isRapid,
        int cutTag = 0,
        bool calculateRemainingVolume = true)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(layers);
        if (poses.Count < 2)
            return new TripleDexelCutResult(
                new TripleDexelVolume(),
                calculateRemainingVolume ? Volume() : new TripleDexelVolume(),
                isRapid);
        if (isRapid)
            return new TripleDexelCutResult(
                new TripleDexelVolume(),
                calculateRemainingVolume ? Volume() : new TripleDexelVolume(),
                true);

        var validLayers = layers.Where(layer => layer.IsValid)
            .ToArray();
        if (validLayers.Length == 0)
            return new TripleDexelCutResult(
                new TripleDexelVolume(),
                calculateRemainingVolume ? Volume() : new TripleDexelVolume(),
                false);

        var normalized = new LayeredCutterPose[poses.Count];
        for (var index = 0; index < poses.Count; index++)
        {
            var axis = poses[index].Axis;
            if (axis.LengthSquared() <= 1e-12f)
                throw new ArgumentOutOfRangeException(nameof(poses), "Kesici ekseni sıfır olamaz.");
            normalized[index] = new LayeredCutterPose(poses[index].Tip, Vector3.Normalize(axis));
        }

        var first = normalized[0];
        var constantAxis = normalized.All(pose => Vector3.Dot(first.Axis, pose.Axis) >= 0.999999f);
        if (constantAxis &&
            TryPrincipalAxis(first.Axis, out var principalAxis) && principalAxis == Axis3.Z)
        {
            var baseAxial = first.Tip.Z;
            var constantAxial = normalized.All(pose => Math.Abs(pose.Tip.Z - baseAxial) <= 1e-6);
            var direction = first.Axis.Z;
            var backs = validLayers.Select(layer =>
                    baseAxial + (direction * (layer.AxialOffset + layer.Length)))
                .ToArray();
            if (constantAxial && (validLayers.All(layer => !layer.IsTapered) && backs.Max() - backs.Min() <= 1e-6 || IsContinuousIncreasingProfile(validLayers)))
            {
                var spans = validLayers.Select(layer =>
                {
                    var a = baseAxial + (direction * layer.AxialOffset);
                    var b = baseAxial + (direction * (layer.AxialOffset + layer.Length));
                    return new ProfileLayerSpan(Math.Min(a, b), Math.Max(a, b), direction > 0 ? layer.Radius : layer.StartRadius ?? layer.Radius)
                    { StartRadius = layer.StartRadius is null ? null : direction > 0 ? layer.StartRadius : layer.Radius };
                }).ToArray();
                var removed = SubtractAxisAlignedZLayeredCylinderPath(
                    normalized.Select(pose => pose.Tip).ToArray(), spans, cutTag);
                return new TripleDexelCutResult(
                    removed,
                    calculateRemainingVolume ? Volume() : new TripleDexelVolume(),
                    false);
            }
        }

        var total = new TripleDexelVolume();
        for (var index = 1; index < normalized.Length; index++)
        {
            var firstIndex = index - 1;
            // Translation of a straight, fixed-axis frustum is continuous.
            // Collapse only collinear input poses; every retained path point
            // is checked against the proposed segment (not just its ends).
            if (validLayers.Any(layer => layer.IsTapered))
                while (index + 1 < normalized.Length && StraightThrough(firstIndex, index + 1)) index++;
            var result = ApplyLayeredCutterMove(
                normalized[firstIndex].Tip,
                normalized[index].Tip,
                normalized[firstIndex].Axis,
                normalized[index].Axis,
                validLayers,
                false,
                cutTag,
                calculateRemainingVolume: false);
            total = new TripleDexelVolume(
                total.X + result.Removed.X,
                total.Y + result.Removed.Y,
                total.Z + result.Removed.Z);
        }
        return new TripleDexelCutResult(
            total,
            calculateRemainingVolume ? Volume() : new TripleDexelVolume(),
            false);

        bool StraightThrough(int start, int end)
        {
            var travel = normalized[end].Tip - normalized[start].Tip;
            double squared = travel.LengthSquared();
            if (squared < 1e-12) return false;
            for (int i = start + 1; i <= end; i++)
            {
                if (Vector3.DistanceSquared(normalized[i].Axis, normalized[start].Axis) > 1e-12f) return false;
                var delta = normalized[i].Tip - normalized[start].Tip;
                double t = Vector3.Dot(delta, travel) / squared;
                // Job-local poses use floats. 0.0001 mm is the existing path
                // continuity tolerance and avoids interpreting float roundoff
                // along one NC line as hundreds of geometric bends.
                if (t < -1e-7 || t > 1 + 1e-7 || Vector3.DistanceSquared(delta, travel * (float)t) > 1e-8f) return false;
            }
            return true;
        }
    }

    private TripleDexelVolume SubtractAxisAlignedZLayeredCylinderPath(
        IReadOnlyList<Vector3> tips,
        IReadOnlyList<ProfileLayerSpan> layers,
        int cutTag)
    {
        var path = tips.Select(tip => new Vector2(tip.X, tip.Y)).ToArray();
        var maximumRadius = layers.Max(layer => layer.MaximumRadius);
        var axialMinimum = layers.Min(layer => layer.Minimum);
        var axialMaximum = layers.Max(layer => layer.Maximum);
        var planeMinimum = new Vector2(
            path.Min(point => point.X),
            path.Min(point => point.Y)) - new Vector2((float)maximumRadius);
        var planeMaximum = new Vector2(
            path.Max(point => point.X),
            path.Max(point => point.Y)) + new Vector2((float)maximumRadius);
        var (x0, x1) = CoordinateRange(_zField.U, planeMinimum.X, planeMaximum.X);
        var (y0, y1) = CoordinateRange(_zField.V, planeMinimum.Y, planeMaximum.Y);
        var (fixedY0, fixedY1) = CoordinateRange(_xField.U, planeMinimum.Y, planeMaximum.Y);
        var (axialX0, axialX1) = CoordinateRange(_xField.V, axialMinimum, axialMaximum);
        var (fixedX0, fixedX1) = CoordinateRange(_yField.U, planeMinimum.X, planeMaximum.X);
        var (axialY0, axialY1) = CoordinateRange(_yField.V, axialMinimum, axialMaximum);
        var xVolumeBefore = _xField.Volume();
        var yVolumeBefore = _yField.Volume();
        var zVolumeBefore = _zField.Volume();
        var changedRows = new System.Collections.Concurrent.ConcurrentBag<Bounds3>();

        RunIndependentFieldPasses(
            RangeArea(fixedY0, fixedY1, axialX0, axialX1) +
            RangeArea(fixedX0, fixedX1, axialY0, axialY1) +
            RangeArea(x0, x1, y0, y1),
            () =>
            {
                ForIndependentRows(axialX0, axialX1,
                    RangeArea(fixedY0, fixedY1, axialX0, axialX1), z =>
                {
                    var radius = ProfileRadiusAt(_xField.V[z]);
                    if (radius <= 0) return;
                    var rowMinimum = new Vector3(float.PositiveInfinity);
                    var rowMaximum = new Vector3(float.NegativeInfinity);
                    var intervals = new CylinderRayInterval[Math.Max(1, path.Length - 1)];
                    for (var y = fixedY0; y <= fixedY1; y++)
                    {
                        var count = CollectPolylineIntervals(
                            path, radius, _xField.U[y], alongX: true, intervals);
                        for (var intervalIndex = 0; intervalIndex < count; intervalIndex++)
                            RemoveX(y, z, intervals[intervalIndex].Minimum, intervals[intervalIndex].Maximum,
                                ref rowMinimum, ref rowMaximum);
                    }
                    CaptureChangedRow(rowMinimum, rowMaximum);
                });
            },
            () =>
            {
                ForIndependentRows(axialY0, axialY1,
                    RangeArea(fixedX0, fixedX1, axialY0, axialY1), z =>
                {
                    var radius = ProfileRadiusAt(_yField.V[z]);
                    if (radius <= 0) return;
                    var rowMinimum = new Vector3(float.PositiveInfinity);
                    var rowMaximum = new Vector3(float.NegativeInfinity);
                    var intervals = new CylinderRayInterval[Math.Max(1, path.Length - 1)];
                    for (var x = fixedX0; x <= fixedX1; x++)
                    {
                        var count = CollectPolylineIntervals(
                            path, radius, _yField.U[x], alongX: false, intervals);
                        for (var intervalIndex = 0; intervalIndex < count; intervalIndex++)
                            RemoveY(x, z, intervals[intervalIndex].Minimum, intervals[intervalIndex].Maximum,
                                ref rowMinimum, ref rowMaximum);
                    }
                    CaptureChangedRow(rowMinimum, rowMaximum);
                });
            },
            () =>
            {
                ForIndependentRows(y0, y1, RangeArea(x0, x1, y0, y1), y =>
                {
                    var rowMinimum = new Vector3(float.PositiveInfinity);
                    var rowMaximum = new Vector3(float.NegativeInfinity);
                    for (var x = x0; x <= x1; x++)
                    {
                        var point = new Vector2((float)_zField.U[x], (float)_zField.V[y]);
                        if (TryProfileAxialInterval(point, out var minimum, out var maximum))
                            RemoveZ(x, y, minimum, maximum, ref rowMinimum, ref rowMaximum);
                    }
                    CaptureChangedRow(rowMinimum, rowMaximum);
                });
            });

        var removedX = Math.Max(0, xVolumeBefore - _xField.Volume());
        var removedY = Math.Max(0, yVolumeBefore - _yField.Volume());
        var removedZ = Math.Max(0, zVolumeBefore - _zField.Volume());
        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
        {
            // Each parallel row owns its changed bounds. After all field
            // passes finish, invalidate only rows that actually lost material.
            // The former full cutter envelope rebuilt untouched stock and air
            // on every tiny NC advance, including the whole flute depth.
            // Sorting preserves deterministic dirty-region priority.
            foreach (var bounds in changedRows.OrderBy(b => b.Min.Z).ThenBy(b => b.Min.Y)
                         .ThenBy(b => b.Min.X).ThenBy(b => b.Max.Z).ThenBy(b => b.Max.Y).ThenBy(b => b.Max.X))
                MarkSurfaceChunksDirty(bounds);
        }
        return new TripleDexelVolume(removedX, removedY, removedZ);

        double ProfileRadiusAt(double axialCoordinate)
        {
            var radius = 0.0;
            foreach (var layer in layers)
                if (axialCoordinate >= layer.Minimum - 1e-9 && axialCoordinate <= layer.Maximum + 1e-9)
                    radius = Math.Max(radius, layer.RadiusAt(axialCoordinate));
            return radius;
        }

        bool TryProfileAxialInterval(Vector2 point, out double minimum, out double maximum)
        {
            var distanceSquared = DistanceSquaredToPolyline(point, path);
            minimum = double.PositiveInfinity;
            maximum = double.NegativeInfinity;
            foreach (var layer in layers)
            {
                if (!layer.IntervalAt(distanceSquared, out var lo, out var hi)) continue;
                minimum = Math.Min(minimum, lo);
                maximum = Math.Max(maximum, hi);
            }
            return maximum > minimum;
        }

        void CaptureChangedRow(Vector3 minimum, Vector3 maximum)
        {
            if (_surfaceCacheEnabled && float.IsFinite(minimum.X))
                changedRows.Add(new Bounds3(minimum, maximum));
        }

        void RemoveX(int u, int v, double minimum, double maximum, ref Vector3 rowMin, ref Vector3 rowMax)
        {
            if (_xField.SubtractThreadSafe(u, v, minimum, maximum, cutTag, out var lo, out var hi) > 0)
                IncludeChangedBounds(ref rowMin, ref rowMax,
                    new Vector3((float)lo, (float)_xField.U[u], (float)_xField.V[v]),
                    new Vector3((float)hi, (float)_xField.U[u], (float)_xField.V[v]));
        }

        void RemoveY(int u, int v, double minimum, double maximum, ref Vector3 rowMin, ref Vector3 rowMax)
        {
            if (_yField.SubtractThreadSafe(u, v, minimum, maximum, cutTag, out var lo, out var hi) > 0)
                IncludeChangedBounds(ref rowMin, ref rowMax,
                    new Vector3((float)_yField.U[u], (float)lo, (float)_yField.V[v]),
                    new Vector3((float)_yField.U[u], (float)hi, (float)_yField.V[v]));
        }

        void RemoveZ(int u, int v, double minimum, double maximum, ref Vector3 rowMin, ref Vector3 rowMax)
        {
            if (_zField.SubtractThreadSafe(u, v, minimum, maximum, cutTag, out var lo, out var hi) > 0)
                IncludeChangedBounds(ref rowMin, ref rowMax,
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)lo),
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)hi));
        }
    }

    private TripleDexelVolume SubtractFiniteLayeredCylinders(
        Vector3 baseTip,
        Vector3 axis,
        IReadOnlyList<CutterCylinderLayer> layers,
        int cutTag,
        Vector3? sweepEnd = null)
    {
        var swept = sweepEnd is Vector3 end && Vector3.DistanceSquared(baseTip, end) > 1e-16f;
        var travel = swept ? sweepEnd!.Value - baseTip : Vector3.Zero;
        if (!swept && _lastLayeredStampProfile is not null &&
            _lastLayeredStampTip == baseTip &&
            _lastLayeredStampAxis == axis &&
            SameLayerProfile(_lastLayeredStampProfile, layers))
            return new TripleDexelVolume();

        _lastLayeredStampTip = baseTip;
        _lastLayeredStampAxis = axis;
        _lastLayeredStampProfile = swept ? null : layers.ToArray();

        var cylinders = layers.Select(layer => (
            Tip: baseTip + (axis * (float)layer.AxialOffset),
            layer.Length,
            layer.Radius,
            layer.StartRadius,
            layer.MaximumRadius)).ToArray();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var cylinder in cylinders)
        {
            var back = cylinder.Tip + (axis * (float)cylinder.Length);
            // A disk extends only perpendicular to its axis. Padding every
            // coordinate by the full diameter scanned large solid areas below
            // a pointed nose that cannot possibly intersect the cutter.
            double normSquared = (double)axis.X*axis.X + (double)axis.Y*axis.Y + (double)axis.Z*axis.Z;
            var disk = new Vector3((float)Math.Sqrt(Math.Max(0, 1-axis.X*(double)axis.X/normSquared)),
                (float)Math.Sqrt(Math.Max(0, 1-axis.Y*(double)axis.Y/normSquared)),
                (float)Math.Sqrt(Math.Max(0, 1-axis.Z*(double)axis.Z/normSquared)));
            var frontExtent = disk * (float)(cylinder.StartRadius ?? cylinder.Radius) + new Vector3(.0001f);
            var backExtent = disk * (float)cylinder.Radius + new Vector3(.0001f);
            minimum = Vector3.Min(minimum, Vector3.Min(cylinder.Tip-frontExtent, back-backExtent));
            maximum = Vector3.Max(maximum, Vector3.Max(cylinder.Tip+frontExtent, back+backExtent));
        }
        if (swept)
        {
            minimum = Vector3.Min(minimum, minimum + travel);
            maximum = Vector3.Max(maximum, maximum + travel);
        }
        minimum = new(float.BitDecrement(minimum.X),float.BitDecrement(minimum.Y),float.BitDecrement(minimum.Z));
        maximum = new(float.BitIncrement(maximum.X),float.BitIncrement(maximum.Y),float.BitIncrement(maximum.Z));

        var xKernels = cylinders.Select(cylinder => PrepareCylinderRay(
            cylinder.Tip, axis, cylinder.Length, cylinder.Radius, Vector3.UnitX, cylinder.StartRadius)).ToArray();
        var yKernels = cylinders.Select(cylinder => PrepareCylinderRay(
            cylinder.Tip, axis, cylinder.Length, cylinder.Radius, Vector3.UnitY, cylinder.StartRadius)).ToArray();
        var zKernels = cylinders.Select(cylinder => PrepareCylinderRay(
            cylinder.Tip, axis, cylinder.Length, cylinder.Radius, Vector3.UnitZ, cylinder.StartRadius)).ToArray();

        if (layers.Any(layer => layer.IsTapered))
            return SubtractProfileRows(minimum, maximum, travel, swept, xKernels, yKernels, zKernels, cutTag);

        var xSweeps=swept ? xKernels.Select(kernel=>PrepareFrustumSweep(kernel,travel)).ToArray() : [];
        var ySweeps=swept ? yKernels.Select(kernel=>PrepareFrustumSweep(kernel,travel)).ToArray() : [];
        var zSweeps=swept ? zKernels.Select(kernel=>PrepareFrustumSweep(kernel,travel)).ToArray() : [];

        var removedX = 0.0;
        var removedY = 0.0;
        var removedZ = 0.0;
        var changedXMinimum = new Vector3(float.PositiveInfinity);
        var changedXMaximum = new Vector3(float.NegativeInfinity);
        var changedYMinimum = new Vector3(float.PositiveInfinity);
        var changedYMaximum = new Vector3(float.NegativeInfinity);
        var changedZMinimum = new Vector3(float.PositiveInfinity);
        var changedZMaximum = new Vector3(float.NegativeInfinity);
        var (xYu0, xYu1) = CoordinateRange(_xField.U, minimum.Y, maximum.Y);
        var (xZv0, xZv1) = CoordinateRange(_xField.V, minimum.Z, maximum.Z);
        var (yXu0, yXu1) = CoordinateRange(_yField.U, minimum.X, maximum.X);
        var (yZv0, yZv1) = CoordinateRange(_yField.V, minimum.Z, maximum.Z);
        var (zXu0, zXu1) = CoordinateRange(_zField.U, minimum.X, maximum.X);
        var (zYv0, zYv1) = CoordinateRange(_zField.V, minimum.Y, maximum.Y);

        RunIndependentFieldPasses(
            RangeArea(xYu0, xYu1, xZv0, xZv1) +
            RangeArea(yXu0, yXu1, yZv0, yZv1) +
            RangeArea(zXu0, zXu1, zYv0, zYv1),
            () =>
            {
                var intervals = new CylinderRayInterval[xKernels.Length];
                for (var z = xZv0; z <= xZv1; z++)
                for (var y = xYu0; y <= xYu1; y++)
                {
                    if (!_xField.Ray(y, z).MayOverlap(minimum.X, maximum.X)) continue;
                    var origin = new Vector3(0, (float)_xField.U[y], (float)_xField.V[z]);
                    var count = swept ? CollectPreparedSweptIntervals(origin, xSweeps, intervals) : CollectCylinderIntervals(origin, xKernels, intervals);
                    for (var index = 0; index < count; index++)
                    {
                        var interval = intervals[index];
                        var delta = _xField.Subtract(y, z, interval.Minimum, interval.Maximum, cutTag, out var changedMin, out var changedMax);
                        removedX += delta;
                        if (delta > 0)
                            IncludeChangedBounds(ref changedXMinimum, ref changedXMaximum,
                                new Vector3((float)changedMin, origin.Y, origin.Z),
                                new Vector3((float)changedMax, origin.Y, origin.Z));
                    }
                }
            },
            () =>
            {
                var intervals = new CylinderRayInterval[yKernels.Length];
                for (var z = yZv0; z <= yZv1; z++)
                for (var x = yXu0; x <= yXu1; x++)
                {
                    if (!_yField.Ray(x, z).MayOverlap(minimum.Y, maximum.Y)) continue;
                    var origin = new Vector3((float)_yField.U[x], 0, (float)_yField.V[z]);
                    var count = swept ? CollectPreparedSweptIntervals(origin, ySweeps, intervals) : CollectCylinderIntervals(origin, yKernels, intervals);
                    for (var index = 0; index < count; index++)
                    {
                        var interval = intervals[index];
                        var delta = _yField.Subtract(x, z, interval.Minimum, interval.Maximum, cutTag, out var changedMin, out var changedMax);
                        removedY += delta;
                        if (delta > 0)
                            IncludeChangedBounds(ref changedYMinimum, ref changedYMaximum,
                                new Vector3(origin.X, (float)changedMin, origin.Z),
                                new Vector3(origin.X, (float)changedMax, origin.Z));
                    }
                }
            },
            () =>
            {
                var intervals = new CylinderRayInterval[zKernels.Length];
                for (var y = zYv0; y <= zYv1; y++)
                for (var x = zXu0; x <= zXu1; x++)
                {
                    if (!_zField.Ray(x, y).MayOverlap(minimum.Z, maximum.Z)) continue;
                    var origin = new Vector3((float)_zField.U[x], (float)_zField.V[y], 0);
                    var count = swept ? CollectPreparedSweptIntervals(origin, zSweeps, intervals) : CollectCylinderIntervals(origin, zKernels, intervals);
                    for (var index = 0; index < count; index++)
                    {
                        var interval = intervals[index];
                        var delta = _zField.Subtract(x, y, interval.Minimum, interval.Maximum, cutTag, out var changedMin, out var changedMax);
                        removedZ += delta;
                        if (delta > 0)
                            IncludeChangedBounds(ref changedZMinimum, ref changedZMaximum,
                                new Vector3(origin.X, origin.Y, (float)changedMin),
                                new Vector3(origin.X, origin.Y, (float)changedMax));
                    }
                }
            });

        var changedMinimum = Vector3.Min(changedXMinimum, Vector3.Min(changedYMinimum, changedZMinimum));
        var changedMaximum = Vector3.Max(changedXMaximum, Vector3.Max(changedYMaximum, changedZMaximum));
        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
            MarkSurfaceChunksDirty(new Bounds3(changedMinimum, changedMaximum));
        return new TripleDexelVolume(removedX, removedY, removedZ);
    }

    private static bool SameLayerProfile(
        IReadOnlyList<CutterCylinderLayer> left,
        IReadOnlyList<CutterCylinderLayer> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (left[index] != right[index]) return false;
        return true;
    }

    private TripleDexelVolume SubtractAxisAlignedLayeredCylinderSweep(
        Vector3 startTip,
        Vector3 endTip,
        Axis3 principalAxis,
        IReadOnlyList<ProfileLayerSpan> layers,
        int cutTag)
    {
        var a = ProjectTransverse(startTip, principalAxis);
        var b = ProjectTransverse(endTip, principalAxis);
        var maximumRadius = layers.Max(layer => layer.MaximumRadius);
        var axialMinimum = layers.Min(layer => layer.Minimum);
        var axialMaximum = layers.Max(layer => layer.Maximum);
        var planeMinimum = Vector2.Min(a, b) - new Vector2((float)maximumRadius);
        var planeMaximum = Vector2.Max(a, b) + new Vector2((float)maximumRadius);
        var removedX = 0.0;
        var removedY = 0.0;
        var removedZ = 0.0;
        var changedXMinimum = new Vector3(float.PositiveInfinity);
        var changedXMaximum = new Vector3(float.NegativeInfinity);
        var changedYMinimum = new Vector3(float.PositiveInfinity);
        var changedYMaximum = new Vector3(float.NegativeInfinity);
        var changedZMinimum = new Vector3(float.PositiveInfinity);
        var changedZMaximum = new Vector3(float.NegativeInfinity);

        switch (principalAxis)
        {
            case Axis3.X:
            {
                var (y0, y1) = CoordinateRange(_xField.U, planeMinimum.X, planeMaximum.X);
                var (z0, z1) = CoordinateRange(_xField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialY0, axialY1) = CoordinateRange(_yField.U, axialMinimum, axialMaximum);
                var (fixedZ0, fixedZ1) = CoordinateRange(_yField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialZ0, axialZ1) = CoordinateRange(_zField.U, axialMinimum, axialMaximum);
                var (fixedY0, fixedY1) = CoordinateRange(_zField.V, planeMinimum.X, planeMaximum.X);
                RunIndependentFieldPasses(
                    RangeArea(y0, y1, z0, z1) +
                    RangeArea(axialY0, axialY1, fixedZ0, fixedZ1) +
                    RangeArea(axialZ0, axialZ1, fixedY0, fixedY1),
                    () =>
                    {
                        for (var z = z0; z <= z1; z++)
                        for (var y = y0; y <= y1; y++)
                        {
                            var point = new Vector2((float)_xField.U[y], (float)_xField.V[z]);
                            if (TryProfileAxialInterval(point, out var min, out var max))
                                RemoveX(y, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var x = axialY0; x <= axialY1; x++)
                        {
                            var radius = ProfileRadiusAt(_yField.U[x]);
                            if (radius <= 0) continue;
                            for (var z = fixedZ0; z <= fixedZ1; z++)
                                if (CapsuleLineInterval(a, b, radius, _yField.V[z], alongX: true, out var min, out var max))
                                    RemoveY(x, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var x = axialZ0; x <= axialZ1; x++)
                        {
                            var radius = ProfileRadiusAt(_zField.U[x]);
                            if (radius <= 0) continue;
                            for (var y = fixedY0; y <= fixedY1; y++)
                                if (CapsuleLineInterval(a, b, radius, _zField.V[y], alongX: false, out var min, out var max))
                                    RemoveZ(x, y, min, max);
                        }
                    });
                break;
            }
            case Axis3.Y:
            {
                var (x0, x1) = CoordinateRange(_yField.U, planeMinimum.X, planeMaximum.X);
                var (z0, z1) = CoordinateRange(_yField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialX0, axialX1) = CoordinateRange(_xField.U, axialMinimum, axialMaximum);
                var (fixedZ0, fixedZ1) = CoordinateRange(_xField.V, planeMinimum.Y, planeMaximum.Y);
                var (fixedX0, fixedX1) = CoordinateRange(_zField.U, planeMinimum.X, planeMaximum.X);
                var (axialZ0, axialZ1) = CoordinateRange(_zField.V, axialMinimum, axialMaximum);
                RunIndependentFieldPasses(
                    RangeArea(axialX0, axialX1, fixedZ0, fixedZ1) +
                    RangeArea(x0, x1, z0, z1) +
                    RangeArea(fixedX0, fixedX1, axialZ0, axialZ1),
                    () =>
                    {
                        for (var y = axialX0; y <= axialX1; y++)
                        {
                            var radius = ProfileRadiusAt(_xField.U[y]);
                            if (radius <= 0) continue;
                            for (var z = fixedZ0; z <= fixedZ1; z++)
                                if (CapsuleLineInterval(a, b, radius, _xField.V[z], alongX: true, out var min, out var max))
                                    RemoveX(y, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var z = z0; z <= z1; z++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var point = new Vector2((float)_yField.U[x], (float)_yField.V[z]);
                            if (TryProfileAxialInterval(point, out var min, out var max))
                                RemoveY(x, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var y = axialZ0; y <= axialZ1; y++)
                        {
                            var radius = ProfileRadiusAt(_zField.V[y]);
                            if (radius <= 0) continue;
                            for (var x = fixedX0; x <= fixedX1; x++)
                                if (CapsuleLineInterval(a, b, radius, _zField.U[x], alongX: false, out var min, out var max))
                                    RemoveZ(x, y, min, max);
                        }
                    });
                break;
            }
            default:
            {
                var (x0, x1) = CoordinateRange(_zField.U, planeMinimum.X, planeMaximum.X);
                var (y0, y1) = CoordinateRange(_zField.V, planeMinimum.Y, planeMaximum.Y);
                var (fixedY0, fixedY1) = CoordinateRange(_xField.U, planeMinimum.Y, planeMaximum.Y);
                var (axialX0, axialX1) = CoordinateRange(_xField.V, axialMinimum, axialMaximum);
                var (fixedX0, fixedX1) = CoordinateRange(_yField.U, planeMinimum.X, planeMaximum.X);
                var (axialY0, axialY1) = CoordinateRange(_yField.V, axialMinimum, axialMaximum);
                RunIndependentFieldPasses(
                    RangeArea(fixedY0, fixedY1, axialX0, axialX1) +
                    RangeArea(fixedX0, fixedX1, axialY0, axialY1) +
                    RangeArea(x0, x1, y0, y1),
                    () =>
                    {
                        for (var z = axialX0; z <= axialX1; z++)
                        {
                            var radius = ProfileRadiusAt(_xField.V[z]);
                            if (radius <= 0) continue;
                            for (var y = fixedY0; y <= fixedY1; y++)
                                if (CapsuleLineInterval(a, b, radius, _xField.U[y], alongX: true, out var min, out var max))
                                    RemoveX(y, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var z = axialY0; z <= axialY1; z++)
                        {
                            var radius = ProfileRadiusAt(_yField.V[z]);
                            if (radius <= 0) continue;
                            for (var x = fixedX0; x <= fixedX1; x++)
                                if (CapsuleLineInterval(a, b, radius, _yField.U[x], alongX: false, out var min, out var max))
                                    RemoveY(x, z, min, max);
                        }
                    },
                    () =>
                    {
                        for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var point = new Vector2((float)_zField.U[x], (float)_zField.V[y]);
                            if (TryProfileAxialInterval(point, out var min, out var max))
                                RemoveZ(x, y, min, max);
                        }
                    });
                break;
            }
        }

        var changedMinimum = Vector3.Min(changedXMinimum, Vector3.Min(changedYMinimum, changedZMinimum));
        var changedMaximum = Vector3.Max(changedXMaximum, Vector3.Max(changedYMaximum, changedZMaximum));
        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
            MarkSurfaceChunksDirty(new Bounds3(changedMinimum, changedMaximum));
        return new TripleDexelVolume(removedX, removedY, removedZ);

        double ProfileRadiusAt(double axialCoordinate)
        {
            var radius = 0.0;
            foreach (var layer in layers)
                if (axialCoordinate >= layer.Minimum - 1e-9 && axialCoordinate <= layer.Maximum + 1e-9)
                    radius = Math.Max(radius, layer.RadiusAt(axialCoordinate));
            return radius;
        }

        bool TryProfileAxialInterval(Vector2 point, out double minimum, out double maximum)
        {
            var distanceSquared = DistanceSquaredToSegment(point, a, b);
            minimum = double.PositiveInfinity;
            maximum = double.NegativeInfinity;
            foreach (var layer in layers)
            {
                if (!layer.IntervalAt(distanceSquared, out var lo, out var hi)) continue;
                minimum = Math.Min(minimum, lo);
                maximum = Math.Max(maximum, hi);
            }
            return maximum > minimum;
        }

        void RemoveX(int u, int v, double min, double max)
        {
            var delta = _xField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedX += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedXMinimum, ref changedXMaximum,
                    new Vector3((float)changedMin, (float)_xField.U[u], (float)_xField.V[v]),
                    new Vector3((float)changedMax, (float)_xField.U[u], (float)_xField.V[v]));
        }

        void RemoveY(int u, int v, double min, double max)
        {
            var delta = _yField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedY += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedYMinimum, ref changedYMaximum,
                    new Vector3((float)_yField.U[u], (float)changedMin, (float)_yField.V[v]),
                    new Vector3((float)_yField.U[u], (float)changedMax, (float)_yField.V[v]));
        }

        void RemoveZ(int u, int v, double min, double max)
        {
            var delta = _zField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedZ += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedZMinimum, ref changedZMaximum,
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)changedMin),
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)changedMax));
        }
    }

    private TripleDexelVolume SubtractAxisAlignedCylinderSweep(
        Vector3 startTip,
        Vector3 endTip,
        Vector3 cutterAxis,
        Axis3 principalAxis,
        double radius,
        double length,
        int cutTag)
    {
        var startBack = startTip + (cutterAxis * (float)length);
        var endBack = endTip + (cutterAxis * (float)length);
        var axialMinimum = Math.Min(
            AxisCoordinate(principalAxis, startTip),
            Math.Min(AxisCoordinate(principalAxis, endTip),
                Math.Min(AxisCoordinate(principalAxis, startBack), AxisCoordinate(principalAxis, endBack))));
        var axialMaximum = Math.Max(
            AxisCoordinate(principalAxis, startTip),
            Math.Max(AxisCoordinate(principalAxis, endTip),
                Math.Max(AxisCoordinate(principalAxis, startBack), AxisCoordinate(principalAxis, endBack))));
        var a = ProjectTransverse(startTip, principalAxis);
        var b = ProjectTransverse(endTip, principalAxis);
        var planeMinimum = Vector2.Min(a, b) - new Vector2((float)radius);
        var planeMaximum = Vector2.Max(a, b) + new Vector2((float)radius);
        var radiusSquared = radius * radius;
        var removedX = 0.0;
        var removedY = 0.0;
        var removedZ = 0.0;
        var changedXMinimum = new Vector3(float.PositiveInfinity);
        var changedXMaximum = new Vector3(float.NegativeInfinity);
        var changedYMinimum = new Vector3(float.PositiveInfinity);
        var changedYMaximum = new Vector3(float.NegativeInfinity);
        var changedZMinimum = new Vector3(float.PositiveInfinity);
        var changedZMaximum = new Vector3(float.NegativeInfinity);

        switch (principalAxis)
        {
            case Axis3.X:
            {
                var (y0, y1) = CoordinateRange(_xField.U, planeMinimum.X, planeMaximum.X);
                var (z0, z1) = CoordinateRange(_xField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialY0, axialY1) = CoordinateRange(_yField.U, axialMinimum, axialMaximum);
                var (fixedZ0, fixedZ1) = CoordinateRange(_yField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialZ0, axialZ1) = CoordinateRange(_zField.U, axialMinimum, axialMaximum);
                var (fixedY0, fixedY1) = CoordinateRange(_zField.V, planeMinimum.X, planeMaximum.X);
                RunIndependentFieldPasses(
                    RangeArea(y0, y1, z0, z1) +
                    RangeArea(axialY0, axialY1, fixedZ0, fixedZ1) +
                    RangeArea(axialZ0, axialZ1, fixedY0, fixedY1),
                    () =>
                    {
                        for (var z = z0; z <= z1; z++)
                        for (var y = y0; y <= y1; y++)
                        {
                            var point = new Vector2((float)_xField.U[y], (float)_xField.V[z]);
                            if (DistanceSquaredToSegment(point, a, b) <= radiusSquared + 1e-9)
                                RemoveX(y, z, axialMinimum, axialMaximum);
                        }
                    },
                    () =>
                    {
                        for (var z = fixedZ0; z <= fixedZ1; z++)
                        for (var x = axialY0; x <= axialY1; x++)
                            if (CapsuleLineInterval(a, b, radius, _yField.V[z], alongX: true, out var min, out var max))
                                RemoveY(x, z, min, max);
                    },
                    () =>
                    {
                        for (var y = fixedY0; y <= fixedY1; y++)
                        for (var x = axialZ0; x <= axialZ1; x++)
                            if (CapsuleLineInterval(a, b, radius, _zField.V[y], alongX: false, out var min, out var max))
                                RemoveZ(x, y, min, max);
                    });
                break;
            }
            case Axis3.Y:
            {
                var (x0, x1) = CoordinateRange(_yField.U, planeMinimum.X, planeMaximum.X);
                var (z0, z1) = CoordinateRange(_yField.V, planeMinimum.Y, planeMaximum.Y);
                var (axialX0, axialX1) = CoordinateRange(_xField.U, axialMinimum, axialMaximum);
                var (fixedZ0, fixedZ1) = CoordinateRange(_xField.V, planeMinimum.Y, planeMaximum.Y);
                var (fixedX0, fixedX1) = CoordinateRange(_zField.U, planeMinimum.X, planeMaximum.X);
                var (axialZ0, axialZ1) = CoordinateRange(_zField.V, axialMinimum, axialMaximum);
                RunIndependentFieldPasses(
                    RangeArea(axialX0, axialX1, fixedZ0, fixedZ1) +
                    RangeArea(x0, x1, z0, z1) +
                    RangeArea(fixedX0, fixedX1, axialZ0, axialZ1),
                    () =>
                    {
                        for (var z = fixedZ0; z <= fixedZ1; z++)
                        for (var y = axialX0; y <= axialX1; y++)
                            if (CapsuleLineInterval(a, b, radius, _xField.V[z], alongX: true, out var min, out var max))
                                RemoveX(y, z, min, max);
                    },
                    () =>
                    {
                        for (var z = z0; z <= z1; z++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var point = new Vector2((float)_yField.U[x], (float)_yField.V[z]);
                            if (DistanceSquaredToSegment(point, a, b) <= radiusSquared + 1e-9)
                                RemoveY(x, z, axialMinimum, axialMaximum);
                        }
                    },
                    () =>
                    {
                        for (var y = axialZ0; y <= axialZ1; y++)
                        for (var x = fixedX0; x <= fixedX1; x++)
                            if (CapsuleLineInterval(a, b, radius, _zField.U[x], alongX: false, out var min, out var max))
                                RemoveZ(x, y, min, max);
                    });
                break;
            }
            default:
            {
                var (x0, x1) = CoordinateRange(_zField.U, planeMinimum.X, planeMaximum.X);
                var (y0, y1) = CoordinateRange(_zField.V, planeMinimum.Y, planeMaximum.Y);
                var (fixedY0, fixedY1) = CoordinateRange(_xField.U, planeMinimum.Y, planeMaximum.Y);
                var (axialX0, axialX1) = CoordinateRange(_xField.V, axialMinimum, axialMaximum);
                var (fixedX0, fixedX1) = CoordinateRange(_yField.U, planeMinimum.X, planeMaximum.X);
                var (axialY0, axialY1) = CoordinateRange(_yField.V, axialMinimum, axialMaximum);
                RunIndependentFieldPasses(
                    RangeArea(fixedY0, fixedY1, axialX0, axialX1) +
                    RangeArea(fixedX0, fixedX1, axialY0, axialY1) +
                    RangeArea(x0, x1, y0, y1),
                    () =>
                    {
                        for (var z = axialX0; z <= axialX1; z++)
                        for (var y = fixedY0; y <= fixedY1; y++)
                            if (CapsuleLineInterval(a, b, radius, _xField.U[y], alongX: true, out var min, out var max))
                                RemoveX(y, z, min, max);
                    },
                    () =>
                    {
                        for (var z = axialY0; z <= axialY1; z++)
                        for (var x = fixedX0; x <= fixedX1; x++)
                            if (CapsuleLineInterval(a, b, radius, _yField.U[x], alongX: false, out var min, out var max))
                                RemoveY(x, z, min, max);
                    },
                    () =>
                    {
                        for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var point = new Vector2((float)_zField.U[x], (float)_zField.V[y]);
                            if (DistanceSquaredToSegment(point, a, b) <= radiusSquared + 1e-9)
                                RemoveZ(x, y, axialMinimum, axialMaximum);
                        }
                    });
                break;
            }
        }

        var changedMinimum = Vector3.Min(changedXMinimum, Vector3.Min(changedYMinimum, changedZMinimum));
        var changedMaximum = Vector3.Max(changedXMaximum, Vector3.Max(changedYMaximum, changedZMaximum));
        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
            MarkSurfaceChunksDirty(new Bounds3(changedMinimum, changedMaximum));
        return new TripleDexelVolume(removedX, removedY, removedZ);

        void RemoveX(int u, int v, double min, double max)
        {
            var delta = _xField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedX += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedXMinimum, ref changedXMaximum,
                    new Vector3((float)changedMin, (float)_xField.U[u], (float)_xField.V[v]),
                    new Vector3((float)changedMax, (float)_xField.U[u], (float)_xField.V[v]));
        }

        void RemoveY(int u, int v, double min, double max)
        {
            var delta = _yField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedY += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedYMinimum, ref changedYMaximum,
                    new Vector3((float)_yField.U[u], (float)changedMin, (float)_yField.V[v]),
                    new Vector3((float)_yField.U[u], (float)changedMax, (float)_yField.V[v]));
        }

        void RemoveZ(int u, int v, double min, double max)
        {
            var delta = _zField.Subtract(u, v, min, max, cutTag, out var changedMin, out var changedMax);
            removedZ += delta;
            if (delta > 0)
                IncludeChangedBounds(
                    ref changedZMinimum, ref changedZMaximum,
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)changedMin),
                    new Vector3((float)_zField.U[u], (float)_zField.V[v], (float)changedMax));
        }
    }

    private static long RangeArea(int firstU, int lastU, int firstV, int lastV)
    {
        var uCount = Math.Max(0, lastU - firstU + 1L);
        var vCount = Math.Max(0, lastV - firstV + 1L);
        return uCount * vCount;
    }

    private static void ForIndependentRows(int first, int last, long estimatedRayTests, Action<int> row)
    {
        if (first > last) return;
        if (estimatedRayTests >= 8_000 && Environment.ProcessorCount > 1)
            Parallel.For(first, last + 1, row);
        else
            for (var index = first; index <= last; index++)
                row(index);
    }

    private static void RunIndependentFieldPasses(
        long estimatedRayTests,
        Action xPass,
        Action yPass,
        Action zPass)
    {
        // X/Y/Z dexel fields have disjoint ray and volume storage. Large
        // passes can therefore run concurrently without changing the order of
        // mutations inside any one field. Small passes remain sequential so
        // thread-pool scheduling never dominates a short cutter move.
        if (estimatedRayTests >= 20_000 && Environment.ProcessorCount > 1)
            Parallel.Invoke(xPass, yPass, zPass);
        else
        {
            xPass();
            yPass();
            zPass();
        }
    }

    private static bool TryPrincipalAxis(Vector3 axis, out Axis3 principalAxis)
    {
        var absolute = Vector3.Abs(axis);
        if (absolute.X >= 0.999999f)
        {
            principalAxis = Axis3.X;
            return true;
        }
        if (absolute.Y >= 0.999999f)
        {
            principalAxis = Axis3.Y;
            return true;
        }
        if (absolute.Z >= 0.999999f)
        {
            principalAxis = Axis3.Z;
            return true;
        }
        principalAxis = default;
        return false;
    }

    private static double AxisCoordinate(Axis3 axis, Vector3 point) => axis switch
    {
        Axis3.X => point.X,
        Axis3.Y => point.Y,
        _ => point.Z
    };

    private static Vector2 ProjectTransverse(Vector3 point, Axis3 axis) => axis switch
    {
        Axis3.X => new Vector2(point.Y, point.Z),
        Axis3.Y => new Vector2(point.X, point.Z),
        _ => new Vector2(point.X, point.Y)
    };

    private TripleDexelVolume SubtractFiniteCylinder(
        Vector3 tip,
        Vector3 axis,
        double radius,
        double length,
        int cutTag)
    {
        // Stock is monotonic: material is removed but never added between
        // Reset calls. Reapplying an identical finite cutter stamp can
        // therefore never change geometry. Adjacent NC/path segments share
        // endpoints, so a bounded recent set avoids scanning the same dexel
        // rays twice without making memory depend on program length.
        var stampKey = new FiniteCylinderStampKey(
            tip,
            axis,
            BitConverter.DoubleToInt64Bits(radius),
            BitConverter.DoubleToInt64Bits(length));
        if (!_recentFiniteCylinderStamps.Add(stampKey))
            return new TripleDexelVolume();
        _recentFiniteCylinderStampOrder.Enqueue(stampKey);
        if (_recentFiniteCylinderStampOrder.Count > RecentFiniteCylinderStampCapacity)
            _recentFiniteCylinderStamps.Remove(_recentFiniteCylinderStampOrder.Dequeue());

        var back = tip + (axis * (float)length);
        var minimum = Vector3.Min(tip, back) - new Vector3((float)radius);
        var maximum = Vector3.Max(tip, back) + new Vector3((float)radius);
        var removedX = 0.0;
        var removedY = 0.0;
        var removedZ = 0.0;
        var changedXMinimum = new Vector3(float.PositiveInfinity);
        var changedXMaximum = new Vector3(float.NegativeInfinity);
        var changedYMinimum = new Vector3(float.PositiveInfinity);
        var changedYMaximum = new Vector3(float.NegativeInfinity);
        var changedZMinimum = new Vector3(float.PositiveInfinity);
        var changedZMaximum = new Vector3(float.NegativeInfinity);

        var (xYu0, xYu1) = CoordinateRange(_xField.U, minimum.Y, maximum.Y);
        var (xZv0, xZv1) = CoordinateRange(_xField.V, minimum.Z, maximum.Z);
        var (yXu0, yXu1) = CoordinateRange(_yField.U, minimum.X, maximum.X);
        var (yZv0, yZv1) = CoordinateRange(_yField.V, minimum.Z, maximum.Z);
        var (zXu0, zXu1) = CoordinateRange(_zField.U, minimum.X, maximum.X);
        var (zYv0, zYv1) = CoordinateRange(_zField.V, minimum.Y, maximum.Y);
        RunIndependentFieldPasses(
            RangeArea(xYu0, xYu1, xZv0, xZv1) +
            RangeArea(yXu0, yXu1, yZv0, yZv1) +
            RangeArea(zXu0, zXu1, zYv0, zYv1),
            () =>
            {
                for (var z = xZv0; z <= xZv1; z++)
                for (var y = xYu0; y <= xYu1; y++)
                {
                    var origin = new Vector3(0, (float)_xField.U[y], (float)_xField.V[z]);
                    if (!CylinderLineInterval(origin, Vector3.UnitX, tip, axis, length, radius, out var min, out var max))
                        continue;
                    var delta = _xField.Subtract(y, z, min, max, cutTag, out var changedMin, out var changedMax);
                    removedX += delta;
                    if (delta > 0)
                        IncludeChangedBounds(
                            ref changedXMinimum,
                            ref changedXMaximum,
                            new Vector3((float)changedMin, origin.Y, origin.Z),
                            new Vector3((float)changedMax, origin.Y, origin.Z));
                }
            },
            () =>
            {
                for (var z = yZv0; z <= yZv1; z++)
                for (var x = yXu0; x <= yXu1; x++)
                {
                    var origin = new Vector3((float)_yField.U[x], 0, (float)_yField.V[z]);
                    if (!CylinderLineInterval(origin, Vector3.UnitY, tip, axis, length, radius, out var min, out var max))
                        continue;
                    var delta = _yField.Subtract(x, z, min, max, cutTag, out var changedMin, out var changedMax);
                    removedY += delta;
                    if (delta > 0)
                        IncludeChangedBounds(
                            ref changedYMinimum,
                            ref changedYMaximum,
                            new Vector3(origin.X, (float)changedMin, origin.Z),
                            new Vector3(origin.X, (float)changedMax, origin.Z));
                }
            },
            () =>
            {
                for (var y = zYv0; y <= zYv1; y++)
                for (var x = zXu0; x <= zXu1; x++)
                {
                    var origin = new Vector3((float)_zField.U[x], (float)_zField.V[y], 0);
                    if (!CylinderLineInterval(origin, Vector3.UnitZ, tip, axis, length, radius, out var min, out var max))
                        continue;
                    var delta = _zField.Subtract(x, y, min, max, cutTag, out var changedMin, out var changedMax);
                    removedZ += delta;
                    if (delta > 0)
                        IncludeChangedBounds(
                            ref changedZMinimum,
                            ref changedZMaximum,
                            new Vector3(origin.X, origin.Y, (float)changedMin),
                            new Vector3(origin.X, origin.Y, (float)changedMax));
                }
            });

        var changedMinimum = Vector3.Min(changedXMinimum, Vector3.Min(changedYMinimum, changedZMinimum));
        var changedMaximum = Vector3.Max(changedXMaximum, Vector3.Max(changedYMaximum, changedZMaximum));
        if (removedX > 1e-9 || removedY > 1e-9 || removedZ > 1e-9)
            MarkSurfaceChunksDirty(new Bounds3(changedMinimum, changedMaximum));

        return new TripleDexelVolume(removedX, removedY, removedZ);
    }

    private static void IncludeChangedBounds(
        ref Vector3 minimum,
        ref Vector3 maximum,
        Vector3 changedMinimum,
        Vector3 changedMaximum)
    {
        minimum = Vector3.Min(minimum, changedMinimum);
        maximum = Vector3.Max(maximum, changedMaximum);
    }

    /// <summary>
    /// Debug/render mesh derived from the Z dexel bundle.  It deliberately
    /// keeps each XY cell flat and inserts vertical walls where neighbouring
    /// heights differ, making the removed slot easy to inspect.
    /// </summary>
    public TriangleMeshData BuildZHeightFieldMesh()
    {
        var cellCountX = _zField.U.Length - 1;
        var cellCountY = _zField.V.Length - 1;
        var heights = new double[cellCountX, cellCountY];
        for (var y = 0; y < cellCountY; y++)
        for (var x = 0; x < cellCountX; x++)
            heights[x, y] = Math.Min(
                Math.Min(_zField.Ray(x, y).Top(Bounds.Min.Z), _zField.Ray(x + 1, y).Top(Bounds.Min.Z)),
                Math.Min(_zField.Ray(x, y + 1).Top(Bounds.Min.Z), _zField.Ray(x + 1, y + 1).Top(Bounds.Min.Z)));

        var positions = new List<float>(cellCountX * cellCountY * 12);
        var normals = new List<float>(positions.Capacity);
        var indices = new List<int>(cellCountX * cellCountY * 6);

        for (var y = 0; y < cellCountY; y++)
        for (var x = 0; x < cellCountX; x++)
        {
            var x0 = _zField.U[x];
            var x1 = _zField.U[x + 1];
            var y0 = _zField.V[y];
            var y1 = _zField.V[y + 1];
            var top = heights[x, y];
            AddQuad(new(x0, y0, top), new(x1, y0, top), new(x1, y1, top), new(x0, y1, top), Vector3.UnitZ);

            if (x == 0)
                AddWall(new(x0, y0, Bounds.Min.Z), new(x0, y1, Bounds.Min.Z), top, -Vector3.UnitX);
            if (x == cellCountX - 1)
                AddWall(new(x1, y1, Bounds.Min.Z), new(x1, y0, Bounds.Min.Z), top, Vector3.UnitX);
            if (y == 0)
                AddWall(new(x1, y0, Bounds.Min.Z), new(x0, y0, Bounds.Min.Z), top, -Vector3.UnitY);
            if (y == cellCountY - 1)
                AddWall(new(x0, y1, Bounds.Min.Z), new(x1, y1, Bounds.Min.Z), top, Vector3.UnitY);

            if (x + 1 < cellCountX && Math.Abs(top - heights[x + 1, y]) > 1e-9)
                AddStepWallX(x1, y0, y1, top, heights[x + 1, y]);
            if (y + 1 < cellCountY && Math.Abs(top - heights[x, y + 1]) > 1e-9)
                AddStepWallY(y1, x0, x1, top, heights[x, y + 1]);
        }

        AddQuad(
            new(Bounds.Min.X, Bounds.Max.Y, Bounds.Min.Z),
            new(Bounds.Max.X, Bounds.Max.Y, Bounds.Min.Z),
            new(Bounds.Max.X, Bounds.Min.Y, Bounds.Min.Z),
            new(Bounds.Min.X, Bounds.Min.Y, Bounds.Min.Z),
            -Vector3.UnitZ);

        return new TriangleMeshData
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
            Bounds = Bounds
        };

        void AddStepWallX(double x, double y0, double y1, double left, double right)
        {
            var low = Math.Min(left, right);
            var high = Math.Max(left, right);
            var normal = left > right ? Vector3.UnitX : -Vector3.UnitX;
            AddQuad(new(x, y0, low), new(x, y1, low), new(x, y1, high), new(x, y0, high), normal);
        }

        void AddStepWallY(double y, double x0, double x1, double lower, double upper)
        {
            var low = Math.Min(lower, upper);
            var high = Math.Max(lower, upper);
            var normal = lower > upper ? Vector3.UnitY : -Vector3.UnitY;
            AddQuad(new(x1, y, low), new(x0, y, low), new(x0, y, high), new(x1, y, high), normal);
        }

        void AddWall(Vector3d a, Vector3d b, double top, Vector3 normal) =>
            AddQuad(a, b, new(b.X, b.Y, top), new(a.X, a.Y, top), normal);

        void AddQuad(Vector3d a, Vector3d b, Vector3d c, Vector3d d, Vector3 normal)
        {
            var first = positions.Count / 3;
            AddVertex(a, normal); AddVertex(b, normal); AddVertex(c, normal); AddVertex(d, normal);
            indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
            indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
        }

        void AddVertex(Vector3d point, Vector3 normal)
        {
            positions.Add((float)point.X); positions.Add((float)point.Y); positions.Add((float)point.Z);
            normals.Add(normal.X); normals.Add(normal.Y); normals.Add(normal.Z);
        }
    }

    /// <summary>
    /// Same debug surface as BuildZHeightFieldMesh, split by the operation that
    /// exposed each top surface. Tag 0 is untouched stock; tag -1 is a target
    /// gouge in the Z comparison. This gives the viewer MANUS/NX-style cut
    /// provenance without changing the geometric subtraction result.
    /// </summary>
    public IReadOnlyList<TaggedTriangleMeshData> BuildTaggedZHeightFieldMeshes(ZDexelTarget? target = null)
    {
        var cellCountX = _zField.U.Length - 1;
        var cellCountY = _zField.V.Length - 1;
        var heights = new double[cellCountX, cellCountY];
        var tags = new int[cellCountX, cellCountY];
        for (var y = 0; y < cellCountY; y++)
        for (var x = 0; x < cellCountX; x++)
        {
            var rays = new[]
            {
                _zField.Ray(x, y), _zField.Ray(x + 1, y),
                _zField.Ray(x, y + 1), _zField.Ray(x + 1, y + 1)
            };
            var selected = rays.MinBy(ray => ray.Top(Bounds.Min.Z))!;
            var top = selected.Top(Bounds.Min.Z);
            var tag = selected.TopTag;
            if (target is not null)
            {
                var targetTop = Math.Min(
                    Math.Min(target.Top(x, y, Bounds.Min.Z), target.Top(x + 1, y, Bounds.Min.Z)),
                    Math.Min(target.Top(x, y + 1, Bounds.Min.Z), target.Top(x + 1, y + 1, Bounds.Min.Z)));
                if (targetTop > Bounds.Min.Z + 1e-7 && top < targetTop - (TargetPitch * 0.75)) tag = -1;
            }
            heights[x, y] = top;
            tags[x, y] = tag;
        }

        var builders = new Dictionary<int, TaggedMeshBuilder>();
        TaggedMeshBuilder Builder(int tag)
        {
            if (!builders.TryGetValue(tag, out var builder))
            {
                builder = new TaggedMeshBuilder(Bounds);
                builders[tag] = builder;
            }
            return builder;
        }

        for (var y = 0; y < cellCountY; y++)
        for (var x = 0; x < cellCountX; x++)
        {
            var x0 = _zField.U[x];
            var x1 = _zField.U[x + 1];
            var y0 = _zField.V[y];
            var y1 = _zField.V[y + 1];
            var top = heights[x, y];
            var tag = tags[x, y];
            Builder(tag).AddQuad(new(x0, y0, top), new(x1, y0, top), new(x1, y1, top), new(x0, y1, top), Vector3.UnitZ);

            if (x == 0) Builder(0).AddQuad(new(x0, y0, Bounds.Min.Z), new(x0, y1, Bounds.Min.Z), new(x0, y1, top), new(x0, y0, top), -Vector3.UnitX);
            if (x == cellCountX - 1) Builder(0).AddQuad(new(x1, y1, Bounds.Min.Z), new(x1, y0, Bounds.Min.Z), new(x1, y0, top), new(x1, y1, top), Vector3.UnitX);
            if (y == 0) Builder(0).AddQuad(new(x1, y0, Bounds.Min.Z), new(x0, y0, Bounds.Min.Z), new(x0, y0, top), new(x1, y0, top), -Vector3.UnitY);
            if (y == cellCountY - 1) Builder(0).AddQuad(new(x0, y1, Bounds.Min.Z), new(x1, y1, Bounds.Min.Z), new(x1, y1, top), new(x0, y1, top), Vector3.UnitY);

            if (x + 1 < cellCountX && Math.Abs(top - heights[x + 1, y]) > 1e-9)
            {
                var other = heights[x + 1, y];
                var low = Math.Min(top, other); var high = Math.Max(top, other);
                var wallTag = top < other ? tag : tags[x + 1, y];
                var normal = top > other ? Vector3.UnitX : -Vector3.UnitX;
                Builder(wallTag).AddQuad(new(x1, y0, low), new(x1, y1, low), new(x1, y1, high), new(x1, y0, high), normal);
            }
            if (y + 1 < cellCountY && Math.Abs(top - heights[x, y + 1]) > 1e-9)
            {
                var other = heights[x, y + 1];
                var low = Math.Min(top, other); var high = Math.Max(top, other);
                var wallTag = top < other ? tag : tags[x, y + 1];
                var normal = top > other ? Vector3.UnitY : -Vector3.UnitY;
                Builder(wallTag).AddQuad(new(x1, y1, low), new(x0, y1, low), new(x0, y1, high), new(x1, y1, high), normal);
            }
        }

        Builder(0).AddQuad(
            new(Bounds.Min.X, Bounds.Max.Y, Bounds.Min.Z),
            new(Bounds.Max.X, Bounds.Max.Y, Bounds.Min.Z),
            new(Bounds.Max.X, Bounds.Min.Y, Bounds.Min.Z),
            new(Bounds.Min.X, Bounds.Min.Y, Bounds.Min.Z),
            -Vector3.UnitZ);
        return builders.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build(pair.Key)).ToArray();
    }

    private sealed class TaggedMeshBuilder
    {
        private readonly List<float> _positions = new();
        private readonly List<float> _normals = new();
        private readonly List<int> _indices = new();
        private readonly Bounds3 _bounds;
        public TaggedMeshBuilder(Bounds3 bounds) => _bounds = bounds;

        public void AddQuad(Vector3d a, Vector3d b, Vector3d c, Vector3d d, Vector3 normal)
        {
            var first = _positions.Count / 3;
            Add(a, normal); Add(b, normal); Add(c, normal); Add(d, normal);
            _indices.Add(first); _indices.Add(first + 1); _indices.Add(first + 2);
            _indices.Add(first); _indices.Add(first + 2); _indices.Add(first + 3);
        }

        private void Add(Vector3d point, Vector3 normal)
        {
            _positions.Add((float)point.X); _positions.Add((float)point.Y); _positions.Add((float)point.Z);
            _normals.Add(normal.X); _normals.Add(normal.Y); _normals.Add(normal.Z);
        }

        public TaggedTriangleMeshData Build(int tag) => new(tag, new TriangleMeshData
        {
            Positions = _positions.ToArray(),
            Normals = _normals.ToArray(),
            Indices = _indices.ToArray(),
            Bounds = _bounds
        });
    }

    private readonly record struct Vector3d(double X, double Y, double Z);

    private static double[] Coordinates(double min, double max, double targetPitch)
    {
        var count = Math.Max(2, (int)Math.Ceiling((max - min) / targetPitch) + 1);
        var step = (max - min) / (count - 1);
        var values = new double[count];
        for (var i = 0; i < count; i++) values[i] = i == count - 1 ? max : min + (i * step);
        return values;
    }

    private static (int First, int Last) CoordinateRange(IReadOnlyList<double> coordinates, double min, double max)
    {
        if (coordinates.Count == 0 || max < coordinates[0] || min > coordinates[^1])
            return (0, -1);
        var first = LowerBound(coordinates, min);
        var last = UpperBound(coordinates, max) - 1;
        first = Math.Clamp(first, 0, coordinates.Count - 1);
        last = Math.Clamp(last, -1, coordinates.Count - 1);
        if (first > last) return (first, first - 1);
        return (first, last);
    }

    /// <summary>
    /// Intersects an infinite dexel line o+d*t with a flat-capped finite
    /// cylinder.  d is one of the Cartesian unit vectors, so t is directly the
    /// X, Y or Z coordinate stored by the corresponding dexel ray.
    /// </summary>
    private static PreparedCylinderRay PrepareCylinderRay(
        Vector3 cylinderTip,
        Vector3 cylinderAxis,
        double cylinderLength,
        double radius,
        Vector3 direction,
        double? startRadius = null)
    {
        var axialDirection = Vector3.Dot(direction, cylinderAxis);
        var radialDirection = direction - (cylinderAxis * axialDirection);
        var slope=startRadius is double initialRadius ? (radius-initialRadius)/cylinderLength : 0;
        var frustumBasis=PrepareFrustumBasis(cylinderAxis,direction,slope);
        return new PreparedCylinderRay(
            cylinderTip,
            cylinderAxis,
            cylinderLength,
            radius * radius,
            axialDirection,
            radialDirection,
            Vector3.Dot(radialDirection, radialDirection),
            startRadius ?? radius,
            slope,
            direction,
            frustumBasis);
    }

    private static int CollectCylinderIntervals(
        Vector3 origin,
        PreparedCylinderRay[] kernels,
        CylinderRayInterval[] intervals,
        ProfileRayBounds[]? bounds = null)
    {
        var count = 0;
        for (int section = 0; section < kernels.Length; section++)
        {
            if (bounds is not null && !bounds[section].Contains(origin)) continue;
            ref readonly var kernel = ref kernels[section];
            if (!PreparedCylinderLineInterval(origin, kernel, out var minimum, out var maximum))
                continue;

            var insert = count;
            while (insert > 0 && intervals[insert - 1].Minimum > minimum)
            {
                intervals[insert] = intervals[insert - 1];
                insert--;
            }
            intervals[insert] = new CylinderRayInterval(minimum, maximum);
            count++;
        }

        if (count <= 1) return count;
        const double epsilon = 1e-10;
        var mergedCount = 1;
        for (var read = 1; read < count; read++)
        {
            var previous = intervals[mergedCount - 1];
            var current = intervals[read];
            if (current.Minimum <= previous.Maximum + epsilon)
            {
                intervals[mergedCount - 1] = new CylinderRayInterval(
                    previous.Minimum,
                    Math.Max(previous.Maximum, current.Maximum));
            }
            else
            {
                intervals[mergedCount++] = current;
            }
        }
        return mergedCount;
    }

    private static bool PreparedCylinderLineInterval(
        Vector3 origin,
        in PreparedCylinderRay kernel,
        out double minimum,
        out double maximum)
    {
        if (kernel.RadiusSlope != 0)
            return PreparedFrustumLineInterval(origin, kernel, out minimum, out maximum);
        const double epsilon = 1e-10;
        var offset = origin - kernel.Tip;
        var axialOrigin = Vector3.Dot(offset, kernel.Axis);
        // A ray parallel to the end caps and outside their slab cannot touch
        // this finite layer. Reject it before the radial quadratic/square root.
        // The original intersection arithmetic is retained for every candidate.
        if (Math.Abs(kernel.AxialDirection) <= epsilon &&
            (axialOrigin < -epsilon || axialOrigin > kernel.Length + epsilon))
        {
            minimum = maximum = 0;
            return false;
        }
        var radialOrigin = offset - (kernel.Axis * axialOrigin);
        var b = 2.0 * Vector3.Dot(radialOrigin, kernel.RadialDirection);
        var c = Vector3.Dot(radialOrigin, radialOrigin) - kernel.RadiusSquared;

        var radialMin = double.NegativeInfinity;
        var radialMax = double.PositiveInfinity;
        if (kernel.RadialDirectionSquared <= epsilon)
        {
            if (c > epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
        }
        else
        {
            var discriminant = (b * b) - (4.0 * kernel.RadialDirectionSquared * c);
            if (discriminant < -epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
            var root = Math.Sqrt(Math.Max(0, discriminant));
            radialMin = (-b - root) / (2.0 * kernel.RadialDirectionSquared);
            radialMax = (-b + root) / (2.0 * kernel.RadialDirectionSquared);
        }

        var axialMin = double.NegativeInfinity;
        var axialMax = double.PositiveInfinity;
        if (Math.Abs(kernel.AxialDirection) <= epsilon)
        {
            if (axialOrigin < -epsilon || axialOrigin > kernel.Length + epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
        }
        else
        {
            axialMin = -axialOrigin / kernel.AxialDirection;
            axialMax = (kernel.Length - axialOrigin) / kernel.AxialDirection;
            if (axialMin > axialMax) (axialMin, axialMax) = (axialMax, axialMin);
        }

        minimum = Math.Max(radialMin, axialMin);
        maximum = Math.Min(radialMax, axialMax);
        return maximum > minimum + epsilon;
    }

    private static bool CylinderLineInterval(
        Vector3 origin,
        Vector3 direction,
        Vector3 cylinderTip,
        Vector3 cylinderAxis,
        double cylinderLength,
        double radius,
        out double minimum,
        out double maximum)
    {
        const double epsilon = 1e-10;
        var offset = origin - cylinderTip;
        var axialOrigin = Vector3.Dot(offset, cylinderAxis);
        var axialDirection = Vector3.Dot(direction, cylinderAxis);
        var radialDirection = direction - (cylinderAxis * axialDirection);
        var radialOrigin = offset - (cylinderAxis * axialOrigin);
        var a = Vector3.Dot(radialDirection, radialDirection);
        var b = 2.0 * Vector3.Dot(radialOrigin, radialDirection);
        var c = Vector3.Dot(radialOrigin, radialOrigin) - (radius * radius);

        var radialMin = double.NegativeInfinity;
        var radialMax = double.PositiveInfinity;
        if (a <= epsilon)
        {
            if (c > epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
        }
        else
        {
            var discriminant = (b * b) - (4.0 * a * c);
            if (discriminant < -epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
            var root = Math.Sqrt(Math.Max(0, discriminant));
            radialMin = (-b - root) / (2.0 * a);
            radialMax = (-b + root) / (2.0 * a);
        }

        var axialMin = double.NegativeInfinity;
        var axialMax = double.PositiveInfinity;
        if (Math.Abs(axialDirection) <= epsilon)
        {
            if (axialOrigin < -epsilon || axialOrigin > cylinderLength + epsilon)
            {
                minimum = maximum = 0;
                return false;
            }
        }
        else
        {
            axialMin = -axialOrigin / axialDirection;
            axialMax = (cylinderLength - axialOrigin) / axialDirection;
            if (axialMin > axialMax) (axialMin, axialMax) = (axialMax, axialMin);
        }

        minimum = Math.Max(radialMin, axialMin);
        maximum = Math.Min(radialMax, axialMax);
        return maximum > minimum + epsilon;
    }

    private static double DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 1e-20f) return Vector2.DistanceSquared(point, a);
        var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0, 1);
        return Vector2.DistanceSquared(point, a + (t * ab));
    }

    private static double DistanceSquaredToPolyline(Vector2 point, IReadOnlyList<Vector2> path)
    {
        var distance = double.PositiveInfinity;
        for (var index = 1; index < path.Count; index++)
            distance = Math.Min(distance, DistanceSquaredToSegment(point, path[index - 1], path[index]));
        return distance;
    }

    private static int CollectPolylineIntervals(
        IReadOnlyList<Vector2> path,
        double radius,
        double fixedCoordinate,
        bool alongX,
        CylinderRayInterval[] intervals)
    {
        var count = 0;
        for (var index = 1; index < path.Count; index++)
        {
            if (!CapsuleLineInterval(
                    path[index - 1], path[index], radius, fixedCoordinate, alongX,
                    out var minimum, out var maximum))
                continue;
            var insert = count;
            while (insert > 0 && intervals[insert - 1].Minimum > minimum)
            {
                intervals[insert] = intervals[insert - 1];
                insert--;
            }
            intervals[insert] = new CylinderRayInterval(minimum, maximum);
            count++;
        }

        if (count <= 1) return count;
        const double epsilon = 1e-10;
        var mergedCount = 1;
        for (var read = 1; read < count; read++)
        {
            var previous = intervals[mergedCount - 1];
            var current = intervals[read];
            if (current.Minimum <= previous.Maximum + epsilon)
                intervals[mergedCount - 1] = new CylinderRayInterval(
                    previous.Minimum,
                    Math.Max(previous.Maximum, current.Maximum));
            else
                intervals[mergedCount++] = current;
        }
        return mergedCount;
    }

    private static bool CapsuleLineInterval(
        Vector2 a,
        Vector2 b,
        double radius,
        double fixedCoordinate,
        bool alongX,
        out double min,
        out double max)
    {
        const double epsilon = 1e-10;
        var directionX = alongX ? 1.0 : 0.0;
        var directionY = alongX ? 0.0 : 1.0;
        var originX = (alongX ? 0.0 : fixedCoordinate) - a.X;
        var originY = (alongX ? fixedCoordinate : 0.0) - a.Y;
        var segmentX = b.X - a.X;
        var segmentY = b.Y - a.Y;
        var segmentLengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
        var radiusSquared = radius * radius;
        var resultMin = double.PositiveInfinity;
        var resultMax = double.NegativeInfinity;

        AddCircle(originX, originY);
        AddCircle(originX - segmentX, originY - segmentY);

        if (segmentLengthSquared > epsilon)
        {
            var parameterOrigin = ((originX * segmentX) + (originY * segmentY)) /
                                  segmentLengthSquared;
            var parameterDirection = ((directionX * segmentX) + (directionY * segmentY)) /
                                     segmentLengthSquared;
            if (TryBand(parameterOrigin, parameterDirection, out var bandMin, out var bandMax))
            {
                var perpendicularOriginX = originX - (segmentX * parameterOrigin);
                var perpendicularOriginY = originY - (segmentY * parameterOrigin);
                var perpendicularDirectionX = directionX - (segmentX * parameterDirection);
                var perpendicularDirectionY = directionY - (segmentY * parameterDirection);
                if (TryCircle(
                        perpendicularOriginX, perpendicularOriginY,
                        perpendicularDirectionX, perpendicularDirectionY,
                        out var radialMin, out var radialMax))
                    AddInterval(Math.Max(bandMin, radialMin), Math.Min(bandMax, radialMax));
            }
        }

        min = resultMin;
        max = resultMax;
        return resultMax > resultMin + epsilon;

        void AddCircle(double offsetX, double offsetY)
        {
            if (TryCircle(offsetX, offsetY, directionX, directionY, out var circleMin, out var circleMax))
                AddInterval(circleMin, circleMax);
        }

        void AddInterval(double intervalMin, double intervalMax)
        {
            if (intervalMax <= intervalMin + epsilon) return;
            resultMin = Math.Min(resultMin, intervalMin);
            resultMax = Math.Max(resultMax, intervalMax);
        }

        bool TryBand(double value, double slope, out double intervalMin, out double intervalMax)
        {
            if (Math.Abs(slope) <= epsilon)
            {
                if (value < -epsilon || value > 1 + epsilon)
                {
                    intervalMin = intervalMax = 0;
                    return false;
                }
                intervalMin = double.NegativeInfinity;
                intervalMax = double.PositiveInfinity;
                return true;
            }
            intervalMin = -value / slope;
            intervalMax = (1 - value) / slope;
            if (intervalMin > intervalMax) (intervalMin, intervalMax) = (intervalMax, intervalMin);
            return intervalMax > intervalMin + epsilon;
        }

        bool TryCircle(
            double offsetX,
            double offsetY,
            double rayX,
            double rayY,
            out double intervalMin,
            out double intervalMax)
        {
            var quadratic = (rayX * rayX) + (rayY * rayY);
            var linear = 2 * ((offsetX * rayX) + (offsetY * rayY));
            var constant = (offsetX * offsetX) + (offsetY * offsetY) - radiusSquared;
            if (quadratic <= epsilon)
            {
                if (constant > epsilon)
                {
                    intervalMin = intervalMax = 0;
                    return false;
                }
                intervalMin = double.NegativeInfinity;
                intervalMax = double.PositiveInfinity;
                return true;
            }
            var discriminant = (linear * linear) - (4 * quadratic * constant);
            if (discriminant < -epsilon)
            {
                intervalMin = intervalMax = 0;
                return false;
            }
            var root = Math.Sqrt(Math.Max(0, discriminant));
            intervalMin = (-linear - root) / (2 * quadratic);
            intervalMax = (-linear + root) / (2 * quadratic);
            return intervalMax > intervalMin + epsilon;
        }
    }
}

