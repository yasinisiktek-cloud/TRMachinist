namespace TRMachinist.Core;

/// <summary>
/// Converts the speed slider into a target instead of an unconditional clock.
/// Expensive stock/render frames lower the effective clock rate, and time spent
/// blocked on the UI thread is never paid back as a later simulation jump.
/// This class affects only the NC timeline; stock pitch and deterministic cut
/// segmentation remain entirely independent.
/// </summary>
public sealed class AdaptivePlaybackGovernor
{
    private const double MaximumWallStepMilliseconds = 24.0;
    private const double TargetWorkMilliseconds = 18.0;
    private const double OverloadThresholdMilliseconds = 25.0;
    private const double RecoveryThresholdMilliseconds = 12.0;
    private const double MinimumLoadScale = 0.10;
    // Do not impose a permanent synthetic ceiling before the measured engine
    // has done any work. The bounded pipeline supplies the real back-pressure.
    private const double InitialStockSpeedCeiling = 25.0;

    private int _underBudgetFrames;

    public double LoadScale { get; private set; } = 1.0;

    public double EffectiveSpeed(double requestedSpeed) =>
        Math.Clamp(requestedSpeed, 0.05, 100.0) * LoadScale;

    public void Reset(double requestedSpeed, bool stockSimulationActive)
    {
        var requested = Math.Clamp(requestedSpeed, 0.05, 100.0);
        LoadScale = stockSimulationActive && requested > InitialStockSpeedCeiling
            ? InitialStockSpeedCeiling / requested
            : 1.0;
        _underBudgetFrames = 0;
    }

    /// <summary>
    /// Returns the wall-clock slice that SimulationPlayer may consume.  The
    /// hard cap is the no-catch-up guarantee: a 300 ms blocked frame can never
    /// turn into a 300 ms * requested-speed leap on the next tick.
    /// </summary>
    public TimeSpan CreateAdvanceStep(TimeSpan wallElapsed)
    {
        var milliseconds = Math.Clamp(
            wallElapsed.TotalMilliseconds,
            0,
            MaximumWallStepMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds * LoadScale);
    }

    /// <summary>
    /// Applies immediate back-pressure on an overloaded frame and recovers
    /// conservatively only after several genuinely light frames.
    /// </summary>
    public void ObserveWork(TimeSpan workElapsed)
    {
        var milliseconds = Math.Max(0, workElapsed.TotalMilliseconds);
        if (milliseconds >= OverloadThresholdMilliseconds)
        {
            var pressure = Math.Clamp(
                TargetWorkMilliseconds / Math.Max(milliseconds, 0.001),
                0.55,
                0.90);
            LoadScale = Math.Clamp(LoadScale * pressure, MinimumLoadScale, 1.0);
            _underBudgetFrames = 0;
            return;
        }

        if (milliseconds > RecoveryThresholdMilliseconds)
        {
            _underBudgetFrames = 0;
            return;
        }

        _underBudgetFrames++;
        if (_underBudgetFrames < 3 || LoadScale >= 1.0) return;

        // Slow recovery prevents the controller from oscillating between a
        // smooth low rate and the same overloaded high rate every few frames.
        LoadScale = Math.Min(1.0, LoadScale + Math.Max(0.025, (1.0 - LoadScale) * 0.20));
        _underBudgetFrames = 0;
    }

    /// <summary>
    /// One observation per synchronized frame. The ready-stock gate already
    /// waits for background computation; it must not also be treated as a UI
    /// stall. Bound long kernel steps with a 75 ms overload / 36 ms recovery
    /// window while retaining the tighter UI budget. Two separate observations
    /// reset the recovery streak on every moderately costly kernel step.
    /// </summary>
    public void ObserveSynchronizedWork(TimeSpan kernelElapsed, TimeSpan presentationElapsed) =>
        ObserveWork(TimeSpan.FromMilliseconds(Math.Max(
            kernelElapsed.TotalMilliseconds / 3.0, presentationElapsed.TotalMilliseconds)));

    /// <summary>
    /// Applies smooth back-pressure from the asynchronous stock pipelines.
    /// This changes only the NC clock: exact dexel pitch and cut segmentation
    /// remain untouched. Repeated small reductions avoid the visible
    /// stop/jump cycle of a hard pause while preventing the cutter from running
    /// far ahead of either authoritative stock or its live delta surface.
    /// </summary>
    public void ObserveStockBacklog(double exactBlockLag, int pendingDisplayCuts)
    {
        exactBlockLag = Math.Max(0, exactBlockLag);
        pendingDisplayCuts = Math.Max(0, pendingDisplayCuts);
        var exactCeiling = exactBlockLag switch
        {
            >= 64.0 => 0.40,
            >= 24.0 => 0.60,
            >= 8.0 => 0.78,
            >= 3.0 => 0.90,
            _ => 1.0
        };
        // A short display FIFO is expected: one NC move may contain several
        // cutting-profile layers. Only a genuinely large visual queue limits
        // playback, and even then it sets a ceiling once rather than
        // multiplying the speed down on every 16 ms timer tick.
        var displayCeiling = pendingDisplayCuts switch
        {
            >= 512 => 0.25,
            >= 256 => 0.50,
            _ => 1.0
        };
        var ceiling = Math.Min(exactCeiling, displayCeiling);
        if (ceiling >= 1.0) return;
        LoadScale = Math.Clamp(Math.Min(LoadScale, ceiling), MinimumLoadScale, 1.0);
        _underBudgetFrames = 0;
    }
}
