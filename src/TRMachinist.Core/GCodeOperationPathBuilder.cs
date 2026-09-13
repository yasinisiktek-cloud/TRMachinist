using System.Numerics;

namespace TRMachinist.Core;

/// <summary>
/// Planned, operation-scoped path data for an NX-style preview. This is not a
/// playback trace: it is rebuilt from the selected operation's NC interval and
/// expressed in workpiece-local coordinates so it stays attached to the part.
/// </summary>
public sealed record GCodeOperationPathPreview(
    float[] Positions,
    int[] CuttingIndices,
    int[] LinkingIndices)
{
    public GCodePathTiming[] CuttingTimings { get; init; } = Array.Empty<GCodePathTiming>();
    public GCodePathTiming[] LinkingTimings { get; init; } = Array.Empty<GCodePathTiming>();
    public int CuttingSegmentCount => CuttingIndices.Length / 2;
    public int LinkingSegmentCount => LinkingIndices.Length / 2;
}

public static class GCodeOperationPathBuilder
{
    private readonly record struct Segment(Vector3 Start, Vector3 End, GCodePathTiming Timing);

    public static GCodeOperationPathPreview Build(
        GCodeProgram program,
        IReadOnlyDictionary<int, double> toolGaugeLengths,
        IReadOnlyList<GCodeOperationRange> operations,
        Func<AxisState, double, Vector3> toWorkpieceLocalTip,
        bool includeLocalLinks = false)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(toolGaugeLengths);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(toWorkpieceLocalTip);

        var cuttingSegments = new List<Segment>();
        var rapidCandidates = includeLocalLinks ? new List<Segment>() : null;
        foreach (var operation in operations.OrderBy(o => o.StartBlockIndex))
        {
            for (var blockIndex = operation.StartBlockIndex;
                 blockIndex <= operation.EndBlockIndex && blockIndex < program.Blocks.Count;
                 blockIndex++)
            {
                var block = program.Blocks[blockIndex];
                if (block.ExecutionBlocked) continue;
                var gauge = block.Tool is int tool && toolGaugeLengths.TryGetValue(tool, out var value)
                    ? value
                    : 0.0;
                if (block.CycleMotion is { Phases.Count: > 0 } cycle)
                {
                    var duration = cycle.Phases.Sum(SimulationPlayer.CyclePhaseDuration);
                    var elapsed = 0.0;
                    foreach (var phase in cycle.Phases)
                    {
                        var startProgress = duration > 0 ? elapsed / duration : 0;
                        elapsed += SimulationPlayer.CyclePhaseDuration(phase);
                        var endProgress = duration > 0 ? elapsed / duration : 1;
                        if (phase.Kind == CycleMotionPhaseKind.Dwell) continue;
                        var rapid = phase.Kind is
                            CycleMotionPhaseKind.RapidPosition or
                            CycleMotionPhaseKind.RapidApproach or
                            CycleMotionPhaseKind.PeckRetract or
                            CycleMotionPhaseKind.RapidRetract or
                            CycleMotionPhaseKind.RapidReturnToProgrammedLevel;
                        Add(phase.Start, phase.End, gauge, rapid, false, new(blockIndex, startProgress, endProgress));
                    }
                    continue;
                }

                if (!block.HasMotion) continue;
                var from = block.Start;
                if (block.Path is { Samples.Count: > 0 } path)
                {
                    var sampleIndex = 0;
                    foreach (var to in path.Samples)
                    {
                        Add(
                            from,
                            to,
                            gauge,
                            block.Motion == MotionKind.Rapid,
                            IsControllerReferenceMove(block.Raw),
                            new(blockIndex, sampleIndex / (double)path.Samples.Count, (sampleIndex + 1) / (double)path.Samples.Count));
                        sampleIndex++;
                        from = to;
                    }
                }
                else
                {
                    Add(
                        from,
                        block.End,
                        gauge,
                        block.Motion == MotionKind.Rapid,
                        IsControllerReferenceMove(block.Raw), new(blockIndex, 0, 1));
                }
            }
        }

        var linkingSegments = includeLocalLinks
            ? KeepOnlyOperationLocalLinks(cuttingSegments, rapidCandidates!)
            : Array.Empty<Segment>();
        var positions = new List<float>((cuttingSegments.Count + linkingSegments.Count) * 6);
        var cutting = new List<int>(cuttingSegments.Count * 2);
        var linking = new List<int>(linkingSegments.Count * 2);
        Append(cuttingSegments, cutting);
        Append(linkingSegments, linking);
        return new GCodeOperationPathPreview(positions.ToArray(), cutting.ToArray(), linking.ToArray())
        {
            CuttingTimings = cuttingSegments.Select(s => s.Timing).ToArray(),
            LinkingTimings = linkingSegments.Select(s => s.Timing).ToArray()
        };

        void Add(AxisState from, AxisState to, double gauge, bool rapid, bool controllerReference, GCodePathTiming timing)
        {
            var start = toWorkpieceLocalTip(from, gauge);
            var end = toWorkpieceLocalTip(to, gauge);
            if (Vector3.DistanceSquared(start, end) <= 1e-10f) return;
            var segment = new Segment(start, end, timing);
            if (rapid)
            {
                if (!controllerReference)
                    rapidCandidates?.Add(segment);
                return;
            }
            cuttingSegments.Add(segment);
        }

        void Append(IEnumerable<Segment> source, List<int> indices)
        {
            foreach (var segment in source)
            {
                var startIndex = positions.Count / 3;
                positions.Add(segment.Start.X);
                positions.Add(segment.Start.Y);
                positions.Add(segment.Start.Z);
                positions.Add(segment.End.X);
                positions.Add(segment.End.Y);
                positions.Add(segment.End.Z);
                indices.Add(startIndex);
                indices.Add(startIndex + 1);
            }
        }
    }

    /// <summary>
    /// Controller reference/tool-change rapids (for example SUPA Z605) are not
    /// CAM operation links. Keep only short rapids whose both ends remain near
    /// the cutting envelope. This makes the optional link display equivalent
    /// to an operation preview rather than a whole-machine movement trace.
    /// </summary>
    private static IReadOnlyList<Segment> KeepOnlyOperationLocalLinks(
        IReadOnlyList<Segment> cutting,
        IReadOnlyList<Segment> candidates)
    {
        if (cutting.Count == 0 || candidates.Count == 0) return Array.Empty<Segment>();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var segment in cutting)
        {
            minimum = Vector3.Min(minimum, Vector3.Min(segment.Start, segment.End));
            maximum = Vector3.Max(maximum, Vector3.Max(segment.Start, segment.End));
        }

        var diagonal = Math.Max(1f, Vector3.Distance(minimum, maximum));
        var padding = Math.Clamp(diagonal * 1.25f, 15f, 80f);
        var maximumLength = Math.Clamp(diagonal * 2.0f, 30f, 150f);
        return candidates.Where(segment =>
                Vector3.Distance(segment.Start, segment.End) <= maximumLength &&
                DistanceToBounds(segment.Start, minimum, maximum) <= padding &&
                DistanceToBounds(segment.End, minimum, maximum) <= padding)
            .ToArray();
    }

    private static float DistanceToBounds(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        var nearest = Vector3.Clamp(point, minimum, maximum);
        return Vector3.Distance(point, nearest);
    }

    private static bool IsControllerReferenceMove(string raw)
    {
        var text = raw.ToUpperInvariant();
        return ContainsWord(text, "SUPA") ||
               ContainsWord(text, "G28") ||
               ContainsWord(text, "G30") ||
               ContainsWord(text, "G53");
    }

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var end = start + word.Length;
            var after = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            start = end;
        }
        return false;
    }
}
