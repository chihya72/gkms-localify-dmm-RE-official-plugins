using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    private readonly PluginComponents started = new();
    private readonly HashSet<string> missing = new();
    public IReadOnlyList<string> Warnings => missing.Concat(started.Warnings).ToArray();

    public void Start(IPluginContext context)
    {
        var root = ResourceUpdater.ResourceDirectory;
        context.Log($"汉化资源目录: {root}");
        var resources = new TextDictionary(root);
        foreach (var warning in resources.Warnings) { missing.Add(warning); context.Log(warning); }
        StartFeature("界面翻译", new KeyedText(resources));
        StartFeature("通用文本", new GenericText(resources));
        StartFeature("剧情翻译", new StoryText());
        StartFeature("语音字幕", new VoiceSubtitles(), File.Exists(VoiceSubtitles.ConfigFile(root)));

        StartFeature("主数据翻译", new MasterText(resources));

        if (started.Count == 0) throw new InvalidOperationException("没有可用的文本汉化功能。");

        Gkms.Resources.AutoResourceUpdate.Start(context, new ResourceUpdater(), "useAPIAssets", reload: null);

        void StartFeature(string name, ILocalifyPlugin feature, bool installed = true)
        {
            var warning = $"{name} 缺少对应资源，已跳过。";
            if (!installed) { missing.Add(warning); context.Log(warning); return; }
            started.Start(context, feature, name);
        }
    }

    public void Dispose() { started.Dispose(); missing.Clear(); }
}
