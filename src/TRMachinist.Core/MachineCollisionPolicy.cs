namespace TRMachinist.Core;

/// <summary>
/// Selects the machine bodies that belong to the machining kinematic chain
/// when a TRMAC does not provide an authoritative collision-pair matrix.
/// Service mechanisms (doors, magazines and tool changers) may be animated,
/// but their non-NC auxiliary axes must not become all-to-all stop alarms.
/// </summary>
public static class MachineCollisionPolicy
{
    public static bool ShouldMonitorMachineComponent(
        string componentName,
        string componentRole,
        IReadOnlySet<string> declaredCollidableComponents,
        IReadOnlyList<AxisDefinition> axes)
    {
        if (!declaredCollidableComponents.Contains(componentName)) return false;
        if (componentRole.Contains("auxiliary", StringComparison.OrdinalIgnoreCase)) return false;

        for (var index = 0; index < axes.Count; index++)
        {
            var axis = axes[index];
            if (!axis.Component.Equals(componentName, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsMachiningAxisType(axis.Type)) return true;
        }

        return false;
    }

    public static bool IsMachiningAxisType(string axisType) =>
        axisType.Contains("NcAxis", StringComparison.OrdinalIgnoreCase) ||
        axisType.Equals("Spindle", StringComparison.OrdinalIgnoreCase);
}
