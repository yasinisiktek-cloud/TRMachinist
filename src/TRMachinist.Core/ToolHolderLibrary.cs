namespace TRMachinist.Core;

/// <summary>
/// A holder is deliberately independent from the cutter.  This mirrors the NX
/// tool-builder contract: the cutter/shank dimensions stay on <see cref="JobTool"/>
/// while the holder supplies an axial section profile and TOOLINS overlap.
/// </summary>
public sealed record ToolHolderPreset(
    string Id,
    string Name,
    string Interface,
    double ToolInsertion,
    IReadOnlyList<ToolProfileSection> Sections,
    string Source)
{
    public double AxialLength => Sections.Sum(section => Math.Max(0, section.Length));
    public double MaximumDiameter => Sections.Count == 0
        ? 0
        : Sections.Max(section => Math.Max(0, section.Diameter));
}

public static class ToolHolderLibrary
{
    /// <summary>
    /// Builds a job-aware holder catalog.  Exact profiles already exported by
    /// NX are listed first; generic simulator presets remain available when the
    /// job does not carry a suitable holder.  Generic entries are collision and
    /// visualization profiles, not manufacturer-certified production drawings.
    /// </summary>
    public static IReadOnlyList<ToolHolderPreset> Create(IEnumerable<JobTool>? jobTools = null)
    {
        var result = new List<ToolHolderPreset>();
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in jobTools ?? Array.Empty<JobTool>())
        {
            if (tool.HolderSections.Count == 0) continue;
            var signature = ProfileSignature(tool.ToolInsertion, tool.HolderSections);
            if (!signatures.Add(signature)) continue;
            var reference = FirstNonEmpty(tool.HolderLibraryReference, tool.Holder, tool.Name, tool.Id);
            result.Add(new ToolHolderPreset(
                "job:" + SanitizeId(reference),
                $"NX işinden · {reference}",
                InterfaceFrom(reference),
                Math.Max(0, tool.ToolInsertion),
                tool.HolderSections.ToArray(),
                "NX .trjob"));
        }

        foreach (var preset in BuiltIns())
        {
            if (signatures.Add(ProfileSignature(preset.ToolInsertion, preset.Sections)))
                result.Add(preset);
        }
        return result;
    }

    private static IEnumerable<ToolHolderPreset> BuiltIns()
    {
        yield return Preset("sim-hsk63-er16-slim", "HSK63 · ER16 ince simülasyon profili", "HSK63", 18,
            (18, 12, 12), (28, 24, 5), (40, 10, 0), (63, 18, 8));
        yield return Preset("sim-hsk63-er25", "HSK63 · ER25 simülasyon profili", "HSK63", 20,
            (25, 14, 10), (35, 28, 4), (45, 10, 0), (63, 18, 8));
        yield return Preset("sim-hsk63-er32", "HSK63 · ER32 simülasyon profili", "HSK63", 22,
            (32, 16, 9), (42, 30, 3), (50, 10, 0), (63, 18, 8));
        yield return Preset("sim-hsk63-er40", "HSK63 · ER40 simülasyon profili", "HSK63", 24,
            (40, 18, 8), (50, 32, 3), (58, 10, 0), (63, 18, 8));
        yield return Preset("sim-hsk63-weldon16", "HSK63 · Weldon Ø16 simülasyon profili", "HSK63", 16,
            (22, 34, 2), (42, 14, 8), (63, 18, 8));
        yield return Preset("sim-hsk63-shrink12", "HSK63 · Shrink Ø12 ince simülasyon profili", "HSK63", 20,
            (18, 38, 2), (32, 18, 7), (63, 18, 8));
        yield return Preset("sim-bt40-er32", "BT40 · ER32 simülasyon profili", "BT40", 22,
            (32, 16, 9), (42, 32, 3), (48, 12, 0), (63, 38, 8));
        yield return Preset("sim-sk40-weldon20", "SK40 · Weldon Ø20 simülasyon profili", "SK40", 18,
            (28, 38, 2), (44, 16, 7), (63, 38, 8));
    }

    private static ToolHolderPreset Preset(
        string id,
        string name,
        string @interface,
        double insertion,
        params (double Diameter, double Length, double TaperAngle)[] sections) =>
        new(
            id,
            name,
            @interface,
            insertion,
            sections.Select(section => new ToolProfileSection(
                section.Diameter,
                section.Length,
                section.TaperAngle,
                0)).ToArray(),
            "TRMachinist hazır profil");

    private static string ProfileSignature(double insertion, IReadOnlyList<ToolProfileSection> sections) =>
        insertion.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + "|" +
        string.Join(";", sections.Select(section => string.Join(",",
            section.Diameter.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
            section.Length.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
            section.TaperAngle.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
            section.CornerRadius.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture))));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "İsimsiz tutucu";

    private static string SanitizeId(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string InterfaceFrom(string value)
    {
        foreach (var candidate in new[] { "HSK100", "HSK80", "HSK63", "HSK50", "HSK40", "BT50", "BT40", "SK50", "SK40" })
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        return "Genel";
    }
}
