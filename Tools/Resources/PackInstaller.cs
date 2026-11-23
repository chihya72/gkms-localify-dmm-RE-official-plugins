using System.IO.Compression;
using System.Text.Json;

namespace Gkms.Resources;

public static class PackInstaller
{
    public static Uri SelectReleaseZip(JsonElement release, bool textures)
    {
        var zips = release.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString()!, Url: a.GetProperty("browser_download_url").GetString()!))
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var preferred = textures ? "texture2d.zip" : "GakumasTranslationData.zip";
        var asset = zips.Where(a => textures || !a.Name.Equals("texture2d.zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("发布页缺少相应的资源 ZIP 地址。");
        return uri;
    }

    // The distribution marker identifies the payload, including release ZIPs with a wrapper folder.
    public static int Install(string packageDirectory, bool textures, ZipArchive archive, string? release)
    {
        var marker = textures ? "texture_version.txt" : "localization.json";
        var matches = archive.Entries.Where(e => e.Name.Equals(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A master-data table can also be named Localization.json. Only a marker
        // with no ancestor payload marker identifies an archive's resource root.
        // Sibling roots (or duplicate markers at one root) remain ambiguous.
        var markers = matches.Where(candidate => !matches.Any(parent =>
            parent.FullName.Length < candidate.FullName.Length &&
            candidate.FullName.StartsWith(parent.FullName[..^parent.Name.Length], StringComparison.OrdinalIgnoreCase))).ToArray();
        if (markers.Length != 1) throw new InvalidDataException($"资源包须包含唯一的 {marker}。");
        var prefix = markers[0].FullName[..^markers[0].Name.Length];
        var target = Path.Combine(packageDirectory, "resources");
        var backup = Path.Combine(packageDirectory, "resources.previous");
        var stage = Path.Combine(packageDirectory, ".resources-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
        Directory.CreateDirectory(stage);
        try
        {
            var count = 0;
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length != 0))
            {
                var relative = entry.FullName[prefix.Length..];
                var parts = relative.Split('/', '\\');
                if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                    p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException($"无效的资源路径 {relative}");
                if (textures && !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(stage, Path.Combine(parts));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest);
                count++;
            }
            var markerPath = Path.Combine(stage, marker);
            if (textures)
            {
                var actual = File.ReadAllText(markerPath).Trim();
                if (actual.Length == 0 || release != null && actual != release || count < 2)
                    throw new InvalidDataException("贴图版本或 PNG 内容与发布包不符。");
            }
            else
            {
                using var dict = JsonDocument.Parse(File.ReadAllText(markerPath));
                if (dict.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(marker);
                // Translation ZIPs often omit the separately distributed font bundle and user overrides.
                if (Directory.Exists(target)) CopyMissing(target, stage);
                var version = release;
                var versions = archive.Entries.Where(e => e.Name == "version.txt").ToArray();
                if (version == null && versions.Length == 1)
                {
                    using var reader = new StreamReader(versions[0].Open());
                    version = reader.ReadToEnd().Trim();
                }
                File.WriteAllText(Path.Combine(stage, "version.txt"), version ?? "");
            }
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(stage, target); }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }
            return count;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static void CopyMissing(string source, string dest)
    {
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"资源目录包含链接，请先单独处理 {item.FullName}");
            var path = Path.Combine(dest, item.Name);
            if (item is DirectoryInfo dir) { Directory.CreateDirectory(path); CopyMissing(dir.FullName, path); }
            else if (!File.Exists(path)) File.Copy(item.FullName, path);
        }
    }
}
