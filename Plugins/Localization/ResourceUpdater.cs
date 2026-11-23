using Gkms.Resources;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

public sealed class ResourceUpdater : IResourceUpdateService
{
    // The loader uses the actual installed package, including packages installed under a custom folder name.
    internal static string PackageDirectory => Path.GetDirectoryName(typeof(ResourceUpdater).Assembly.Location)!;
    internal static string ResourceDirectory => Path.Combine(PackageDirectory, "resources");
    public async Task<int> UpdateAsync(string dataDirectory, string? source) =>
        await InstallAsync(dataDirectory, await CheckAsync(dataDirectory, source));
    public Task<ResourceRelease> CheckAsync(string dataDirectory, string? source, CancellationToken cancellationToken = default) =>
        ArchiveUpdate.CheckAsync(dataDirectory, PackageDirectory, source, false, "useAPIAssetsURL", "https://uma.chinosk6.cn/api/gkms_trans_data", cancellationToken);
    public Task<int> InstallAsync(string dataDirectory, ResourceRelease release, IProgress<ResourceProgress>? progress = null,
        CancellationToken cancellationToken = default) => ArchiveUpdate.InstallAsync(PackageDirectory, release, false, progress, cancellationToken);
}
