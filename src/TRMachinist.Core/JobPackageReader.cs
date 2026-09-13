using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace TRMachinist.Core;

public static class JobPackageReader
{
    public static JobPackage Load(string packagePath)
    {
        var source = Path.GetFullPath(packagePath);
        if (!File.Exists(source)) throw new FileNotFoundException("NX iş paketi bulunamadı.", source);

        var sha = PackageArchive.ComputeFileSha256(source);
        using var archive = ZipFile.OpenRead(source);
        var entries = PackageArchive.ValidateEntries(archive);
        foreach (var required in new[] { "manifest.json", "project.json", "tools.json", "operations.json" })
        {
            if (!entries.ContainsKey(required)) throw new InvalidDataException($"NX iş paketi girdisi eksik: {required}");
        }

        using var manifestDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "manifest.json"));
        var format = JsonRead.String(manifestDoc.RootElement, "format");
        if (!format.Equals("shopdoc-viewer-package", StringComparison.OrdinalIgnoreCase) &&
            !format.StartsWith("trmachinist-job", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Desteklenmeyen NX iş paketi: {format}");
        }
        if (format.StartsWith("trmachinist-job", StringComparison.OrdinalIgnoreCase))
            ValidateTrJobIntegrity(manifestDoc.RootElement, entries);

        var cache = PackageArchive.ExtractToCache(archive, entries, "Jobs", sha);
        using var projectDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "project.json"));
        using var toolsDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "tools.json"));
        using var operationsDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "operations.json"));
        var project = projectDoc.RootElement;

        var models = new List<JobModelAsset>();
        if (project.TryGetProperty("models", out var modelArray))
        {
            foreach (var item in modelArray.EnumerateArray())
            {
                var path = JsonRead.String(item, "path");
                if (path.Length == 0 || !entries.ContainsKey(path.Replace('\\', '/'))) continue;
                models.Add(new JobModelAsset(
                    JsonRead.String(item, "role", "body"),
                    path,
                    JsonRead.String(item, "title", Path.GetFileNameWithoutExtension(path)),
                    JsonRead.String(item, "color", "#A8B6AE")));
            }
        }

        var tools = new List<JobTool>();
        foreach (var item in toolsDoc.RootElement.EnumerateArray())
        {
            var modelPath = JsonRead.String(item, "model");
            tools.Add(new JobTool(
                JsonRead.String(item, "id"),
                JsonRead.String(item, "name"),
                JsonRead.String(item, "type"),
                JsonRead.Double(item, "diameter"),
                JsonRead.Double(item, "radius"),
                JsonRead.Double(item, "length"),
                JsonRead.String(item, "holder"),
                modelPath.Length > 0 && entries.ContainsKey(modelPath.Replace('\\', '/')) ? modelPath : null,
                JsonRead.String(item, "subtype"),
                JsonRead.Double(item, "fluteLength"),
                JsonRead.Double(item, "shankDiameter"),
                JsonRead.Double(item, "tipAngle"),
                JsonRead.Double(item, "taperAngle"),
                JsonRead.Double(item, "toolInsertion"),
                JsonRead.String(item, "holderLibraryReference"),
                JsonRead.String(item, "geometrySource"),
                ReadToolSections(item, "shankSections"),
                ReadToolSections(item, "holderSections"),
                ReadOptionalVector(item, "mountPoint"),
                ReadOptionalVector(item, "tipPoint")));
        }

        var operations = new List<JobOperation>();
        foreach (var item in operationsDoc.RootElement.EnumerateArray())
        {
            var toolpath = JsonRead.String(item, "toolpath");
            operations.Add(new JobOperation(
                JsonRead.String(item, "id"),
                JsonRead.Int(item, "no"),
                JsonRead.String(item, "name"),
                JsonRead.String(item, "program"),
                JsonRead.String(item, "type"),
                JsonRead.String(item, "toolId"),
                JsonRead.Double(item, "feed"),
                JsonRead.Double(item, "spindle"),
                toolpath.Length > 0 && entries.ContainsKey(toolpath.Replace('\\', '/')) ? toolpath : null));
        }

        return new JobPackage
        {
            SourcePath = source,
            CacheRoot = cache,
            PackageSha256 = sha,
            Format = format,
            PartNumber = JsonRead.String(project, "partNumber", "-"),
            PartName = JsonRead.String(project, "partName", Path.GetFileNameWithoutExtension(source)),
            Program = JsonRead.String(project, "program", "-"),
            HasExplicitMachineMount = project.TryGetProperty("machineMountCsys", out var mountElement) && mountElement.ValueKind == JsonValueKind.Object,
            HasExplicitControllerWorkFrame = project.TryGetProperty("controllerWorkFrame", out var controllerFrameElement) && controllerFrameElement.ValueKind == JsonValueKind.Object,
            Mcs = ReadFrame(project, "mcs", "MCS"),
            MachineMount = ReadFrame(project, "machineMountCsys", "MACHINE_MOUNT"),
            MountAxisConvention = project.TryGetProperty("machineMountCsys", out var mountConvention)
                && mountConvention.ValueKind == JsonValueKind.Object
                    ? JsonRead.String(mountConvention, "axisConvention", "nx-csys")
                    : "nx-csys",
            ControllerWorkFrame = project.TryGetProperty("controllerWorkFrame", out _)
                ? ReadFrame(project, "controllerWorkFrame", "G54")
                : ReadFrame(project, "mcs", "MCS"),
            Models = models,
            Tools = tools,
            Operations = operations
        };
    }

    private static CoordinateFrame ReadFrame(JsonElement project, string property, string fallbackLabel)
    {
        if (!project.TryGetProperty(property, out var frame) || frame.ValueKind != JsonValueKind.Object)
            return CoordinateFrame.Identity(fallbackLabel);
        return new CoordinateFrame(
            JsonRead.String(frame, "label", fallbackLabel),
            JsonRead.VectorProperty(frame, "origin", Vector3.Zero),
            Normalize(JsonRead.VectorProperty(frame, "xAxis", Vector3.UnitX), Vector3.UnitX),
            Normalize(JsonRead.VectorProperty(frame, "yAxis", Vector3.UnitY), Vector3.UnitY),
            Normalize(JsonRead.VectorProperty(frame, "zAxis", Vector3.UnitZ), Vector3.UnitZ),
            JsonRead.String(frame, "source", "nx-csys"));
    }

    private static Vector3 Normalize(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    private static Vector3? ReadOptionalVector(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        return JsonRead.Vector(value, Vector3.Zero);
    }

    private static IReadOnlyList<ToolProfileSection> ReadToolSections(JsonElement tool, string property)
    {
        var result = new List<ToolProfileSection>();
        if (!tool.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in array.EnumerateArray())
        {
            var diameter = JsonRead.Double(item, "diameter");
            var length = JsonRead.Double(item, "length");
            if (diameter <= 0 || length <= 0) continue;
            result.Add(new ToolProfileSection(
                diameter,
                length,
                JsonRead.Double(item, "taperAngle"),
                JsonRead.Double(item, "cornerRadius")));
        }
        return result;
    }

    private static void ValidateTrJobIntegrity(JsonElement manifest, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!manifest.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(".trjob manifest SHA-256 envanteri eksik.");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            var path = JsonRead.String(item, "path");
            if (!entries.TryGetValue(path, out var entry)) throw new InvalidDataException($"TRJOB girdisi eksik: {path}");
            declared.Add(path);
            if (item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) && size != entry.Length)
                throw new InvalidDataException($"TRJOB boyutu uyuşmuyor: {path}");
            var expected = JsonRead.String(item, "sha256");
            using var stream = entry.Open();
            var actual = PackageArchive.ComputeSha256(stream);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"TRJOB SHA-256 uyuşmuyor: {path}");
        }
        foreach (var path in entries.Keys.Where(path => !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
            if (!declared.Contains(path)) throw new InvalidDataException($"TRJOB manifestinde bildirilmeyen girdi: {path}");
    }
}
