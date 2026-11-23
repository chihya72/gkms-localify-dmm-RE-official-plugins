using System.Runtime.InteropServices;
using System.Text.Json;
using Gkms.Sdk;

namespace Gkms.Resources;

internal static class AutoResourceUpdate
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);

    public static void Start(IPluginContext context, IResourceUpdateService updater, string legacyFlag, Action? reload)
    {
        if (context is not IPluginRuntime runtime) return;
        bool enabled;
        try
        {
            var legacy = false;
            var file = Path.Combine(context.DataDirectory, "config.json");
            if (File.Exists(file))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                legacy = json.RootElement.TryGetProperty(legacyFlag, out var flag) && flag.ValueKind == JsonValueKind.True;
            }
            enabled = context.Option("autoCheckUpdates", legacy);
        }
        catch (Exception e) { context.Log("无法读取自动更新设置: " + e.Message); return; }
        if (!enabled) return;
        var token = runtime.Stopping;
        _ = Task.Run(async () =>
        {
            try
            {
                var release = await updater.CheckAsync(context.DataDirectory, context.Option<string?>("updateSource", null), token);
                if (release.IsCurrent || token.IsCancellationRequested) return;
                // The worker owns the native prompt so downloads never block the Unity loop.
                if (MessageBoxW(IntPtr.Zero, $"{release.InstalledVersion ?? "—"} → {release.Version ?? "ZIP"}\n\n{release.Description}\n\n现在更新资源？",
                    "Gakumas Localify · " + legacyFlag, 0x144) != 6 || token.IsCancellationRequested) return;
                var progress = new LogProgress(context);
                var count = await updater.InstallAsync(context.DataDirectory, release, progress, token);
                runtime.Enqueue(() =>
                {
                    if (reload == null) context.Log($"已更新 {count} 个资源文件。启动时加载的词典下次启动生效；剧情文件在后续剧情请求时读取。");
                    else { reload(); context.Log($"已更新并重载 {count} 个资源文件。"); }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) { context.Log("资源更新失败: " + e.GetBaseException().Message); }
        });
    }
    private sealed class LogProgress(IPluginContext context) : IProgress<ResourceProgress>
    {
        private long previous;
        public void Report(ResourceProgress value)
        {
            if (value.Received - previous < 4 * 1024 * 1024 && value.Received != value.Total) return;
            previous = value.Received;
            context.Log($"资源下载: {value.Received / 1024} KiB / {(value.Total.HasValue ? (value.Total / 1024).ToString() : "?")} KiB");
        }
    }
}
