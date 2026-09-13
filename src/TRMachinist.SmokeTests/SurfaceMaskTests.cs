using System.Reflection;
using TRMachinist.Core;

internal static class SurfaceMaskTests
{
    public static void TileBoundarySearch()
    {
        var search = typeof(TripleDexelStock).GetMethod("SurfaceBoundaryIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (int count in new[] { 1, 2, 31, 32, 33, 63, 64, 65, 101, 257 })
        foreach (double origin in new[] { -70000.123, -7.37, 0.0, 50000.321 })
        {
            var coordinates = Enumerable.Range(0, count).Select(i => origin + i * .15).ToArray();
            var boundaries = coordinates.SelectMany(c => new[] { Math.BitDecrement(c), c, Math.BitIncrement(c), c - .01, c + .01 })
                .Concat(new[] { origin - 100, origin + count * .15 + 100 });
            foreach (double boundary in boundaries)
            for (int start = 0; start < count + 1; start += 32)
            {
                int end = Math.Min(start + 32, count + 1);
                // Independent full-array search; either adjacent edge may
                // belong to the tile, including the final exterior edge.
                int full = 0;
                while (full < count && coordinates[full] < boundary) full++;
                int local = (int)search.Invoke(null, new object[] { coordinates, boundary, start, end })!;
                int[] Owned(int first) => first < 0 ? [] : new[] { first, first + 1 }
                    .Where(i => i >= start && i < end && i <= count).ToArray();
                if (!Owned(full).SequenceEqual(Owned(local)))
                    throw new Exception($"Surface tile lost an edge: {count}/{origin}/{boundary}/{start}");
            }
        }
    }

    public static void ExactBoundaryOccupancy()
    {
        // Independent dense Contains oracle, including inclusive epsilon edges,
        // empty/split rays, a full 32-bit tile and the final truncated tile.
        var rayType = typeof(TripleDexelStock).GetNestedType("DexelRay", BindingFlags.NonPublic)!;
        var constructor = rayType.GetConstructor(new[] { typeof(IEnumerable<DexelSpan>) })!;
        var sample = typeof(TripleDexelStock).GetMethod("SampleMask", BindingFlags.Static | BindingFlags.NonPublic)!;
        var coordinates = Enumerable.Range(0, 101).Select(i => -7.37 + i * 0.15).ToArray();
        var random = new Random(31907);
        for (var trial = 0; trial < 600; trial++)
        {
            var spans = new List<DexelSpan>();
            for (var i = 0; i < 6; i++)
            {
                var index = i * 15 + random.Next(0, 5);
                var epsilon = new[] { -1e-8, 0, 1e-8 }[trial % 3];
                spans.Add(new DexelSpan(coordinates[index] + epsilon,
                    coordinates[index + random.Next(1, 10)] - epsilon));
            }
            if (trial % 9 == 0) spans.Clear();
            if (trial % 11 == 0) spans = new() { new(-100, 100) };
            var ray = constructor.Invoke(new object[] { spans });
            foreach (var start in new[] { 0, 32, 64, 96 })
            {
                var end = Math.Min(start + 32, coordinates.Length);
                uint expected = 0;
                for (var i = start; i < end; i++)
                    if (spans.Any(span => coordinates[i] >= span.Min - 1e-8 && coordinates[i] <= span.Max + 1e-8))
                        expected |= 1u << (i - start);
                var actual = (uint)sample.Invoke(null, new object?[] { ray, coordinates, start, start + 32 })!;
                if (actual != expected) throw new Exception($"Surface occupancy differs at {trial}/{start}.");
            }
        }
    }
}
