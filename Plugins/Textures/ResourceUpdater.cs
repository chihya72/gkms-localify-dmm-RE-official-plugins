using Gkms.Resources;
using Gkms.Sdk;

namespace Gkms.Plugins.Textures;

public sealed class ResourceUpdater : IResourceUpdateService
{
    internal static string PackageDirectory => Path.GetDirectoryName(typeof(ResourceUpdater).Assembly.Location)!;
    internal static string ResourceDirectory => Path.Combine(PackageDirectory, "resources");
    public async Task<int> UpdateAsync(string dataDirectory, string? source) =>
        await InstallAsync(dataDirectory, await CheckAsync(dataDirectory, source));
    public Task<ResourceRelease> CheckAsync(string dataDirectory, string? source, CancellationToken cancellationToken = default) =>
        ArchiveUpdate.CheckAsync(dataDirectory, PackageDirectory, source, true, "useAPITextureAssetsURL", "https://texture.gakumas.cn/api/gkms_texture_data", cancellationToken);
    public Task<int> InstallAsync(string dataDirectory, ResourceRelease release, IProgress<ResourceProgress>? progress = null,
        CancellationToken cancellationToken = default) => ArchiveUpdate.InstallAsync(PackageDirectory, release, true, progress, cancellationToken);
}
