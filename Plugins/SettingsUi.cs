using Gkms.Sdk;

namespace Gkms.Plugins;

// Each built-in assembly exposes the same settings entry contract as a third-party plugin.
public sealed class SettingsUi : SchemaSettingsUi
{
    public override void DrawResourceSettings()
    {
        var source = Context.Fields.FirstOrDefault(field => field.Key == "updateSource");
        if (source?.Default.ValueKind == System.Text.Json.JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(source.Default.GetString()))
        {
            var label = Context.Language switch
            {
                "zh-TW" => "原版預設地址：",
                "en" => "Original default URL: ",
                "ja" => "オリジナルの既定 URL: ",
                _ => "原版默认地址："
            };
            PluginUiText.Wrapped(label + source.Default.GetString());
        }
        base.DrawResourceSettings();
    }
}
