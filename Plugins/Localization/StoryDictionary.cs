using System.Text.RegularExpressions;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

internal static class StoryDictionary
{
    private static readonly Regex Message = new(@"\[message text=(.*?)(?= [A-Za-z_][A-Za-z0-9_]*=|\]$)", RegexOptions.Compiled);
    private static readonly Regex Ruby = new(@"<r\\?=([^>]*)>(.*?)</r>", RegexOptions.Compiled | RegexOptions.Singleline);
    public static void Read(string root, Action<string, string> add)
    {
        var directory = Path.Combine(root, "resource");
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            ReadText(File.ReadAllText(file), add);
    }
    public static void ReadText(string content, Action<string, string> add)
    {
            foreach (var line in content.Split('\n'))
            {
                var match = Message.Match(line);
                if (!match.Success) continue;
                var text = match.Groups[1].Value.Replace(@"\r\n", "\n").Replace(@"\n", "\n").Replace(@"\=", "=");
                if (!Ruby.IsMatch(text)) continue;
                void Register(string original, string translated)
                { add(original, translated); }
                foreach (Match ruby in Ruby.Matches(text)) Register(ruby.Groups[1].Value, ruby.Groups[2].Value);
                var original = Ruby.Replace(text, "$1");
                var translated = Ruby.Replace(text, "$2");
                Register(original, translated);
                Register(original, text);
            }
    }
}
