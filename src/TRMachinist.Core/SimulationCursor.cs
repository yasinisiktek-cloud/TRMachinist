namespace TRMachinist.Core;

/// <summary>
/// Canonical NC/IPW progress cursor. A completed block and the beginning of
/// the next block describe the same stock state and therefore normalize to one
/// representation before rewind decisions are made.
/// </summary>
public readonly record struct SimulationCursor(int BlockIndex, double Progress)
{
    public static int Compare(SimulationCursor left, SimulationCursor right)
    {
        var block = left.BlockIndex.CompareTo(right.BlockIndex);
        return block != 0 ? block : left.Progress.CompareTo(right.Progress);
    }

    public SimulationCursor Clamp(int blockCount)
    {
        if (blockCount <= 0) return new SimulationCursor(0, 0);
        return new SimulationCursor(
            Math.Clamp(BlockIndex, 0, blockCount - 1),
            Math.Clamp(Progress, 0, 1));
    }

    public SimulationCursor Normalize(int blockCount)
    {
        var cursor = Clamp(blockCount);
        while (cursor.Progress >= 1 - 1e-9 && cursor.BlockIndex < blockCount - 1)
            cursor = new SimulationCursor(cursor.BlockIndex + 1, 0);
        return cursor;
    }
}
