using System.Text.RegularExpressions;

namespace TRMachinist.Core;

/// <summary>
/// One NX CAM operation mapped onto the posted NC block interval that belongs
/// to it. TRJOB supplies the real operation metadata; the post's MSG line is
/// the authoritative boundary in the executable NC stream.
/// </summary>
public sealed record GCodeOperationRange(
    string Id,
    int Number,
    string Name,
    string Program,
    string Type,
    string ToolId,
    int StartBlockIndex,
    int EndBlockIndex,
    int StartSourceLine,
    int EndSourceLine)
{
    public bool ContainsBlock(int blockIndex) =>
        blockIndex >= StartBlockIndex && blockIndex <= EndBlockIndex;

    public string DisplayName =>
        $"{Number:000}  {Name}" +
        (string.IsNullOrWhiteSpace(Type) ? string.Empty : $" — {Type}") +
        (string.IsNullOrWhiteSpace(ToolId) ? string.Empty : $" · {ToolId}");

    public override string ToString() => DisplayName;
}

public static partial class GCodeOperationCatalog
{
    public static IReadOnlyList<GCodeOperationRange> Build(
        GCodeProgram program,
        IReadOnlyList<JobOperation>? jobOperations)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Blocks.Count == 0) return Array.Empty<GCodeOperationRange>();

        var markers = program.Blocks
            .Select((block, index) => (Index: index, Name: OperationName(block.Raw)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => (item.Index, Name: item.Name!))
            .ToArray();
        if (markers.Length == 0)
            return BuildToolSections(program);

        var metadata = (jobOperations ?? Array.Empty<JobOperation>())
            .GroupBy(operation => operation.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<JobOperation>(group.OrderBy(operation => operation.Number)),
                StringComparer.OrdinalIgnoreCase);
        var ranges = new List<GCodeOperationRange>(markers.Length);
        for (var markerIndex = 0; markerIndex < markers.Length; markerIndex++)
        {
            var marker = markers[markerIndex];
            var endBlock = markerIndex + 1 < markers.Length
                ? markers[markerIndex + 1].Index - 1
                : program.Blocks.Count - 1;
            if (endBlock < marker.Index ||
                !program.Blocks.Skip(marker.Index).Take(endBlock - marker.Index + 1).Any(block => block.HasMotion))
                continue;

            JobOperation? operation = null;
            if (metadata.TryGetValue(marker.Name, out var matches) && matches.Count > 0)
                operation = matches.Dequeue();
            var sequence = ranges.Count + 1;
            ranges.Add(new GCodeOperationRange(
                operation?.Id ?? $"NC_OP_{sequence:000}",
                operation?.Number > 0 ? operation.Number : sequence,
                operation?.Name ?? marker.Name,
                operation?.Program ?? "NC_PROGRAM",
                operation?.Type ?? "NC operasyonu",
                operation?.ToolId ?? ToolId(program, marker.Index, endBlock),
                marker.Index,
                endBlock,
                program.Blocks[marker.Index].SourceLine,
                program.Blocks[endBlock].SourceLine));
        }
        return ranges;
    }

    public static int FindOperationIndex(
        IReadOnlyList<GCodeOperationRange> operations,
        int blockIndex)
    {
        for (var index = 0; index < operations.Count; index++)
            if (operations[index].ContainsBlock(blockIndex))
                return index;
        return operations.Count == 0
            ? -1
            : blockIndex < operations[0].StartBlockIndex ? 0 : operations.Count - 1;
    }

    private static IReadOnlyList<GCodeOperationRange> BuildToolSections(GCodeProgram program)
    {
        var starts = new List<int> { 0 };
        for (var index = 1; index < program.Blocks.Count; index++)
            if (program.Blocks[index].IsToolChange)
                starts.Add(index);

        var ranges = new List<GCodeOperationRange>();
        for (var section = 0; section < starts.Count; section++)
        {
            var start = starts[section];
            var end = section + 1 < starts.Count ? starts[section + 1] - 1 : program.Blocks.Count - 1;
            if (!program.Blocks.Skip(start).Take(end - start + 1).Any(block => block.HasMotion))
                continue;
            var number = ranges.Count + 1;
            var toolId = ToolId(program, start, end);
            ranges.Add(new GCodeOperationRange(
                $"NC_SECTION_{number:000}",
                number,
                string.IsNullOrWhiteSpace(toolId) ? $"NC BÖLÜM {number}" : $"{toolId} BÖLÜM {number}",
                "NC_PROGRAM",
                "Takım değişimi bölümü",
                toolId,
                start,
                end,
                program.Blocks[start].SourceLine,
                program.Blocks[end].SourceLine));
        }
        return ranges;
    }

    private static string ToolId(GCodeProgram program, int start, int end)
    {
        for (var index = start; index <= end; index++)
            if (program.Blocks[index].Tool is int tool)
                return $"T{tool:00}";
        return string.Empty;
    }

    private static string? OperationName(string raw)
    {
        var match = OperationMessageRegex().Match(raw);
        if (!match.Success) match = OperationCommentRegex().Match(raw);
        if (!match.Success) return null;
        var text = match.Groups["text"].Value.Trim();
        var tool = ToolSuffixRegex().Match(text);
        if (tool.Success) text = text[..tool.Index].Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex("MSG\\s*\\(\\s*[\\\"'](?<text>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex OperationMessageRegex();

    [GeneratedRegex("^\\s*\\((?<text>[^()]+?\\s+TOOL\\s*:[^()]*)\\)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OperationCommentRegex();

    [GeneratedRegex("(?:,\\s*|\\s+)TOOL\\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ToolSuffixRegex();
}
