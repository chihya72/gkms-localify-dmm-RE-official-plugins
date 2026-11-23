using System.Text.Json;

namespace Gkms.Plugins.Localization;

public sealed class SubtitleConfig
{
    public bool Enabled { get; init; } = true;
    public bool ShowCueWhenMissing { get; init; }
    public float FontSize { get; init; }
    public float LeftOffset { get; init; }
    public float MinWidth { get; init; }
    public float MaxWidth { get; init; }
    public float[] Padding { get; init; } = null!;
    public Dictionary<string, string> Lines { get; } = new(StringComparer.Ordinal);

    public static SubtitleConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        float Number(string name, float fallback) => root.TryGetProperty(name, out var v) ? v.GetSingle() : fallback;
        var max = Number("bubbleWidth", 520);
        var config = new SubtitleConfig
        {
            Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            ShowCueWhenMissing = root.TryGetProperty("showCueWhenMissing", out var missing) && missing.GetBoolean(),
            FontSize = Number("fontSize", 32), LeftOffset = Number("leftOffset", 40), MaxWidth = max,
            MinWidth = Number("minBubbleWidth", Math.Min(424, max)),
            Padding = root.TryGetProperty("textPadding", out var padding)
                ? padding.EnumerateArray().Select(v => v.GetSingle()).ToArray() : new[] { 40f, 40f }
        };
        if (config.Padding.Length != 2 || config.Padding.Any(n => !float.IsFinite(n) || n < 0) ||
            !float.IsFinite(config.LeftOffset) || !float.IsFinite(config.FontSize) || config.FontSize <= 0 ||
            !float.IsFinite(max) || !float.IsFinite(config.MinWidth) || config.MinWidth > max || config.MinWidth <= config.Padding[0])
            throw new InvalidDataException("语音气泡的宽度、字号或文字边距无效。");
        if (root.TryGetProperty("subtitles", out var entries))
        {
            if (entries.ValueKind == JsonValueKind.Object)
                foreach (var p in entries.EnumerateObject()) config.Lines[p.Name] = p.Value.GetString() ?? "";
            else
                foreach (var entry in entries.EnumerateArray())
                    config.Lines[entry.GetProperty("voiceAssetId").GetString()!] = entry.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        }
        return config;
    }

    public string? Lookup(string cue) => Lines.TryGetValue(cue, out var line) ? line :
        ShowCueWhenMissing ? "语音字幕测试\n" + (cue.IndexOf("_home_", StringComparison.Ordinal) is var at && at >= 0 ? cue[(at + 1)..] : cue) : null;
}
