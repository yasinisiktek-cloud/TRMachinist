namespace TRMachinist.Core;

public readonly record struct GCodePathTiming(int BlockIndex, double StartProgress, double EndProgress);

/// <summary>A read-only prefix of planned geometry at the committed playback cursor.</summary>
public readonly record struct GCodePathPrefix(int CompletedSegments, double PartialProgress)
{
    public static GCodePathPrefix At(IReadOnlyList<GCodePathTiming> segments, int blockIndex, double progress)
    {
        var low = 0;
        var high = segments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var segment = segments[middle];
            if (segment.BlockIndex < blockIndex || segment.BlockIndex == blockIndex && segment.EndProgress <= progress)
                low = middle + 1;
            else high = middle;
        }
        if (low >= segments.Count) return new(low, 0);
        var current = segments[low];
        var fraction = current.BlockIndex == blockIndex && current.EndProgress > current.StartProgress
            ? Math.Clamp((progress - current.StartProgress) / (current.EndProgress - current.StartProgress), 0, 1)
            : 0;
        return new(low, fraction);
    }
}
