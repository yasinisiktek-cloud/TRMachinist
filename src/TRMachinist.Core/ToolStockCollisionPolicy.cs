namespace TRMachinist.Core;

/// <summary>
/// Defines which workpiece representation is authoritative for non-cutting
/// tool collision checks.  A static blank/part mesh has no material-removal
/// history, so it cannot prove a shank/holder collision after playback starts.
/// Live IPW intervals can prove penetration, but a boundary touch remains a
/// warning until it exceeds the stock-resolution tolerance.
/// </summary>
public static class ToolStockCollisionPolicy
{
    public static bool IsStaticReferencePair(string firstKind, string secondKind) =>
        IsTool(firstKind) && IsReferenceWorkpiece(secondKind) ||
        IsTool(secondKind) && IsReferenceWorkpiece(firstKind);

    public static double MinimumLivePenetrationMm(string regionKind, double stockPitchMm)
    {
        if (!double.IsFinite(stockPitchMm) || stockPitchMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(stockPitchMm));

        return regionKind.Equals("shank", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(ToolCollisionGeometry.AxialContactToleranceMm, stockPitchMm * 0.5)
            : Math.Max(ToolCollisionGeometry.AxialContactToleranceMm, stockPitchMm / 6.0);
    }

    private static bool IsTool(string kind) =>
        kind.StartsWith("tool-", StringComparison.OrdinalIgnoreCase);

    private static bool IsReferenceWorkpiece(string kind) =>
        kind is "stock" or "part";
}
