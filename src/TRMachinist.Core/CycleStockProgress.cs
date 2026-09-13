namespace TRMachinist.Core;

public readonly record struct CycleStockSlice(CycleMotionPhase Phase, double From, double To);

/// <summary>Uses the player's own phase clock; rapid and dwell never cut.</summary>
public static class CycleStockProgress
{
    public static IReadOnlyList<CycleStockSlice> Select(GeneratedCycleMotion cycle, double from, double to)
    {
        from = Math.Clamp(from, 0, 1); to = Math.Clamp(to, 0, 1);
        if (to <= from) return Array.Empty<CycleStockSlice>();
        var total = cycle.Phases.Sum(SimulationPlayer.CyclePhaseDuration);
        if (total <= 1e-12) return Array.Empty<CycleStockSlice>();
        var first = from * total; var last = to * total;
        var elapsed = 0.0;
        var result = new List<CycleStockSlice>();
        foreach (var phase in cycle.Phases)
        {
            var duration = SimulationPlayer.CyclePhaseDuration(phase);
            var end = elapsed + duration;
            if (phase.Kind is CycleMotionPhaseKind.FeedIn or CycleMotionPhaseKind.FeedRetract)
            {
                if (duration > 1e-12 && last > elapsed && first < end)
                    result.Add(new(phase, Math.Clamp((first - elapsed) / duration, 0, 1),
                        Math.Clamp((last - elapsed) / duration, 0, 1)));
                else if (duration <= 1e-12 && end <= last && (end > first || from == 0 && end == 0))
                    result.Add(new(phase, 0, 1));
            }
            elapsed = end;
        }
        return result;
    }
}
