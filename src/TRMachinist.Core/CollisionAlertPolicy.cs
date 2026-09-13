namespace TRMachinist.Core;

public static class CollisionAlertPolicy
{
    public static bool ShouldStopPlayback(IEnumerable<bool> clearanceOnlyHits)
    {
        ArgumentNullException.ThrowIfNull(clearanceOnlyHits);
        return clearanceOnlyHits.Any(isClearanceOnly => !isClearanceOnly);
    }
}
