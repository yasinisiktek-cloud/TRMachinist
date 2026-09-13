namespace TRMachinist.Core;

public sealed class SimulationPlayer
{
    private double _elapsed;
    private int _firstBlockedBlock = int.MaxValue;

    public GCodeProgram? Program { get; private set; }
    /// <summary>The block that will continue executing on the next tick.</summary>
    public int BlockIndex { get; private set; } = -1;
    /// <summary>
    /// The NC block whose start/end interpolation produced the position currently
    /// rendered on screen.  This intentionally remains on a just-completed block
    /// until the next block actually starts; otherwise a rotary move appears to
    /// happen on the following source line.
    /// </summary>
    public int DisplayedBlockIndex { get; private set; } = -1;
    /// <summary>0..1 progress of the block represented by Position.</summary>
    public double DisplayedBlockProgress { get; private set; }
    public AxisState Position { get; private set; }
    public bool IsPlaying { get; private set; }
    public double SpeedFactor { get; set; } = 1;

    public GCodeBlock? CurrentBlock => Program is not null && DisplayedBlockIndex >= 0 && DisplayedBlockIndex < Program.Blocks.Count
        ? Program.Blocks[DisplayedBlockIndex]
        : null;

    public void Load(GCodeProgram program)
    {
        Program = program;
        _firstBlockedBlock = int.MaxValue;
        for (var i=0; i<program.Blocks.Count; i++)
            if (program.Blocks[i].ExecutionBlocked) { _firstBlockedBlock=i; break; }
        BlockIndex = program.Blocks.Count > 0 ? 0 : -1;
        DisplayedBlockIndex = BlockIndex;
        Position = BlockIndex >= 0 ? program.Blocks[0].Start : default;
        DisplayedBlockProgress = 0;
        _elapsed = 0;
        IsPlaying = false;
    }

    public void Play()
    {
        if (Program is { Blocks.Count: > 0 } && BlockIndex < Program.Blocks.Count) IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Reset()
    {
        if (Program is null) return;
        BlockIndex = Program.Blocks.Count > 0 ? 0 : -1;
        DisplayedBlockIndex = BlockIndex;
        Position = BlockIndex >= 0 ? Program.Blocks[0].Start : default;
        DisplayedBlockProgress = 0;
        _elapsed = 0;
        IsPlaying = false;
    }

    public bool Step()
    {
        if (Program is null || Program.Blocks.Count == 0) return false;
        if (BlockIndex < 0) BlockIndex = 0;
        if (BlockIndex < Program.Blocks.Count && _elapsed + 1e-9 >= Duration(Program.Blocks[BlockIndex]))
        {
            BlockIndex++;
            _elapsed = 0;
        }
        if (BlockIndex >= Program.Blocks.Count)
        {
            IsPlaying = false;
            return false;
        }

        var executedIndex = BlockIndex;
        if (PresentControllerError(Program.Blocks[executedIndex])) return true;
        Position = Program.Blocks[executedIndex].End;
        DisplayedBlockIndex = executedIndex;
        DisplayedBlockProgress = 1;
        BlockIndex = executedIndex + 1;
        _elapsed = 0;
        if (BlockIndex >= Program.Blocks.Count) IsPlaying = false;
        return true;
    }

    public bool Seek(int blockIndex, bool completed = false)
    {
        if (Program is null || Program.Blocks.Count == 0) return false;
        BlockIndex = Math.Clamp(blockIndex, 0, Program.Blocks.Count - 1);
        if (BlockIndex >= _firstBlockedBlock) { BlockIndex = _firstBlockedBlock; completed = false; }
        DisplayedBlockIndex = BlockIndex;
        var block = Program.Blocks[BlockIndex];
        Position = completed ? block.End : block.Start;
        _elapsed = completed ? Duration(block) : 0;
        DisplayedBlockProgress = completed ? 1 : 0;
        return true;
    }

    public bool Advance(TimeSpan elapsed)
    {
        if (!IsPlaying || Program is null || BlockIndex < 0 || BlockIndex >= Program.Blocks.Count) return false;
        var changed = false;
        var remaining = Math.Max(0, elapsed.TotalSeconds) * Math.Clamp(SpeedFactor, 0.05, 100);

        while (remaining > 0 && BlockIndex < Program.Blocks.Count)
        {
            var block = Program.Blocks[BlockIndex];
            if (PresentControllerError(block)) return true;
            var duration = Duration(block);
            var available = duration - _elapsed;
            var consume = Math.Min(remaining, available);
            _elapsed += consume;
            remaining -= consume;
            var t = duration <= 0 ? 1 : Math.Clamp(_elapsed / duration, 0, 1);
            Position = Sample(block, t);
            DisplayedBlockIndex = BlockIndex;
            DisplayedBlockProgress = t;
            changed = true;

            if (_elapsed + 1e-9 < duration) break;
            Position = block.End;
            DisplayedBlockIndex = BlockIndex;
            DisplayedBlockProgress = 1;
            BlockIndex++;
            _elapsed = 0;

            // A high playback factor can otherwise consume SUPA -> M6 -> the
            // next work move inside one dispatcher frame.  The machine really
            // reached its change position, but the viewer only displayed the
            // final work position.  M6 is therefore a semantic render barrier:
            // discard unused wall time and expose the reference pose for at
            // least one frame before continuing the NC stream.
            if (block.IsToolChange) break;
        }

        if (BlockIndex >= Program.Blocks.Count) IsPlaying = false;
        return changed;
    }

    private bool PresentControllerError(GCodeBlock block)
    {
        if (!block.ExecutionBlocked) return false;
        IsPlaying = false;
        DisplayedBlockIndex = BlockIndex;
        DisplayedBlockProgress = 0;
        Position = block.Start;
        _elapsed = 0;
        return true;
    }

    /// <summary>
    /// Position inside a block.  A G2/G3 block carries its sampled arc/helix
    /// path, and the animation has to follow that path - a straight
    /// start-to-end interpolation would cut a TURN= helix down to one chord.
    /// </summary>
    private static AxisState Sample(GCodeBlock block, double t)
    {
        if (block.CycleMotion is { Phases.Count: > 0 } cycle)
            return SampleCycle(block, cycle, t);
        if (block.Path is not { Samples.Count: > 0 } path)
            return AxisState.Lerp(block.Start, block.End, t);
        if (t <= 0) return block.Start;
        if (t >= 1) return block.End;
        var scaled = t * path.Samples.Count;
        var index = (int)Math.Floor(scaled);
        var local = scaled - index;
        var from = index <= 0 ? block.Start : path.Samples[index - 1];
        var to = path.Samples[Math.Min(index, path.Samples.Count - 1)];
        return AxisState.Lerp(from, to, local);
    }

    private static AxisState SampleCycle(GCodeBlock block, GeneratedCycleMotion cycle, double t)
    {
        if (t <= 0) return block.Start;
        if (t >= 1) return block.End;
        var rawDuration = cycle.Phases.Sum(CyclePhaseDuration);
        if (rawDuration <= 1e-12) return block.End;
        var remaining = rawDuration * t;
        foreach (var phase in cycle.Phases)
        {
            var phaseDuration = CyclePhaseDuration(phase);
            if (phaseDuration <= 1e-12) continue;
            if (remaining <= phaseDuration)
            {
                if (phase.Kind == CycleMotionPhaseKind.Dwell) return phase.Start;
                return AxisState.Lerp(phase.Start, phase.End, Math.Clamp(remaining / phaseDuration, 0, 1));
            }
            remaining -= phaseDuration;
        }
        return block.End;
    }

    private static double Duration(GCodeBlock block)
    {
        // A real M6 is not an instantaneous modal word. Giving every tool
        // change a small simulation duration makes the already executed SUPA
        // reference pose visible at normal and high playback rates; the
        // semantic barrier in Advance still prevents the following work move
        // from consuming it in the same dispatcher frame.
        if (block.IsToolChange) return 1.0;
        if (!block.HasMotion) return 0.02;
        if (block.CycleMotion is { } cycle)
        {
            var seconds = cycle.Phases.Sum(CyclePhaseDuration);
            return Math.Clamp(seconds, 0.03, 15.0 + cycle.DwellSeconds);
        }
        var dx = block.End.X - block.Start.X;
        var dy = block.End.Y - block.Start.Y;
        var dz = block.End.Z - block.Start.Z;
        var linearDistance = block.Path is { Length: > 0 } arc
            ? arc.Length
            : Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        var rotaryDistance = Math.Max(Math.Abs(block.End.A - block.Start.A),
            Math.Max(Math.Abs(block.End.B - block.Start.B), Math.Abs(block.End.C - block.Start.C)));
        if (block.Motion == MotionKind.Rapid && block.CycleMotion is null)
            return Math.Clamp(Math.Max(linearDistance / 6000.0 * 60.0, rotaryDistance / 90.0), 0.03, 1.0);
        var feed = block.Feed > 0 ? block.Feed : 1000;
        // A helix is a single NC block but a long real move; it gets a wider
        // ceiling so the revolutions are actually visible.
        var ceiling = block.Path is { Revolutions: > 0 } ? 15.0 : 5.0;
        var motionSeconds = Math.Max(linearDistance / feed * 60.0, rotaryDistance / 30.0);
        var dwellSeconds = block.CycleMotion?.DwellSeconds ?? 0;
        return Math.Clamp(motionSeconds + dwellSeconds, 0.03, ceiling + dwellSeconds);
    }

    internal static double CyclePhaseDuration(CycleMotionPhase phase)
    {
        if (phase.Kind == CycleMotionPhaseKind.Dwell) return Math.Max(0, phase.DwellSeconds);
        var distance = Math.Sqrt(
            Math.Pow(phase.End.X - phase.Start.X, 2) +
            Math.Pow(phase.End.Y - phase.Start.Y, 2) +
            Math.Pow(phase.End.Z - phase.Start.Z, 2));
        var phaseFeed = phase.Feed > 0 ? phase.Feed : 6000.0;
        return distance / phaseFeed * 60.0;
    }
}
