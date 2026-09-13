using System.Numerics;

namespace TRMachinist.Core;

public readonly record struct StockMotionPose(Vector3 Tip, Vector3 Axis);

public readonly record struct StockMotionSegment(
    double StartProgress,
    double EndProgress,
    AxisState Start,
    AxisState End);

/// <summary>
/// Produces one geometry-derived set of stock motion segments for an NC block.
/// Playback speed may consume one or many of these segments per UI frame, but
/// it cannot alter their positions or omit one. Stock therefore remains
/// invariant under frame rate and speed-factor changes.
/// </summary>
public static class DeterministicStockMotion
{
    public static IReadOnlyList<StockMotionSegment> Build(
        GCodeBlock block,
        Func<AxisState, StockMotionPose> poseResolver,
        double linearStep,
        double orientationReach)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(poseResolver);
        if (!double.IsFinite(linearStep) || linearStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(linearStep));
        if (!double.IsFinite(orientationReach) || orientationReach < 0)
            throw new ArgumentOutOfRangeException(nameof(orientationReach));

        var baseStates = new List<AxisState> { block.Start };
        if (block.Path is { Samples.Count: > 0 } path)
            baseStates.AddRange(path.Samples);
        else
            baseStates.Add(block.End);

        if (baseStates.Count < 2) return Array.Empty<StockMotionSegment>();
        var baseSegmentCount = baseStates.Count - 1;
        var segments = new List<StockMotionSegment>(baseSegmentCount);
        for (var baseIndex = 0; baseIndex < baseSegmentCount; baseIndex++)
        {
            var from = baseStates[baseIndex];
            var to = baseStates[baseIndex + 1];
            var fromPose = poseResolver(from);
            var toPose = poseResolver(to);
            var fromAxis = NormalizeAxis(fromPose.Axis);
            var toAxis = NormalizeAxis(toPose.Axis);
            var orientationTravel = orientationReach * Math.Acos(
                Math.Clamp(Vector3.Dot(fromAxis, toAxis), -1f, 1f));
            var linearTravel = Vector3.Distance(fromPose.Tip, toPose.Tip);
            var stepCount = Math.Clamp(
                (int)Math.Ceiling(Math.Max(linearTravel, orientationTravel) / linearStep),
                1,
                256);

            for (var step = 0; step < stepCount; step++)
            {
                var localStart = (double)step / stepCount;
                var localEnd = (double)(step + 1) / stepCount;
                segments.Add(new StockMotionSegment(
                    (baseIndex + localStart) / baseSegmentCount,
                    (baseIndex + localEnd) / baseSegmentCount,
                    AxisState.Lerp(from, to, localStart),
                    AxisState.Lerp(from, to, localEnd)));
            }
        }
        return segments;
    }

    public static IEnumerable<StockMotionSegment> SelectCompleted(
        IReadOnlyList<StockMotionSegment> segments,
        double fromProgress,
        double toProgress)
    {
        ArgumentNullException.ThrowIfNull(segments);
        fromProgress = Math.Clamp(fromProgress, 0, 1);
        toProgress = Math.Clamp(toProgress, 0, 1);
        const double epsilon = 1e-9;
        foreach (var segment in segments)
            if (segment.EndProgress > fromProgress + epsilon &&
                segment.EndProgress <= toProgress + epsilon)
                yield return segment;
    }

    private static Vector3 NormalizeAxis(Vector3 axis) =>
        axis.LengthSquared() <= 1e-12f ? Vector3.UnitZ : Vector3.Normalize(axis);
}
