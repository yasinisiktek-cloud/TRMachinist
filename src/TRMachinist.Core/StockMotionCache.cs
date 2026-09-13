namespace TRMachinist.Core;

/// <summary>
/// Reusable segmentation, never stock history. Retention is bounded by both
/// blocks and segments; a single unusually large active block may exceed the
/// segment budget, but cannot accumulate with any previous block.
/// </summary>
public sealed class StockMotionCache
{
    private const int MaximumBlocks = 512;
    private const int MaximumSegments = 32_768;
    private readonly Dictionary<GCodeBlock, IReadOnlyList<StockMotionSegment>> _entries = new();
    private readonly Queue<GCodeBlock> _order = new();
    public int Count => _entries.Count;
    public int SegmentCount { get; private set; }
    public bool TryGetValue(GCodeBlock block, out IReadOnlyList<StockMotionSegment> segments) =>
        _entries.TryGetValue(block, out segments!);
    public IReadOnlyList<StockMotionSegment> this[GCodeBlock block]
    {
        set
        {
            if (_entries.TryGetValue(block, out var previous))
                SegmentCount -= previous.Count;
            else _order.Enqueue(block);
            _entries[block] = value;
            SegmentCount += value.Count;
            while (_entries.Count > 1 && (Count > MaximumBlocks || SegmentCount > MaximumSegments))
            {
                var oldest = _order.Dequeue();
                if (_entries.Remove(oldest, out var removed)) SegmentCount -= removed.Count;
            }
        }
    }
    public void Clear()
    {
        _entries.Clear(); _order.Clear(); SegmentCount = 0;
    }
}
