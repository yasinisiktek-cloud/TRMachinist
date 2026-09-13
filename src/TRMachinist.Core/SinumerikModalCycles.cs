using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace TRMachinist.Core;

public sealed record CycleDefinition(
    string Name,
    int DwellParameter,
    int PeckParameter,
    int InFeedParameter,
    int OutFeedParameter,
    bool ControlledRetract,
    int PitchParameter = -1);

public sealed record CycleInvocation(
    CycleDefinition Definition,
    IReadOnlyList<double?> Parameters,
    char PlaneNormal,
    int SourceLine);

public sealed record ModalCycleState(CycleInvocation? Active)
{
    public static ModalCycleState Empty { get; } = new((CycleInvocation?)null);
}

internal static class SinumerikModalCycles
{
    private static readonly IReadOnlyDictionary<string, CycleDefinition> Definitions =
        new Dictionary<string, CycleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["CYCLE81"] = new("CYCLE81", -1, -1, -1, -1, false),
            ["CYCLE82"] = new("CYCLE82", 5, -1, -1, -1, false),
            // CYCLE83: FDEP/FDPR are parameters 6/7, while DTB is parameter 9.
            ["CYCLE83"] = new("CYCLE83", 8, 6, -1, -1, false),
            // CYCLE84 feed is synchronized from spindle speed and PIT (parameter 9),
            // not from SST/SST1 (parameters 11/12, which are spindle speeds).
            ["CYCLE84"] = new("CYCLE84", 5, -1, -1, -1, true, 8),
            ["CYCLE85"] = new("CYCLE85", 5, -1, 6, 7, true),
            ["CYCLE89"] = new("CYCLE89", 5, -1, -1, -1, false)
        };

    private static readonly Regex CycleRegex = new(
        @"(?<![A-Z0-9_])(?<name>CYCLE81|CYCLE82|CYCLE83|CYCLE84|CYCLE85|CYCLE89)\s*\((?<args>[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsMcall(string code) =>
        Regex.IsMatch(code, @"(?<![A-Z0-9_])MCALL(?![A-Z0-9_])", RegexOptions.CultureInvariant);

    internal static CycleInvocation? Parse(
        string code,
        char planeNormal,
        int sourceLine,
        IReadOnlyDictionary<string, double> variables)
    {
        var match = CycleRegex.Match(code);
        if (!match.Success || !Definitions.TryGetValue(match.Groups["name"].Value, out var definition)) return null;
        var values = match.Groups["args"].Value.Split(',', StringSplitOptions.None)
            .Select(token => Evaluate(token.Trim(), variables)).ToArray();
        return new CycleInvocation(definition, values, planeNormal, sourceLine);
    }

    internal static bool HasPlanePosition(string code, char normal)
    {
        var axes = normal switch { 'X' => "YZ", 'Y' => "XZ", _ => "XY" };
        return axes.Any(axis => Regex.IsMatch(code, $@"(?<![A-Z0-9_]){axis}\s*(?:=|[+\-.0-9])", RegexOptions.CultureInvariant));
    }

    internal static (MotionPath Path, GeneratedCycleMotion Motion, AxisState End) Generate(
        CycleInvocation invocation,
        int triggerSourceLine,
        AxisState blockStart,
        AxisState positioned,
        double modalFeed,
        double spindle,
        Func<Vector3, AxisState> mapWorkPoint,
        Vector3 positionedWork)
    {
        var p = invocation.Parameters;
        var rtp = Value(p, 0, Get(positionedWork, invocation.PlaneNormal));
        var rfp = Value(p, 1, rtp);
        var sdis = Math.Abs(Value(p, 2, 0));
        var dp = p.ElementAtOrDefault(3);
        var dpr = p.ElementAtOrDefault(4);
        var direction = dp.HasValue
            ? Math.Sign(dp.Value - rfp)
            : dpr.HasValue && Math.Abs(dpr.Value) > 1e-12 ? -Math.Sign(dpr.Value) : -1;
        if (direction == 0) direction = -1;
        var depth = dp ?? (rfp + (direction * Math.Abs(dpr ?? 0)));
        var safety = rfp - (direction * sdis);
        var dwell = invocation.Definition.DwellParameter >= 0
            ? DwellSeconds(Value(p, invocation.Definition.DwellParameter, 0), spindle)
            : 0;
        var feedIn = Positive(Value(p, invocation.Definition.InFeedParameter, modalFeed), modalFeed);
        var feedOut = Positive(Value(p, invocation.Definition.OutFeedParameter, feedIn), feedIn);
        if (invocation.Definition.PitchParameter >= 0)
        {
            var pitch = Math.Abs(Value(p, invocation.Definition.PitchParameter, 0));
            if (pitch > 1e-12 && Math.Abs(spindle) > 1e-12)
                feedIn = feedOut = Math.Abs(spindle) * pitch;
        }

        var samples = new List<AxisState>();
        var phases = new List<CycleMotionPhase>();
        var previousState = blockStart;
        var length = 0.0;

        AxisState AddState(AxisState next, CycleMotionPhaseKind kind, double phaseFeed)
        {
            length += Distance(previousState, next);
            phases.Add(new CycleMotionPhase(kind, previousState, next, phaseFeed));
            samples.Add(next);
            previousState = next;
            return next;
        }

        AxisState Add(double normalValue, CycleMotionPhaseKind kind, double phaseFeed)
        {
            var work = With(positionedWork, invocation.PlaneNormal, normalValue);
            return AddState(mapWorkPoint(work), kind, phaseFeed);
        }

        void Pause(double seconds)
        {
            if (seconds > 0)
                phases.Add(new CycleMotionPhase(CycleMotionPhaseKind.Dwell, previousState, previousState, 0, seconds));
        }

        // Keep the plane-positioning move separate from the tool-axis approach.
        // Combining them would draw a false diagonal through the workpiece.
        AddState(positioned, CycleMotionPhaseKind.RapidPosition, 0);
        Add(safety, CycleMotionPhaseKind.RapidApproach, 0);
        if (invocation.Definition.Name.Equals("CYCLE83", StringComparison.OrdinalIgnoreCase))
        {
            // Siemens CYCLE83 signature:
            // RTP,RFP,SDIS,DP,DPR,FDEP,FDPR,DAM,DTB,DTS,FRF,VARI,_AXN,_MDEP,_VRT,_DTD,_DIS1
            var totalDepth = Math.Abs(depth - rfp);
            var firstDepth = p.ElementAtOrDefault(5)
                ?? (p.ElementAtOrDefault(6).HasValue
                    ? rfp + (direction * Math.Abs(p.ElementAtOrDefault(6)!.Value))
                    : depth);
            var step = Math.Min(Math.Abs(firstDepth - rfp), totalDepth);
            if (step < 1e-9) step = totalDepth;
            var degression = Value(p, 7, 0);
            var peckDwell = DwellSeconds(Value(p, 8, 0), spindle);
            var startDwell = DwellSeconds(Value(p, 9, 0), spindle);
            var firstFeedFactor = Math.Clamp(Positive(Value(p, 10, 1), 1), 0.001, 1);
            var chipRemoval = (int)Math.Round(Value(p, 11, 0)) == 1;
            var minimumStep = Math.Abs(Value(p, 13, 0));
            var chipBreakRetract = Math.Abs(Value(p, 14, 1));
            if (chipBreakRetract < 1e-9) chipBreakRetract = 1;
            var finalDwellValue = p.ElementAtOrDefault(15);
            var finalDwell = !finalDwellValue.HasValue || Math.Abs(finalDwellValue.Value) < 1e-12
                ? peckDwell
                : DwellSeconds(finalDwellValue.Value, spindle);
            var reentryClearance = Math.Abs(Value(p, 16, 1));
            if (reentryClearance < 1e-9) reentryClearance = 1;
            var current = rfp;
            var peckIndex = 0;
            while (Math.Abs(depth - current) > 1e-9 && peckIndex++ < 10000)
            {
                var next = current + (direction * Math.Min(step, Math.Abs(depth - current)));
                var isFinal = Math.Abs(next - depth) <= 1e-9;
                Add(next, CycleMotionPhaseKind.FeedIn, peckIndex == 1 ? feedIn * firstFeedFactor : feedIn);
                Pause(isFinal ? finalDwell : peckDwell);
                if (!isFinal)
                {
                    if (chipRemoval)
                    {
                        Add(safety, CycleMotionPhaseKind.PeckRetract, 0);
                        Pause(startDwell);
                        var reentry = next - (direction * Math.Min(reentryClearance, Math.Abs(next - rfp)));
                        Add(reentry, CycleMotionPhaseKind.RapidApproach, 0);
                    }
                    else
                    {
                        var retract = next - (direction * Math.Min(chipBreakRetract, Math.Abs(next - rfp)));
                        Add(retract, CycleMotionPhaseKind.PeckRetract, 0);
                    }
                }
                current = next;

                if (degression > 0)
                    step = Math.Max(minimumStep > 0 ? minimumStep : 0.001, step - degression);
                else if (degression < 0)
                    step = Math.Max(minimumStep > 0 ? minimumStep : 0.001, step * Math.Abs(degression));
            }
        }
        else
        {
            Add(depth, CycleMotionPhaseKind.FeedIn, feedIn);
            Pause(dwell);
        }

        Add(rtp,
            invocation.Definition.ControlledRetract ? CycleMotionPhaseKind.FeedRetract : CycleMotionPhaseKind.RapidRetract,
            invocation.Definition.ControlledRetract ? feedOut : 0);

        // A generated cycle is a visual sub-motion of the plane-position block;
        // it must not replace the parser's already verified modal endpoint.
        // Test-22-r2 used `positioned` as the next block's start.  Returning the
        // sub-motion to that programmed level keeps every later non-cycle and
        // TRAORI block byte-for-byte compatible while still showing the complete
        // approach/feed/dwell/retract profile inside this block.
        if (Distance(previousState, positioned) > 1e-9)
            AddState(positioned, CycleMotionPhaseKind.RapidReturnToProgrammedLevel, 0);

        var totalDwell = phases.Where(x => x.Kind == CycleMotionPhaseKind.Dwell).Sum(x => x.DwellSeconds);
        var motion = new GeneratedCycleMotion(
            invocation.Definition.Name, triggerSourceLine, invocation.SourceLine, phases, totalDwell);
        return (new MotionPath(samples, length, 0), motion, previousState);
    }

    private static double? Evaluate(string token, IReadOnlyDictionary<string, double> variables)
    {
        if (token.Length == 0) return null;
        if (variables.TryGetValue(token, out var variable)) return variable;
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double Value(IReadOnlyList<double?> values, int index, double fallback) =>
        index >= 0 && index < values.Count && values[index].HasValue ? values[index]!.Value : fallback;

    private static double Positive(double value, double fallback) => value > 0 ? value : fallback > 0 ? fallback : 1000;
    private static double DwellSeconds(double value, double spindle)
    {
        if (value >= 0) return value;
        return Math.Abs(spindle) > 1e-12 ? Math.Abs(value) / Math.Abs(spindle) * 60.0 : 0;
    }
    private static double Get(Vector3 point, char axis) => axis switch { 'X' => point.X, 'Y' => point.Y, _ => point.Z };
    private static Vector3 With(Vector3 point, char axis, double value) => axis switch
    {
        'X' => point with { X = (float)value },
        'Y' => point with { Y = (float)value },
        _ => point with { Z = (float)value }
    };
    private static double Distance(AxisState a, AxisState b) => Math.Sqrt(
        Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
}
