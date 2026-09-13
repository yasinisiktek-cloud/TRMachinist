using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TRMachinist.Core;

public static class MachinePackageReader
{
    public static MachinePackage Load(string packagePath)
    {
        var source = Path.GetFullPath(packagePath);
        if (!File.Exists(source)) throw new FileNotFoundException(".trmac bulunamadı.", source);

        var packageSha = PackageArchive.ComputeFileSha256(source);
        using var archive = ZipFile.OpenRead(source);
        var entries = PackageArchive.ValidateEntries(archive);
        if (!entries.ContainsKey("manifest.json") || !entries.ContainsKey("machine.json") ||
            !entries.ContainsKey("kinematics.json") || !entries.ContainsKey("junctions.json"))
        {
            throw new InvalidDataException("Geçerli .trmac kök dosyaları eksik.");
        }

        using var manifestDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "manifest.json"));
        var manifest = manifestDoc.RootElement;
        var schema = JsonRead.String(manifest, "schema");
        if (!schema.StartsWith("trmac/0.3.", StringComparison.OrdinalIgnoreCase) &&
            !schema.StartsWith("trmac/0.4.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Desteklenmeyen .trmac şeması: {schema}");
        }

        ValidateIntegrity(manifest, entries);
        var cacheRoot = PackageArchive.ExtractToCache(archive, entries, "Machines", packageSha);

        using var machineDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "machine.json"));
        using var kinDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "kinematics.json"));
        using var junctionDoc = JsonDocument.Parse(PackageArchive.ReadText(entries, "junctions.json"));

        var geometry = ReadGeometry(entries);
        var meshByCad = geometry
            .Where(x => !string.IsNullOrWhiteSpace(x.RuntimeMeshPath))
            .ToDictionary(x => x.CadPath, x => x.RuntimeMeshPath, StringComparer.OrdinalIgnoreCase);

        var machine = machineDoc.RootElement;
        var components = new List<MachineComponent>();
        foreach (var item in machine.GetProperty("components").EnumerateArray())
        {
            var cad = item.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
            string? mesh = null;
            if (!string.IsNullOrWhiteSpace(cad)) meshByCad.TryGetValue(cad, out mesh);
            var graphicsTransform = ReadGraphicsTransform(item);
            components.Add(new MachineComponent(
                JsonRead.String(item, "name"),
                JsonRead.String(item, "parent") is { Length: > 0 } parent ? parent : null,
                cad,
                mesh,
                JsonRead.String(item, "role", "kinematic"),
                graphicsTransform));
        }

        var axes = new List<AxisDefinition>();
        foreach (var item in kinDoc.RootElement.GetProperty("axes").EnumerateArray())
        {
            axes.Add(new AxisDefinition(
                JsonRead.String(item, "name"),
                JsonRead.String(item, "type"),
                JsonRead.String(item, "component"),
                JsonRead.String(item, "junction"),
                JsonRead.VectorProperty(item, "vector", Vector3.Zero),
                JsonRead.Double(item, "initial_position"),
                JsonRead.Bool(item, "limit_enabled"),
                JsonRead.Double(item, "lower", double.NegativeInfinity),
                JsonRead.Double(item, "upper", double.PositiveInfinity),
                JsonRead.Double(item, "maximum_velocity", 100)));
        }

        var junctions = new List<JunctionDefinition>();
        foreach (var item in junctionDoc.RootElement.GetProperty("junctions").EnumerateArray())
        {
            var origin = Vector3.Zero;
            var orientation = Matrix4x4.Identity;
            if (item.TryGetProperty("frame", out var frame))
            {
                origin = JsonRead.VectorProperty(frame, "origin", Vector3.Zero);
                if (frame.TryGetProperty("orientation", out var matrix)) orientation = JsonRead.Orientation(matrix);
            }
            junctions.Add(new JunctionDefinition(JsonRead.String(item, "name"), JsonRead.String(item, "owner"), origin, orientation));
        }

        var collidable = ReadCollidable(entries);
        var controller = manifest.TryGetProperty("controller", out var controllerElement)
            ? JsonRead.String(controllerElement, "controller_family", "Unknown")
            : "Unknown";
        if (string.IsNullOrWhiteSpace(controller) || controller.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            controller = controllerElement.ValueKind == JsonValueKind.Object
                ? JsonRead.String(controllerElement, "mcf_adapter", "Unknown") : "Unknown";
        var controllerCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Read declared command names only. Never execute code from controller assets.
        foreach (var entry in entries.Where(x => x.Key.StartsWith("controller/decrypted/", StringComparison.OrdinalIgnoreCase)
                                                && x.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Value.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 });
            var document = XDocument.Load(reader);
            foreach (var node in document.Descendants("Metacode"))
                if (node.Element("Name")?.Value.Trim() is { Length: > 0 } command)
                    controllerCommands.Add(command.ToUpperInvariant());
        }

        return new MachinePackage
        {
            SourcePath = source,
            CacheRoot = cacheRoot,
            PackageSha256 = packageSha,
            Schema = schema,
            MachineName = JsonRead.String(manifest, "machine_name", Path.GetFileNameWithoutExtension(source)),
            ControllerFamily = controller,
            ControllerCommands = controllerCommands,
            RootComponent = JsonRead.String(machine, "root_component", "MACHINE_BASE"),
            Components = components,
            Axes = axes,
            Junctions = junctions,
            Geometry = geometry,
            CollidableComponents = collidable
        };
    }

    private static void ValidateIntegrity(JsonElement manifest, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!manifest.TryGetProperty("package_integrity", out var integrity) ||
            !integrity.TryGetProperty("entries", out var declaredArray))
        {
            throw new InvalidDataException(".trmac bütünlük envanteri eksik.");
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in declaredArray.EnumerateArray())
        {
            var path = JsonRead.String(item, "path");
            if (path == "manifest.json") continue;
            if (!entries.TryGetValue(path, out var entry)) throw new InvalidDataException($"Manifest girdisi arşivde yok: {path}");
            declared.Add(path);
            if (item.TryGetProperty("size", out var size) && size.TryGetInt64(out var expectedSize) && expectedSize != entry.Length)
                throw new InvalidDataException($"Paket boyutu uyuşmuyor: {path}");
            var expectedHash = JsonRead.String(item, "sha256");
            using var stream = entry.Open();
            var actualHash = PackageArchive.ComputeSha256(stream);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Paket SHA-256 uyuşmuyor: {path}");
        }

        foreach (var path in entries.Keys.Where(x => !x.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!declared.Contains(path)) throw new InvalidDataException($"Manifestte bildirilmeyen paket girdisi: {path}");
        }
    }

    private static IReadOnlyList<GeometryAsset> ReadGeometry(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        using var doc = JsonDocument.Parse(PackageArchive.ReadText(entries, "geometry/index.json"));
        var result = new List<GeometryAsset>();
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : doc.RootElement.GetProperty("items").EnumerateArray();
        foreach (var item in items)
        {
            var cad = JsonRead.String(item, "file", JsonRead.String(item, "cad_file"));
            var mesh = JsonRead.String(item, "runtime_mesh", JsonRead.String(item, "mesh"));
            result.Add(new GeometryAsset(cad, string.IsNullOrWhiteSpace(mesh) ? null : mesh,
                JsonRead.String(item, "format", "STEP"),
                (long)JsonRead.Double(item, "size"),
                JsonRead.String(item, "sha256") is { Length: > 0 } sha ? sha : null));
        }
        return result;
    }

    private static IReadOnlySet<string> ReadCollidable(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.ContainsKey("collision.json")) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(PackageArchive.ReadText(entries, "collision.json"));
        if (!doc.RootElement.TryGetProperty("runtime_policy", out var policy) ||
            !policy.TryGetProperty("collidable_components", out var array))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return array.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Matrix4x4 ReadGraphicsTransform(JsonElement component)
    {
        if (!component.TryGetProperty("graphics_transform", out var transform) ||
            transform.ValueKind != JsonValueKind.Object)
            return Matrix4x4.Identity;

        var origin = JsonRead.VectorProperty(transform, "origin", Vector3.Zero);
        var matrix = transform.TryGetProperty("orientation", out var orientation)
            ? JsonRead.Orientation(orientation)
            : Matrix4x4.Identity;
        matrix.M41 = origin.X;
        matrix.M42 = origin.Y;
        matrix.M43 = origin.Z;
        return matrix;
    }
}
