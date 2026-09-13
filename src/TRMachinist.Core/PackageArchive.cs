using System.IO.Compression;
using System.Security.Cryptography;

namespace TRMachinist.Core;

internal static class PackageArchive
{
    public static IReadOnlyDictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)))
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(segment => segment is ".." or "."))
            {
                throw new InvalidDataException($"Güvensiz paket yolu: {entry.FullName}");
            }

            if (!entries.TryAdd(name, entry))
            {
                throw new InvalidDataException($"Tekrarlı paket yolu: {entry.FullName}");
            }
        }

        return entries;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return ComputeSha256(stream);
    }

    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string ReadText(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(path, out var entry))
        {
            throw new InvalidDataException($"Paket girdisi eksik: {path}");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string ExtractToCache(
        ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string category,
        string packageSha)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "TRMachinist", "Cache", category, packageSha);
        Directory.CreateDirectory(root);
        var canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        foreach (var pair in entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Paket çıkarma yolu güvenli değil: {pair.Key}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            pair.Value.ExtractToFile(destination, overwrite: true);
        }

        File.WriteAllText(Path.Combine(root, ".package.sha256"), packageSha);
        return root;
    }
}
