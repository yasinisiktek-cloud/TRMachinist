using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    /// <summary>
    /// Commits the already completed samples of a cycle's original feed phase.
    /// Sample positions are derived from the WHOLE phase, never from the UI
    /// slice, so fast/slow playback gives exactly the same final interval stock.
    /// </summary>
    public TripleDexelCutResult ApplyLayeredCutterMoveProgress(
        Vector3 startTip, Vector3 endTip, Vector3 startAxis, Vector3 endAxis,
        IReadOnlyList<CutterCylinderLayer> layers, double fromProgress, double toProgress,
        int cutTag = 0, bool calculateRemainingVolume = true)
    {
        ArgumentNullException.ThrowIfNull(layers);
        fromProgress = Math.Clamp(fromProgress, 0, 1); toProgress = Math.Clamp(toProgress, 0, 1);
        TripleDexelCutResult Result(TripleDexelVolume volume) =>
            new(volume, calculateRemainingVolume ? Volume() : new(), false);
        if (toProgress <= fromProgress + 1e-12) return Result(new());
        if (fromProgress == 0 && toProgress == 1)
            return ApplyLayeredCutterMove(startTip, endTip, startAxis, endAxis, layers, false, cutTag, calculateRemainingVolume, continuousTranslation: false);
        var validLayers = layers.Where(layer => layer.IsValid).ToArray();
        if (validLayers.Length == 0) return Result(new());
        if (startAxis.LengthSquared() <= 1e-12f || endAxis.LengthSquared() <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(startAxis));
        var a = Vector3.Normalize(startAxis); var b = Vector3.Normalize(endAxis);
        var dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        // A non-drilling lateral phase may use the existing analytic sweep.
        // Retain that exact path rather than replacing it with sampled stamps.
        if ((validLayers.All(layer => !layer.IsTapered) || IsContinuousIncreasingProfile(validLayers)) && dot >= 0.999999f && Math.Abs(Vector3.Dot(endTip - startTip, a)) <= 1e-6 &&
            TryPrincipalAxis(a, out _))
            return toProgress >= 1 - 1e-9
                ? ApplyLayeredCutterMove(startTip, endTip, startAxis, endAxis, layers, false, cutTag, calculateRemainingVolume)
                : Result(new());

        // This is the original arbitrary-pose sampler's grid and float order.
        // Regression compares complete phase and several sliced schedules.
        var reach = validLayers.Max(layer => Math.Max(Math.Abs(layer.AxialOffset), Math.Abs(layer.AxialOffset + layer.Length)));
        var step = validLayers.Min(layer => layer.MotionStep(TargetPitch));
        var count = Math.Clamp((int)Math.Ceiling(Math.Max(Vector3.Distance(startTip, endTip), reach * Math.Acos(dot)) / step), 1, 256);
        var removed = new TripleDexelVolume();
        for (var i = 0; i <= count; i++)
        {
            var completion = i / (double)count;
            if (i == 0 ? fromProgress > 1e-9 : completion <= fromProgress + 1e-9) continue;
            if (completion > toProgress + 1e-9) break;
            var t = (float)i / count;
            var tip = Vector3.Lerp(startTip, endTip, t);
            var axis = Vector3.Lerp(a, b, t);
            axis = axis.LengthSquared() <= 1e-12f ? a : Vector3.Normalize(axis);
            var delta = SubtractFiniteLayeredCylinders(tip, axis, validLayers, cutTag);
            removed = new(removed.X + delta.X, removed.Y + delta.Y, removed.Z + delta.Z);
        }
        return Result(removed);
    }
}
