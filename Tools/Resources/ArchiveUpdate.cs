using System.IO.Compression;
using System.Text.Json;
using Gkms.Sdk;

namespace Gkms.Resources;

internal static class ArchiveUpdate
{
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GkmsLocalifyRE/0.1");
        return client;
    }
    public static async Task<ResourceRelease> CheckAsync(string dataDirectory, string packageDirectory, string? source, bool textures, string configKey,
        string defaultUrl, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(packageDirectory, "resources", textures ? "texture_version.txt" : "version.txt");
        var installed = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        var profile = Path.Combine(dataDirectory, "re", "profile.json");
        if (string.IsNullOrWhiteSpace(source) && File.Exists(profile))
        {
            using var options = JsonDocument.Parse(File.ReadAllText(profile));
            if (options.RootElement.TryGetProperty("settings", out var settings) &&
                settings.TryGetProperty(textures ? "textures" : "localization", out var plugin) &&
                plugin.TryGetProperty("updateSource", out var value)) source = value.GetString();
        }
        var config = Path.Combine(dataDirectory, "config.json");
        if (string.IsNullOrWhiteSpace(source) && File.Exists(config))
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(config));
            var json = settings.RootElement;
            if (!textures && json.TryGetProperty("useRemoteAssets", out var remote) && remote.ValueKind == JsonValueKind.True &&
                json.TryGetProperty("transRemoteZipUrl", out var zip)) source = zip.GetString();
            else if (json.TryGetProperty(configKey, out var value)) source = value.GetString();
        }
        source = string.IsNullOrWhiteSpace(source) ? defaultUrl : source.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new(source, source, null, installed, "");
            using var client = Client();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var prefix = new byte[4];
            var length = 0;
            while (length < prefix.Length)
            {
                var count = await stream.ReadAsync(prefix.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            // Download endpoints need not end in .zip. Inspect the payload instead of
            // assuming every extensionless URL is a release API.
            if (length == 4 && prefix[0] == 'P' && prefix[1] == 'K' &&
                ((prefix[2] == 3 && prefix[3] == 4) || (prefix[2] == 5 && prefix[3] == 6)))
                return new(source, source, null, installed, "");
            using var json = new MemoryStream();
            json.Write(prefix, 0, length);
            await stream.CopyToAsync(json, cancellationToken).ConfigureAwait(false);
            json.Position = 0;
            try
            {
                using var release = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(source, PackInstaller.SelectReleaseZip(release.RootElement, textures).AbsoluteUri,
                    release.RootElement.GetProperty("tag_name").GetString(), installed,
                    release.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("来源必须返回 ZIP，或包含 tag_name 和 assets 的发布 API 数据。普通网页不受支持。", error); }
        }
        if (uri?.IsFile == true) source = uri.LocalPath;
        if (Directory.Exists(source)) throw new InvalidDataException("更新源不支持本地文件夹，请选择 ZIP 文件。");
        if (!File.Exists(source)) throw new FileNotFoundException("找不到本地 ZIP 文件；在线来源请填写 HTTP(S) 地址。", source);
        using (ZipFile.OpenRead(source)) { }
        return new(source, source, null, installed, "");
    }
    public static async Task<int> InstallAsync(string packageDirectory, ResourceRelease release, bool textures,
        IProgress<ResourceProgress>? progress, CancellationToken cancellationToken)
    {
        var root = packageDirectory;
        using var guard = new FileStream(Path.Combine(root, "resources.edit"), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        var file = release.Download ?? release.Source;
        string? download = null;
        try
        {
            if (Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var client = Client();
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                download = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var dest = File.Create(download))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        received += count;
                        progress?.Report(new(received, response.Content.Headers.ContentLength));
                    }
                }
                file = download;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var zip = ZipFile.OpenRead(file);
            return PackInstaller.Install(packageDirectory, textures, zip, release.Version);
        }
        finally { if (download != null) File.Delete(download); }
    }
}
