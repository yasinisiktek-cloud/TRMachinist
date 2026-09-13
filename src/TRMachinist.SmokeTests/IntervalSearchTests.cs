using System.Reflection;
using TRMachinist.Core;

internal static class IntervalSearchTests
{
    public static void OrderedCavities()
    {
        // Exercise the production ray mutation with an independent, full-scan
        // subtraction oracle. Reflection exposes only the private test boundary.
        var type = typeof(TripleDexelStock).GetNestedType("DexelRay", BindingFlags.NonPublic)!;
        var subtract = type.GetMethod("Subtract")!;
        var count = type.GetProperty("Count")!;
        var item = type.GetProperty("Item")!;
        var random = new Random(2819);
        var assertions = 0;
        foreach (var size in new[] { 1, 8, 9, 96, 1024 })
        {
            var expected = Enumerable.Range(0, size).Select(i => new DexelSpan(i * 1.5, i * 1.5 + 1)).ToList();
            var ray = Activator.CreateInstance(type, new object[] { expected })!;
            var cuts = new List<(double A, double B)> {
                (-2, -1), (size * 1.5 + 1, size * 1.5 + 2),
                (0, 0), (.25, .75), (1 - 1e-10, 1.2), (1.2, 1.5 + 1e-10)
            };
            cuts.AddRange(Enumerable.Range(0, 350).Select(_ => {
                var a = random.NextDouble() * size * 1.5 - .5;
                return (a, a + random.NextDouble() * 1.1);
            }));
            cuts.Add((-1, size * 1.5 + 2));
            foreach (var (a, b) in cuts)
            {
                var next = new List<DexelSpan>();
                var expectedRemoved = 0.0;
                var changedMin = double.PositiveInfinity; var changedMax = double.NegativeInfinity;
                const double eps = 1e-10;
                foreach (var s in expected)
                {
                    var lo = Math.Max(a, s.Min); var hi = Math.Min(b, s.Max);
                    if (b <= a || b <= s.Min + eps || a >= s.Max - eps || hi <= lo + eps)
                    { next.Add(s); continue; }
                    expectedRemoved += hi - lo;
                    changedMin = Math.Min(changedMin, lo); changedMax = Math.Max(changedMax, hi);
                    if (a > s.Min + eps) next.Add(new(s.Min, a));
                    if (b < s.Max - eps) next.Add(new(b, s.Max));
                }
                var arguments = new object[] { a, b, 7, 0d, 0d };
                var removed = (double)subtract.Invoke(ray, arguments)!;
                if (removed != expectedRemoved || (double)arguments[3] != changedMin || (double)arguments[4] != changedMax)
                    throw new Exception("Ordered search changed removal or dirty bounds.");
                if ((int)count.GetValue(ray)! != next.Count) throw new Exception("Ordered search lost a cavity.");
                for (var i = 0; i < next.Count; i++)
                    if ((DexelSpan)item.GetValue(ray, new object[] { i })! != next[i])
                        throw new Exception("Ordered search changed an interval boundary.");
                expected = next; assertions++;
            }
        }
        Console.WriteLine($"      {assertions} ordered interval mutations matched full-scan oracle exactly.");
    }
}
