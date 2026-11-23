using Gkms.Sdk;

namespace Gkms.Plugins.Capture;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    private readonly PluginComponents components = new();
    public IReadOnlyList<string> Warnings => components.Warnings;
    public void Start(IPluginContext context)
    {
        var text = context.Option("text", true); var textures = context.Option("textures", true);
        if (text) components.Start(context, new TextCapture(), "原文采集");
        if (textures) components.Start(context, new TextureCapture(), "贴图导出");
        if ((text || textures) && components.Count == 0) throw new InvalidOperationException("没有可用的素材采集入口。");
    }
    public void Dispose() => components.Dispose();
}
