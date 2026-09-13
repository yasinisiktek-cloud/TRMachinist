using System.Collections.Concurrent;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private struct StableSum
    {
        internal double Value;
        private double _correction;
        internal void Add(double value)
        {
            var corrected = value - _correction;
            var next = Value + corrected;
            _correction = (next - Value) - corrected;
            Value = next;
        }
    }
    // Only the Z field acquires this cache, and only when verification is used.
    // Cutting never reads target geometry or changes its stock subtraction.
    private sealed class TargetComparisonCache
    {
        internal readonly ZDexelTarget Target;
        internal readonly double[] Missing, Excess;
        internal readonly bool[] Dirty;
        internal readonly ConcurrentQueue<int> Changes = new();
        internal StableSum TargetVolume, MissingVolume, ExcessVolume;

        internal TargetComparisonCache(ZDexelTarget target, int count)
        {
            Target = target;
            Missing = new double[count]; Excess = new double[count]; Dirty = new bool[count];
        }

        internal void Changed(int index)
        {
            // Field passes own disjoint rays. Comparisons run after all passes
            // join, under the same stock owner lock as cutting and meshing.
            if (Dirty[index]) return;
            Dirty[index] = true;
            Changes.Enqueue(index);
        }
    }

    /// <summary>
    /// Exact Z-ray target comparison, refreshing only rays changed since the
    /// last call. Like other stock queries this must not run during a cut.
    /// CompareWithTarget remains the independent complete-scan reference.
    /// </summary>
    public ZDexelTargetComparison CompareWithTargetIncremental(ZDexelTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.X.AsSpan().SequenceEqual(_zField.U) || !target.Y.AsSpan().SequenceEqual(_zField.V))
            throw new ArgumentException("Hedef dexel ızgarası stok ızgarasıyla aynı değil.", nameof(target));
        var cache = _zField.ComparisonCache;
        if (cache is null || !ReferenceEquals(cache.Target, target))
        {
            cache = new TargetComparisonCache(target, target.X.Length * target.Y.Length);
            for (int index = 0; index < cache.Missing.Length; index++) Refresh(index, true);
            _zField.ComparisonCache = cache;
        }
        else
        {
            while (cache.Changes.TryDequeue(out int index))
            {
                Refresh(index, false);
                cache.Dirty[index] = false;
            }
        }
        return new(cache.TargetVolume.Value, Math.Max(0, cache.MissingVolume.Value), Math.Max(0, cache.ExcessVolume.Value));

        void Refresh(int index, bool initialize)
        {
            int x = index % target.X.Length, y = index / target.X.Length;
            var targetRay = target.Ray(x, y);
            var stockRay = _zField.Ray(x, y);
            double overlap = 0;
            foreach (var a in targetRay)
            foreach (var b in stockRay.Spans)
                overlap += Math.Max(0, Math.Min(a.Max, b.Max) - Math.Max(a.Min, b.Min));
            var area = target.XWeights[x] * target.YWeights[y];
            double missing = Math.Max(0, targetRay.Length - overlap) * area;
            double excess = Math.Max(0, stockRay.Length - overlap) * area;
            if (initialize) cache.TargetVolume.Add(targetRay.Length * area);
            cache.MissingVolume.Add(missing - cache.Missing[index]);
            cache.ExcessVolume.Add(excess - cache.Excess[index]);
            cache.Missing[index] = missing; cache.Excess[index] = excess;
        }
    }
}
