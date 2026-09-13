using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace TRMachinist.Core;

public static partial class GCodeParser
{
    private enum CutterCompensation { Off, Left, Right }

    [GeneratedRegex(@"(?<letter>[A-Z])\s*(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?<![A-Z0-9_])(?<letter>[XYZABC])\s*(?:=\s*(?:(?<mode>DC|ACP|ACN)\s*\(\s*)?(?<expression>_[A-Z][A-Z0-9_]*|[+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*\)?|(?<number>[+-]?(?:\d+(?:\.\d*)?|\.\d+)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AxisRegex();

    [GeneratedRegex(@"(?<![A-Z0-9_])(?<name>_[A-Z][A-Z0-9_]*)\s*=\s*(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariableAssignmentRegex();

    [GeneratedRegex(@"(?<![A-Z0-9_])CYCLE800\s*\((?<args>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Cycle800Regex();

    /// <summary>
    /// SINUMERIK tool calls by name: T="END_MILL_10". Some posts emit a tool
    /// name instead of a T number; the name is resolved
    /// against the job package tool list.  This has to run on the raw block
    /// text because quoted strings are stripped out of <c>code</c>.
    /// </summary>
    [GeneratedRegex("(?<![A-Z0-9_])T\\s*=\\s*\"(?<name>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedToolRegex();

    [GeneratedRegex(@"(?<![A-Z0-9_])T\s*=\s*(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignedToolNumberRegex();

    /// <summary>SINUMERIK helix: TURN=&lt;n&gt; adds n extra full revolutions to the programmed G2/G3 arc.</summary>
    [GeneratedRegex(@"(?<![A-Z0-9_])TURN\s*=\s*(?<turns>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurnRegex();

    /// <summary>SINUMERIK arc by radius: CR=&lt;r&gt;, negative selects the arc larger than a half circle.</summary>
    [GeneratedRegex(@"(?<![A-Z0-9_])CR\s*=\s*(?<radius>[+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcRadiusRegex();

    public static GCodeProgram Load(string path, MachinePackage machine, GCodeSimulationContext? context = null)
    {
        var source = Path.GetFullPath(path);
        if (!File.Exists(source)) throw new FileNotFoundException("NC dosyası bulunamadı.", source);
        return Parse(source, File.ReadAllLines(source), machine, context);
    }

    public static GCodeProgram Parse(
        string sourceName,
        IEnumerable<string> sourceLines,
        MachinePackage machine,
        GCodeSimulationContext? context = null)
    {
        var axisDefinitions = machine.Axes
            .Where(x => x.Name.Length > 0 && "XYZABC".Contains(char.ToUpperInvariant(x.Name[0])))
            .GroupBy(x => char.ToUpperInvariant(x.Name[0]))
            .ToDictionary(x => x.Key, x => x.First());

        var state = new AxisState(
            Initial('X'), Initial('Y'), Initial('Z'), Initial('A'), Initial('B'), Initial('C'));
        var absolute = true;
        var unitScale = 1.0;
        var modalMotion = MotionKind.Rapid;
        var feed = 0.0;
        var spindle = 0.0;
        int? pendingTool = null;
        int? activeTool = null;
        var variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var workCoordinates = new Dictionary<char, double>();
        var workAnchors = new Dictionary<char, (double Programmed, double Machine)>();
        var emittedWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<GCodeBlock>();
        var displayLines = new List<GCodeSourceLine>();
        var lineNumber = 0;
        var activeWorkFrame = context?.WorkFrame;
        var traoriActive = false;
        var modalCycle = ModalCycleState.Empty;
        var cutterCompensation = CutterCompensation.Off;
        Vector3? lastProgrammedWorkPoint = null;
        // G17/G18/G19 select the interpolation plane; the value stored is the
        // plane normal axis, which is what the arc sampler needs.
        var arcPlaneNormal = 'Z';
        var fanuc = FanucModalState.Applies(machine) ? new FanucModalState() : null;
        var toolCoordinates = MachineToolCoordinates.For(machine);
        var unresolvedController = false;

        var inputLines = sourceLines as IReadOnlyList<string> ?? sourceLines.ToArray();
        foreach (var raw in inputLines)
        {
            lineNumber++;
            var text = Clean(fanuc is null ? raw : FanucModalState.WithoutComments(raw)).ToUpperInvariant();
            if (text.Length == 0)
            {
                displayLines.Add(new GCodeSourceLine(lineNumber, raw, null, null, null));
                continue;
            }
            var code = StripQuotedStrings(text);
            if (ContainsCommand(code, "TRAFOOF")) traoriActive = false;
            else if (ContainsCommand(code, "TRAORI")) traoriActive = true;

            foreach (Match assignment in VariableAssignmentRegex().Matches(code))
                variables[assignment.Groups["name"].Value] = ParseNumber(assignment.Groups["value"].Value);

            var words = WordRegex().Matches(code)
                .Select(match => (Letter: match.Groups["letter"].Value[0], Value: ParseNumber(match.Groups["value"].Value)))
                .ToList();

            var frameChanged = false;
            var previousCompensation = cutterCompensation;
            foreach (var word in words.Where(x => x.Letter == 'G'))
            {
                if (Math.Abs(word.Value - Math.Round(word.Value)) > 1e-6) continue;
                var g = (int)Math.Round(word.Value);
                switch (g)
                {
                    case 0: modalMotion = MotionKind.Rapid; break;
                    case 1: modalMotion = MotionKind.Linear; break;
                    case 2: modalMotion = MotionKind.CircularClockwise; break;
                    case 3: modalMotion = MotionKind.CircularCounterClockwise; break;
                    case 20:
                    case 700: unitScale = 25.4; break;
                    case 21:
                    case 710: unitScale = 1.0; break;
                    case 17: arcPlaneNormal = 'Z'; break;
                    case 18: arcPlaneNormal = 'Y'; break;
                    case 19: arcPlaneNormal = 'X'; break;
                    case 40: cutterCompensation = CutterCompensation.Off; break;
                    case 41: cutterCompensation = CutterCompensation.Left; break;
                    case 42: cutterCompensation = CutterCompensation.Right; break;
                    case 53: frameChanged = true; break;
                    case >= 54 and <= 59: frameChanged = true; break;
                    case 90: absolute = true; break;
                    case 91: absolute = false; break;
                }
            }

            var isMcall = SinumerikModalCycles.IsMcall(code);
            var declaredCycle = SinumerikModalCycles.Parse(code, arcPlaneNormal, lineNumber, variables);
            if (isMcall)
                modalCycle = new ModalCycleState(declaredCycle); // plain MCALL cancels
            var triggersModalCycle = !isMcall && modalCycle.Active is not null &&
                SinumerikModalCycles.HasPlanePosition(code, modalCycle.Active.PlaneNormal);

            var isSupa = ContainsCommand(code, "SUPA");
            var isMachineCoordinate = isSupa || ContainsGCode(words, 53);
            if (isSupa || frameChanged || ContainsAnyCommand(code, "TRANS", "ATRANS", "ROT", "AROT", "CYCLE800", "TRAORI", "TRAFOOF"))
            {
                workAnchors.Clear();
                if (context is not null && !isSupa) workCoordinates.Clear();
                lastProgrammedWorkPoint = null;
            }
            if (context is not null && frameChanged)
                activeWorkFrame = context.WorkFrame;

            foreach (var word in words)
            {
                if (word.Letter == 'F') feed = word.Value * unitScale;
                else if (word.Letter == 'S') spindle = word.Value;
                else if (word.Letter == 'T') pendingTool = Math.Max(0, (int)Math.Round(word.Value));
            }
            string? toolWarning = null;
            var namedTool = NamedToolRegex().Match(text);
            if (namedTool.Success)
            {
                var toolName = namedTool.Groups["name"].Value.Trim();
                if (context?.ToolNumbersByName is not null &&
                    context.ToolNumbersByName.TryGetValue(toolName, out var mappedNumber))
                {
                    pendingTool = mappedNumber;
                }
                else if (emittedWarnings.Add("TOOLNAME:" + toolName))
                {
                    toolWarning = "NC'de T=\"" + toolName + "\" ile cagrilan takim is paketinde yok; "
                        + "takim boyu ve govdesi bu program icin cozulemiyor.";
                }
            }
            else
            {
                var assignedTool = AssignedToolNumberRegex().Match(code);
                if (assignedTool.Success && int.TryParse(assignedTool.Groups["number"].Value, out var assignedNumber))
                    pendingTool = assignedNumber;
            }

            var isToolChange = words.Any(x => x.Letter == 'M' && Math.Abs(x.Value - 6) < 0.0001);
            if (isToolChange && pendingTool.HasValue)
                activeTool = pendingTool;

            fanuc?.Update(machine, words, isToolChange);
            fanuc?.Cycles.Update(words, lineNumber, unitScale, code);
            var isHome = fanuc is not null && ContainsGCode(words, 28);
            var blockMotion = isHome ? MotionKind.Rapid : modalMotion;
            var tcpActive = traoriActive || (fanuc?.Tcp == true && !isMachineCoordinate && !isHome && !isToolChange);
            if (fanuc is not null && (fanuc.Changed || isMachineCoordinate || isHome ||
                (fanuc.DynamicOffset && words.Any(w => w.Letter is 'B' or 'C'))))
            {
                workCoordinates.Clear();
                lastProgrammedWorkPoint = null;
            }
            string? gaugeError = null;
            var blockGauge = context is null ? 0 : fanuc is not null ? fanuc.Gauge(context, out gaugeError) :
                activeTool.HasValue && context.ToolGaugeLengths.TryGetValue(activeTool.Value, out var gl) ? gl : 0;
            var fanucStartFrame = context is null ? null : fanuc?.WorkFrame(machine, context.WorkFrame, state);
            Vector3 FanucStartWork()
            {
                var point = toolCoordinates.TipWorld(state, blockGauge);
                if (tcpActive) point = MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, point, state.B, state.C);
                var frame = tcpActive ? context!.WorkFrame : fanucStartFrame!;
                var delta = point - frame.Origin;
                return new Vector3(Vector3.Dot(delta, frame.XAxis), Vector3.Dot(delta, frame.YAxis), Vector3.Dot(delta, frame.ZAxis));
            }
            var incrementalOrigin = fanuc is not null && context is not null ? FanucStartWork() : Vector3.Zero;

            var start = state;
            var hasAxis = false;
            var hasContextWorkCoordinate = false;
            var machineAxesUpdated = new HashSet<char>();
            foreach (Match match in AxisRegex().Matches(code))
            {
                var axis = match.Groups["letter"].Value[0];
                var token = match.Groups["expression"].Success
                    ? match.Groups["expression"].Value
                    : match.Groups["number"].Value;
                var rotaryMode = match.Groups["mode"].Success
                    ? match.Groups["mode"].Value.ToUpperInvariant()
                    : string.Empty;
                if (!TryEvaluate(token, variables, out var parsed)) continue;

                hasAxis = true;
                var scale = "XYZ".Contains(axis) ? unitScale : 1.0;
                var requested = parsed * scale;

                if (isMachineCoordinate || (isHome && !absolute) || "ABC".Contains(axis))
                {
                    var target = absolute ? requested : state.Get(axis) + requested;
                    if (absolute && "ABC".Contains(axis) && rotaryMode.Length > 0)
                        target = ResolveRotaryTarget(state.Get(axis), requested, rotaryMode);
                    state = state.With(axis, target);
                    machineAxesUpdated.Add(axis);
                    if ("XYZ".Contains(axis)) workCoordinates.Remove(axis);
                    continue;
                }

                // Normal G54/G55/... XYZ words are work coordinates, not raw slide
                // coordinates. Anchor the first programmed value to the current machine
                // state and animate subsequent deltas. This prevents work Z0 from being
                // misreported as U630 machine Z0 thousands of times.
                if (!absolute)
                {
                    if (fanuc is not null && context is not null)
                    {
                        var initialValue = axis == 'X' ? incrementalOrigin.X : axis == 'Y' ? incrementalOrigin.Y : incrementalOrigin.Z;
                        workCoordinates[axis] = initialValue + requested;
                        hasContextWorkCoordinate = true;
                        continue;
                    }
                    workCoordinates[axis] = workCoordinates.GetValueOrDefault(axis) + requested;
                    state = state.With(axis, state.Get(axis) + requested);
                    continue;
                }

                workCoordinates[axis] = requested;
                if (context is not null)
                {
                    hasContextWorkCoordinate = true;
                    continue;
                }
                if (!workAnchors.TryGetValue(axis, out var anchor))
                {
                    var machineAnchor = axisDefinitions.TryGetValue(axis, out var definition)
                        ? definition.InitialPosition
                        : state.Get(axis);
                    workAnchors[axis] = (requested, machineAnchor);
                    state = state.With(axis, machineAnchor);
                }
                else
                {
                    state = state.With(axis, anchor.Machine + requested - anchor.Programmed);
                }
            }

            // Modal centre-only arcs are real full circles, even with no XYZ
            // endpoint word. Non-motion blocks in G2/G3 mode must stay still.
            var hasArcCentre = words.Any(w => w.Letter is 'I' or 'J' or 'K');
            if (fanuc is not null && !isMachineCoordinate && !isHome && !fanuc.Cycles.Active &&
                modalMotion is MotionKind.CircularClockwise or MotionKind.CircularCounterClockwise && hasArcCentre)
            {
                hasAxis = true;
                if (context is not null) hasContextWorkCoordinate = true;
            }

            string? warning = toolWarning;
            string? error = fanuc?.Error;
            if (gaugeError is not null) error = Append(error, gaugeError);
            MotionPath? arcPath = null;
            GeneratedCycleMotion? generatedCycle = null;
            if (fanuc is not null && context is not null)
            {
                activeWorkFrame = isHome ? context.WorkFrame : fanuc.WorkFrame(machine, context.WorkFrame, state);
                // In TCPC a feed rotary-only block must preserve modal part XYZ.
                // Haas rapid rotary moves deliberately do not hold the tool tip.
                if (tcpActive && hasAxis && blockMotion != MotionKind.Rapid)
                    hasContextWorkCoordinate = true;
                if (fanuc.Cycles.Triggers(words) && !isMachineCoordinate && !isHome)
                    hasContextWorkCoordinate = true;
            }
            var cycle800 = ParseCycle800(text, variables);
            if (cycle800 is { IsReset: true })
            {
                activeWorkFrame = context?.WorkFrame;
            }
            else if (cycle800 is { IsReset: false } cycle)
            {
                if (Math.Abs(cycle.X0) > 0.000001 || Math.Abs(cycle.Y0) > 0.000001 || Math.Abs(cycle.Z0) > 0.000001 ||
                    Math.Abs(cycle.X1) > 0.000001 || Math.Abs(cycle.Y1) > 0.000001 || Math.Abs(cycle.Z1) > 0.000001)
                {
                    warning = Append(warning, "CYCLE800 sıfır noktası kaydırmaları bu sürümde desteklenmiyor.");
                }
                else if (MachineCoordinateResolver.TryResolveCycle800DualTablePose(
                             machine, cycle.A, cycle.B, cycle.C,
                             out var targetB, out var targetC, out var frameRotationZ))
                {
                    if (cycle.MovesRotaryAxes)
                    {
                        targetC = ResolveRotaryTarget(state.C, targetC, "DC");
                        state = state with { B = targetB, C = targetC };
                        hasAxis = true;
                        machineAxesUpdated.Add('B');
                        machineAxesUpdated.Add('C');
                    }
                    else if (!MachineCoordinateResolver.TryResolveCycle800FrameRotation(
                                 cycle.A, cycle.B, cycle.C, state.B, state.C, out frameRotationZ))
                    {
                        warning = Append(warning, "CYCLE800 manuel B/C pozu ile program düzlemi uyuşmuyor.");
                    }
                    if (context is not null)
                        activeWorkFrame = MachineCoordinateResolver.BuildIndexedWorkFrame(
                            machine, context.WorkFrame, state.B, state.C, frameRotationZ);
                }
                else
                {
                    warning = Append(warning, "CYCLE800 yönelimi bu makine/parametre kombinasyonu için çözülemedi.");
                }
            }

            if (hasContextWorkCoordinate && context is not null && activeWorkFrame is not null)
            {
                var gauge = blockGauge;
                var currentTip = toolCoordinates.TipWorld(start, gauge);
                // Under TRAORI, XYZ are workpiece/TCP coordinates while B/C are
                // physical table axes.  Recover omitted modal XYZ values in the
                // unrotated setup frame, then apply the authored B/C pivot chain
                // to the programmed TCP on every block.  Leaving XYZ in the
                // fixed world frame made the tool detach from the part during
                // simultaneous motion.
                // Alpha 3 Test-18: a work offset lives in the workpiece frame and
                // therefore has to rotate with the B/C table.  The whole motion
                // path is resolved in one single work frame - the exported CAM
                // MCS that the part, stock and fixture STLs share - so no
                // world-space controller-to-CAM patch is applied any more.
                // context.ControllerWorkFrame stays available for NX CSE state
                // comparison only.
                var currentProgramTip = tcpActive
                    ? MachineCoordinateResolver.InverseTransformWorkpiecePoint(machine, currentTip, start.B, start.C)
                    : currentTip;
                var coordinateFrame = tcpActive
                    ? context.WorkFrame
                    : activeWorkFrame;
                var fromOrigin = currentProgramTip - coordinateFrame.Origin;
                if (!workCoordinates.ContainsKey('X')) workCoordinates['X'] = System.Numerics.Vector3.Dot(fromOrigin, coordinateFrame.XAxis);
                if (!workCoordinates.ContainsKey('Y')) workCoordinates['Y'] = System.Numerics.Vector3.Dot(fromOrigin, coordinateFrame.YAxis);
                if (!workCoordinates.ContainsKey('Z')) workCoordinates['Z'] = System.Numerics.Vector3.Dot(fromOrigin, coordinateFrame.ZAxis);

                var nominalStartWork = lastProgrammedWorkPoint ?? new Vector3(
                    Vector3.Dot(fromOrigin, coordinateFrame.XAxis),
                    Vector3.Dot(fromOrigin, coordinateFrame.YAxis),
                    Vector3.Dot(fromOrigin, coordinateFrame.ZAxis));
                var nominalEndWork = new Vector3(
                    (float)workCoordinates['X'], (float)workCoordinates['Y'], (float)workCoordinates['Z']);
                var compensationRadius = activeTool.HasValue && context.ToolRadii is not null &&
                                         context.ToolRadii.TryGetValue(activeTool.Value, out var activeRadius)
                    ? activeRadius
                    : 0.0;
                var mappedEndWork = modalMotion == MotionKind.Linear && hasAxis && compensationRadius > 0 &&
                                    cutterCompensation != CutterCompensation.Off
                    ? ApplyLinearCutterCompensation(
                        nominalStartWork, nominalEndWork, arcPlaneNormal, cutterCompensation, compensationRadius)
                    : nominalEndWork;

                if (fanuc is not null && previousCompensation == CutterCompensation.Off &&
                    cutterCompensation != CutterCompensation.Off && modalMotion == MotionKind.Linear && hasAxis && compensationRadius > 0)
                {
                    // Type A entry ends on the offset start of the following
                    // contour, not on the normal of the approach line.
                    if (TryFanucEntryTangent(inputLines,lineNumber,nominalEndWork,absolute,unitScale,arcPlaneNormal,out var tangent))
                        mappedEndWork = ApplyLinearCutterCompensation(nominalEndWork-tangent,nominalEndWork,arcPlaneNormal,cutterCompensation,compensationRadius);
                    else
                        error = Append(error,"G41/G42 girişinden sonraki kontur yönü çözülemedi; telafili giriş hareketi engellendi.");
                }

                var programmedTip = coordinateFrame.Origin
                    + (coordinateFrame.XAxis * mappedEndWork.X)
                    + (coordinateFrame.YAxis * mappedEndWork.Y)
                    + (coordinateFrame.ZAxis * mappedEndWork.Z);
                if (tcpActive)
                {
                    // The programmed TCP is authored in the work frame that the
                    // exported geometry uses; the physical B/C joint chain then
                    // carries it - and the work offset with it - into machine
                    // space.  Any constant patch added after this rotation is
                    // only correct at B=0 and drifts by |R^-1*d - d| elsewhere.
                    programmedTip = MachineCoordinateResolver.TransformWorkpiecePoint(
                        machine, programmedTip, state.B, state.C);
                }
                state = toolCoordinates.SlidesForTip(programmedTip, state, gauge);
                machineAxesUpdated.Add('X');
                machineAxesUpdated.Add('Y');
                machineAxesUpdated.Add('Z');

                if (!isHome && fanuc?.Cycles.Active != true && modalMotion is MotionKind.CircularClockwise or MotionKind.CircularCounterClockwise)
                {
                    double? Word(char letter) => words.Any(x => x.Letter == letter)
                        ? words.First(x => x.Letter == letter).Value * unitScale
                        : null;
                    var turnMatch = TurnRegex().Match(code);
                    var extraTurns = turnMatch.Success && int.TryParse(turnMatch.Groups["turns"].Value, out var parsedTurns)
                        ? parsedTurns
                        : 0;
                    var radiusMatch = ArcRadiusRegex().Match(code);
                    double? arcRadius = radiusMatch.Success
                        ? ParseNumber(radiusMatch.Groups["radius"].Value) * unitScale
                        : fanuc is not null ? Word('R') : null;
                    arcPath = BuildArcPath(
                        machine, coordinateFrame, tcpActive, arcPlaneNormal,
                        modalMotion == MotionKind.CircularClockwise,
                        nominalStartWork, nominalEndWork,
                        Word('I'), Word('J'), Word('K'), arcRadius, extraTurns,
                        start, state, gauge, cutterCompensation, compensationRadius,
                        out var arcWarning);
                    if (arcWarning is not null)
                    {
                        if (fanuc is not null)
                            error = Append(error, "Yay geometrisi çözülemedi: " + arcWarning.Replace("yay duz cizgi olarak gosteriliyor.", "hareket engellendi."));
                        else if (emittedWarnings.Add("ARC:" + arcWarning))
                            warning = Append(warning, arcWarning);
                    }
                    if (arcPath is { Samples.Count: > 0 } && compensationRadius > 0 &&
                        cutterCompensation != CutterCompensation.Off)
                        state = arcPath.Samples[^1];
                    if (fanuc is null && arcPath is { Revolutions: > 0 } && emittedWarnings.Add("HELIX"))
                        warning = Append(warning,
                            "Helisel G2/G3 (TURN=) bloklari gercek tur sayisiyla interpole ediliyor.");
                }

                if (triggersModalCycle && modalCycle.Active is { } activeCycle)
                {
                    if (traoriActive)
                    {
                        warning = Append(warning,
                            $"{activeCycle.Definition.Name} TRAORI altında güvenli biçimde çözülemedi; modal çevrim hareketi üretilmedi.");
                    }
                    else
                    {
                        var positionedWork = new Vector3(
                            (float)workCoordinates.GetValueOrDefault('X'),
                            (float)workCoordinates.GetValueOrDefault('Y'),
                            (float)workCoordinates.GetValueOrDefault('Z'));
                        AxisState MapCyclePoint(Vector3 work)
                        {
                            var point = coordinateFrame.Origin
                                + (coordinateFrame.XAxis * work.X)
                                + (coordinateFrame.YAxis * work.Y)
                                + (coordinateFrame.ZAxis * work.Z);
                            return toolCoordinates.SlidesForTip(point, state, gauge);
                        }

                        var generated = SinumerikModalCycles.Generate(
                            activeCycle, lineNumber, start, state, feed, spindle, MapCyclePoint, positionedWork);
                        arcPath = generated.Path;
                        generatedCycle = generated.Motion;
                        state = generated.End;
                        var returnPlane = activeCycle.Parameters.ElementAtOrDefault(0);
                        if (returnPlane.HasValue) workCoordinates[activeCycle.PlaneNormal] = returnPlane.Value;
                    }
                }

                if (fanuc?.Cycles.Triggers(words) == true && !isMachineCoordinate && !isHome)
                {
                    try
                    {
                        if (arcPlaneNormal != 'Z' || tcpActive)
                            throw new InvalidDataException("Fanuc delme çevrimleri için sabit G17 düzlemi gereklidir.");
                        AxisState MapFanucCyclePoint(Vector3 work) => toolCoordinates.SlidesForTip(
                            coordinateFrame.Origin + coordinateFrame.XAxis * work.X + coordinateFrame.YAxis * work.Y + coordinateFrame.ZAxis * work.Z, state, gauge);
                        var generated = fanuc.Cycles.Generate(words, absolute, unitScale, lineNumber,
                            new Vector3(Vector3.Dot(fromOrigin, coordinateFrame.XAxis), Vector3.Dot(fromOrigin, coordinateFrame.YAxis), Vector3.Dot(fromOrigin, coordinateFrame.ZAxis)),
                            nominalEndWork, start, feed, MapFanucCyclePoint);
                        state = generated.End; arcPath = generated.Path; generatedCycle = generated.Motion;
                        blockMotion = MotionKind.Linear; hasAxis = true;
                        if (fanuc.Cycles.Code == 83 && emittedWarnings.Add("FANUC_PECK_REENTRY"))
                            warning = Append(warning, "G83 paso ve geri çekilme yolu çözüldü. Kumandanın hızlı yeniden giriş ayarı pakette yok; R düzleminden ilerleme hızı kullanılır, çevrim süresi farklı olabilir.");
                        workCoordinates['Z'] = fanuc.Cycles.ReturnLevel;
                        nominalEndWork.Z = (float)fanuc.Cycles.ReturnLevel;
                    }
                    catch (InvalidDataException ex) { error = Append(error, ex.Message); state = start; arcPath = null; }
                }
                else if (fanuc?.Tcp == true && !isHome && !isMachineCoordinate && blockMotion == MotionKind.Linear &&
                         (Math.Abs(state.B-start.B) > 1e-8 || Math.Abs(state.C-start.C) > 1e-8))
                    arcPath = BuildTcpLinearPath(machine, context.WorkFrame, nominalStartWork, mappedEndWork, start, state, gauge);

                lastProgrammedWorkPoint = nominalEndWork;
            }

            if (isHome)
            {
                var addressed = words.Where(w => "XYZABC".Contains(w.Letter)).Select(w => w.Letter).Distinct().ToArray();
                if (addressed.Length == 0) addressed = axisDefinitions.Keys.ToArray();
                var intermediate = state;
                foreach (var axis in addressed) { state = state.With(axis, 0); machineAxesUpdated.Add(axis); }
                arcPath = new MotionPath(new[] { intermediate, state }, LinearDistance(start, intermediate) + LinearDistance(intermediate, state), 0);
                hasAxis = true;
                workCoordinates.Clear(); lastProgrammedWorkPoint = null;
            }
            if (!(hasContextWorkCoordinate && context is not null && activeWorkFrame is not null) &&
                triggersModalCycle && modalCycle.Active is { } unmappedCycle)
            {
                // A plain parse intentionally has no work frame with which to map
                // generated XYZ. Report that limitation once, rather than once per
                // hole (large production programs otherwise produce warning floods).
                if (emittedWarnings.Add("MODAL_CYCLE_NO_WORK_CONTEXT"))
                    warning = Append(warning,
                        $"{unmappedCycle.Definition.Name} için iş koordinatı bağlamı yok; modal çevrim hareketi üretilmedi.");
            }

            if (ContainsCommand(code, "TRAORI") && emittedWarnings.Add("TRAORI"))
                warning = "TRAORI/TCP programı algılandı; XYZ, fiziksel B/C mafsal zinciriyle TCP hareketine çözüldü.";
            if (cycle800 is { IsReset: false } && emittedWarnings.Add("CYCLE800"))
                warning = Append(warning, "CYCLE800 U630 indeksli B/C ve iş koordinatı çerçevesine çözüldü.");

            foreach (var axis in machineAxesUpdated)
            {
                if (!axisDefinitions.TryGetValue(axis, out var definition)) continue;
                var value = state.Get(axis);
                if (definition.LimitEnabled && (value < definition.Lower - 1e-6 || value > definition.Upper + 1e-6))
                    error = Append(error, $"{axis} limiti aşıldı: {value:0.###} [{definition.Lower:0.###}, {definition.Upper:0.###}]");
            }

            if (fanuc is not null && context is not null && hasAxis &&
                blockMotion != MotionKind.Rapid && !isMachineCoordinate && !isHome)
            {
                var modalWarning = fanuc.CheckCuttingState(context, activeTool, state);
                if (modalWarning is not null) warning = Append(warning, modalWarning);
            }
            if (fanuc is not null && error is not null) unresolvedController = true;

            var blockIndex = blocks.Count;
            blocks.Add(new GCodeBlock(
                lineNumber,
                raw,
                hasAxis ? blockMotion : MotionKind.None,
                start,
                state,
                feed,
                spindle,
                activeTool,
                warning,
                error,
                arcPath,
                generatedCycle,
                isToolChange) { ExecutionBlocked = unresolvedController });
            displayLines.Add(new GCodeSourceLine(lineNumber, raw, blockIndex, warning, error));
        }

        return new GCodeProgram { SourcePath = sourceName, Blocks = blocks, SourceLines = displayLines };

        double Initial(char axis) => axisDefinitions.TryGetValue(axis, out var definition) ? definition.InitialPosition : 0;
    }

    private static bool TryEvaluate(string token, IReadOnlyDictionary<string, double> variables, out double value)
    {
        if (token.StartsWith('_')) return variables.TryGetValue(token, out value);
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed record Cycle800Command(
        bool IsReset,
        double Mode,
        double X0,
        double Y0,
        double Z0,
        double A,
        double B,
        double C,
        double X1,
        double Y1,
        double Z1,
        double PositioningMode,
        double DirectionMode)
    {
        // NX CSE evidence: automatic U630 calls use mode=0 / positioning=1;
        // mode 220000 / positioning=0 keeps the explicit preceding B/C pose.
        public bool MovesRotaryAxes => Math.Abs(Mode) < 0.000001 && Math.Abs(PositioningMode) > 0.000001;
    }

    private static Cycle800Command? ParseCycle800(string text, IReadOnlyDictionary<string, double> variables)
    {
        var match = Cycle800Regex().Match(text);
        if (!match.Success) return null;
        var raw = match.Groups["args"].Value.Trim();
        if (raw.Length == 0) return new Cycle800Command(true, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var args = raw.Split(',').Select(x => x.Trim()).ToArray();
        if (args.Length < 13) return null;
        if (!Value(2, out var mode) ||
            !Value(4, out var x0) || !Value(5, out var y0) || !Value(6, out var z0) ||
            !Value(7, out var a) || !Value(8, out var b) || !Value(9, out var c) ||
            !Value(10, out var x1) || !Value(11, out var y1) || !Value(12, out var z1))
            return null;
        Value(13, out var positioningMode);
        Value(14, out var directionMode);
        return new Cycle800Command(false, mode, x0, y0, z0, a, b, c, x1, y1, z1, positioningMode, directionMode);

        bool Value(int index, out double value)
        {
            value = 0;
            return index < args.Length && TryEvaluate(args[index], variables, out value);
        }
    }

    private static double ResolveRotaryTarget(double current, double requested, string mode)
    {
        var delta = requested - current;
        delta %= 360.0;
        if (mode.Equals("ACP", StringComparison.OrdinalIgnoreCase))
        {
            if (delta < 0) delta += 360.0;
        }
        else if (mode.Equals("ACN", StringComparison.OrdinalIgnoreCase))
        {
            if (delta > 0) delta -= 360.0;
        }
        else
        {
            if (delta > 180.0) delta -= 360.0;
            else if (delta <= -180.0) delta += 360.0;
        }
        return current + delta;
    }

    private static bool ContainsGCode(IEnumerable<(char Letter, double Value)> words, int code) =>
        words.Any(x => x.Letter == 'G' && Math.Abs(x.Value - code) < 0.0001);

    private static bool ContainsCommand(string text, string command) =>
        Regex.IsMatch(text, $@"(?<![A-Z0-9_]){Regex.Escape(command)}(?![A-Z0-9_])", RegexOptions.CultureInvariant);

    private static bool ContainsAnyCommand(string text, params string[] commands) => commands.Any(x => ContainsCommand(text, x));

    private static string Clean(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith('(') && text.EndsWith(')')) return string.Empty;
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0) text = text[..semicolon];
        return text.Trim().TrimStart('/');
    }

    private static string StripQuotedStrings(string text) =>
        Regex.Replace(text, "\"(?:\"\"|[^\"])*\"", "\"\"");

    private static double ParseNumber(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    /// <summary>
    /// Samples a SINUMERIK G2/G3 arc - including a TURN= helix - along its real
    /// path instead of the straight chord between the block endpoints.  The arc
    /// is authored in the active work frame, so every sample is built there and
    /// then carried into machine space through exactly the same chain as the
    /// block endpoint: the physical B/C table chain under TRAORI, the work frame
    /// otherwise.  Without this a thread-milling block such as
    /// "X-11. Y25. Z-15. I-4. J0. TURN=84" collapses into a single straight line
    /// and 85 revolutions never happen.
    /// </summary>
    private static MotionPath? BuildArcPath(
        MachinePackage machine,
        CoordinateFrame frame,
        bool traoriActive,
        char planeNormal,
        bool clockwise,
        Vector3 startWork,
        Vector3 endWork,
        double? offsetI,
        double? offsetJ,
        double? offsetK,
        double? arcRadius,
        int extraTurns,
        AxisState start,
        AxisState end,
        double gauge,
        CutterCompensation cutterCompensation,
        double compensationRadius,
        out string? warning)
    {
        warning = null;
        int first, second, helix;
        double? offsetFirst, offsetSecond;
        switch (char.ToUpperInvariant(planeNormal))
        {
            case 'Y': first = 2; second = 0; helix = 1; offsetFirst = offsetK; offsetSecond = offsetI; break;
            case 'X': first = 1; second = 2; helix = 0; offsetFirst = offsetJ; offsetSecond = offsetK; break;
            default: first = 0; second = 1; helix = 2; offsetFirst = offsetI; offsetSecond = offsetJ; break;
        }

        var s = new double[] { startWork.X, startWork.Y, startWork.Z };
        var e = new double[] { endWork.X, endWork.Y, endWork.Z };

        double centreFirst;
        double centreSecond;
        if (offsetFirst.HasValue || offsetSecond.HasValue)
        {
            centreFirst = s[first] + (offsetFirst ?? 0);
            centreSecond = s[second] + (offsetSecond ?? 0);
        }
        else if (arcRadius.HasValue && Math.Abs(arcRadius.Value) > 1e-9)
        {
            var chordFirst = e[first] - s[first];
            var chordSecond = e[second] - s[second];
            var chord = Math.Sqrt((chordFirst * chordFirst) + (chordSecond * chordSecond));
            var radius = Math.Abs(arcRadius.Value);
            if (chord < 1e-9 || chord > 2 * radius + 1e-6)
            {
                warning = "CR= yaricapi blok uc noktalariyla uyusmuyor; yay duz cizgi olarak gosteriliyor.";
                return null;
            }
            var height = Math.Sqrt(Math.Max(0, (radius * radius) - (chord * chord / 4)));
            // The sign selects which of the two possible centres is used: a
            // negative CR asks for the arc bigger than a half circle.
            var side = (clockwise ? -1.0 : 1.0) * (arcRadius.Value < 0 ? -1.0 : 1.0);
            centreFirst = ((s[first] + e[first]) / 2) + (side * height * (-chordSecond / chord));
            centreSecond = ((s[second] + e[second]) / 2) + (side * height * (chordFirst / chord));
        }
        else
        {
            warning = "G2/G3 blogunda yay merkezi (I/J/K) veya CR= yok; yay duz cizgi olarak gosteriliyor.";
            return null;
        }

        var startRadius = Math.Sqrt(Math.Pow(s[first] - centreFirst, 2) + Math.Pow(s[second] - centreSecond, 2));
        var endRadius = Math.Sqrt(Math.Pow(e[first] - centreFirst, 2) + Math.Pow(e[second] - centreSecond, 2));
        if (startRadius < 1e-6)
        {
            warning = "G2/G3 yay yaricapi sifir; yay duz cizgi olarak gosteriliyor.";
            return null;
        }
        if (Math.Abs(startRadius - endRadius) > Math.Max(0.05, startRadius * 0.002))
        {
            warning = $"G2/G3 baslangic ve bitis yaricapi uyusmuyor ({startRadius:0.###} / {endRadius:0.###} mm); "
                + "yay duz cizgi olarak gosteriliyor.";
            return null;
        }

        var compensatedRadius = startRadius;
        if (cutterCompensation != CutterCompensation.Off && compensationRadius > 0)
        {
            var outward = cutterCompensation == CutterCompensation.Left
                ? clockwise
                : !clockwise;
            compensatedRadius += outward ? compensationRadius : -compensationRadius;
            if (compensatedRadius <= 0.001)
            {
                warning = "G41/G42 takım yarıçapı yay yarıçapından büyük; telafili yay üretilemedi.";
                return null;
            }
        }

        var startAngle = Math.Atan2(s[second] - centreSecond, s[first] - centreFirst);
        var endAngle = Math.Atan2(e[second] - centreSecond, e[first] - centreFirst);
        var sweep = endAngle - startAngle;
        const double twoPi = Math.PI * 2;
        if (FanucModalState.Applies(machine))
        {
            // Recovering omitted work coordinates from float scene transforms
            // can separate identical programmed XY by a few ULPs. A nominal
            // full helix then became a near-zero sweep and a vertical plunge.
            var magnitude = (float)Math.Max(1, new[]{Math.Abs(start.X),Math.Abs(start.Y),Math.Abs(start.Z),Math.Abs(end.X),Math.Abs(end.Y),Math.Abs(end.Z),
                Math.Abs(frame.Origin.X),Math.Abs(frame.Origin.Y),Math.Abs(frame.Origin.Z)}.Max());
            var tolerance = 4.0 * (MathF.BitIncrement(magnitude)-magnitude);
            if (Math.Abs(s[first]-e[first])<=tolerance && Math.Abs(s[second]-e[second])<=tolerance)
                sweep = clockwise ? -twoPi : twoPi;
        }
        if (clockwise)
        {
            while (sweep > -1e-12) sweep -= twoPi;
            if (Math.Abs(sweep + twoPi) < 1e-9 && extraTurns == 0 && Math.Abs(s[helix] - e[helix]) < 1e-9)
                sweep = -twoPi;
        }
        else
        {
            while (sweep < 1e-12) sweep += twoPi;
            if (Math.Abs(sweep - twoPi) < 1e-9 && extraTurns == 0 && Math.Abs(s[helix] - e[helix]) < 1e-9)
                sweep = twoPi;
        }
        sweep += Math.Sign(sweep) * twoPi * Math.Max(0, extraTurns);

        // Chord tolerance drives the sampling density, then it is clamped so a
        // long helix cannot explode into an unbounded number of samples.
        const double chordTolerance = 0.02;
        var step = 2 * Math.Acos(Math.Clamp(1 - (chordTolerance / startRadius), -1, 1));
        step = Math.Clamp(step, 0.5 * Math.PI / 180, 6 * Math.PI / 180);
        var count = (int)Math.Ceiling(Math.Abs(sweep) / step);
        count = Math.Clamp(count, 8, 20000);

        var samples = new List<AxisState>(count);
        var toolCoordinates = MachineToolCoordinates.For(machine);
        var previous = toolCoordinates.TipWorld(start, gauge);
        var length = 0.0;
        for (var index = 1; index <= count; index++)
        {
            var t = (double)index / count;
            var angle = startAngle + (sweep * t);
            var work = new double[3];
            work[first] = centreFirst + (compensatedRadius * Math.Cos(angle));
            work[second] = centreSecond + (compensatedRadius * Math.Sin(angle));
            work[helix] = s[helix] + ((e[helix] - s[helix]) * t);

            var b = start.B + ((end.B - start.B) * t);
            var c = start.C + ((end.C - start.C) * t);
            var point = frame.Origin
                + (frame.XAxis * (float)work[0])
                + (frame.YAxis * (float)work[1])
                + (frame.ZAxis * (float)work[2]);
            if (traoriActive) point = MachineCoordinateResolver.TransformWorkpiecePoint(machine, point, b, c);

            length += Vector3.Distance(previous, point);
            previous = point;
            samples.Add(toolCoordinates.SlidesForTip(point, new AxisState(
                0, 0, 0,
                start.A + ((end.A - start.A) * t),
                b,
                c), gauge));
        }

        // Without radius compensation the modal endpoint is exact and wins over
        // floating point sampling.  With G41/G42 the last sample is deliberately
        // offset from the programmed contour and must remain the controller path.
        if (cutterCompensation == CutterCompensation.Off || compensationRadius <= 0)
            samples[^1] = end;
        return new MotionPath(samples, length, (int)Math.Floor(Math.Abs(sweep) / twoPi));
    }

    private static Vector3 ApplyLinearCutterCompensation(
        Vector3 start,
        Vector3 end,
        char planeNormal,
        CutterCompensation compensation,
        double radius)
    {
        int first;
        int second;
        switch (char.ToUpperInvariant(planeNormal))
        {
            case 'Y': first = 2; second = 0; break;
            case 'X': first = 1; second = 2; break;
            default: first = 0; second = 1; break;
        }
        var values = new[] { (double)end.X, end.Y, end.Z };
        var starts = new[] { (double)start.X, start.Y, start.Z };
        var dx = values[first] - starts[first];
        var dy = values[second] - starts[second];
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-9) return end;
        var side = compensation == CutterCompensation.Left ? 1.0 : -1.0;
        values[first] += side * -dy * radius / length;
        values[second] += side * dx * radius / length;
        return new Vector3((float)values[0], (float)values[1], (float)values[2]);
    }

    private static string Append(string? current, string addition) => string.IsNullOrWhiteSpace(current) ? addition : current + " " + addition;
}
