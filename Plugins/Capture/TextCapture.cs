using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Gkms.Sdk;

namespace Gkms.Plugins.Capture;

internal sealed class TextCapture : ILocalifyPlugin, IPluginDiagnostics
{
    private static readonly HashSet<string> warnings = new();
    public IReadOnlyList<string> Warnings => warnings.ToArray();
    private static readonly object sync = new();
    private static readonly HashSet<(string, string)> seen = new();
    private static StreamWriter? writer;
    private static IPluginContext? session;
    private static bool failed;
    private static string root = "";
    private static int fileIndex;
    private static MethodInfo? key;
    private static readonly Dictionary<string, Dictionary<string, string>> dictionaries = new();
    private static DmmTextDictionary? dictionary;
    private static string? dictionaryRoot;
    private static int dictionaryRevision;
    private Timer? flush;
    public void Start(IPluginContext context)
    {
        root = Directory.CreateDirectory(Path.Combine(context.Services().PackageDirectory, "output")).FullName;
        context.Log("采集输出目录: " + root);
        fileIndex = context.Option("fileIndex", 0);
        writer = File.AppendText(Path.Combine(root, "observed.jsonl"));
        writer.AutoFlush = true;
        failed = false;
        session = context;
        context.Patch(context.Require("quaunity-ui.Runtime", "Qua.UI.I18n", "SetValue", "System.String", "System.String"),
            GameMethods.Handler<TextCapture>(nameof(KeyValue)), priority: 800);
        void Bind(MethodInfo? method, string handler)
        {
            if (method == null) return;
            try { context.Patch(method, GameMethods.Handler<TextCapture>(handler), priority: 800); }
            catch (NotSupportedException error) { var message = $"跳过不兼容采集入口 {method.Name}: {error.Message}"; warnings.Add(message); context.Log(message); }
        }
        const string tmp = "Unity.TextMeshPro", type = "TMPro.TMP_Text";
        Bind(context.Find(tmp, type, "set_text", "System.String"), nameof(Text));
        Bind(context.Find(tmp, type, "SetText", "System.String"), nameof(Text));
        Bind(context.Find(tmp, type, "SetText", "System.String", "System.Boolean"), nameof(Text));
        Bind(context.Find(tmp, type, "PopulateTextBackingArray", "System.String", "System.Int32", "System.Int32"), nameof(Slice));
        Bind(context.Find("UnityEngine.UI", "UnityEngine.UI.Text", "set_text", "System.String"), nameof(Text));
        Bind(context.Find("UnityEngine.UIElementsModule", "UnityEngine.UIElements.TextField", "set_value", "System.String"), nameof(Text));
        var chars = context.FindType(tmp, type)?.GetMethods(GameMethods.Declared).FirstOrDefault(m => m.Name == "SetCharArray" &&
            m.GetParameters() is [{ ParameterType: var a }, { ParameterType: var b }, { ParameterType: var c }] &&
            (a.IsArray && a.GetElementType() == typeof(char) || a.IsGenericType && a.GenericTypeArguments.SequenceEqual(new[] { typeof(char) })) && b == typeof(int) && c == typeof(int));
        Bind(chars, nameof(Characters));
        Bind(context.Find(tmp, "TMPro.TextMeshProUGUI", "Awake"), nameof(Initial));
        Bind(context.Find(tmp, "TMPro.TextMeshProUGUI", "OnPreRenderCanvas"), nameof(Initial));
        Bind(context.Find(tmp, "TMPro.TextMeshPro", "OnPreRenderObject"), nameof(Initial));
        key = context.Find("UnityEngine.InputLegacyModule", "UnityEngine.Input", "GetKeyDown", "System.String");
        if (key != null) Bind(context.Find("campus-submodule.Runtime", "Campus.Common.CampusQualityManager", "Update"), nameof(ChangeFile));
        // Discover the text package by ID, since ZIP installation can choose a new package folder.
        var packages = Path.GetDirectoryName(context.Services().PackageDirectory)!;
        foreach (var folder in Directory.EnumerateDirectories(packages))
        {
            var manifest = Path.Combine(folder, "plugin.json");
            if (!File.Exists(manifest)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!json.RootElement.TryGetProperty("id", out var id) || id.GetString() != "localization") continue;
            var local = Path.Combine(folder, "resources");
            if (!Directory.Exists(local)) continue;
            dictionary = new DmmTextDictionary(local); dictionaryRoot = local;
            dictionaryRevision = LiveGameState.TextResourcesRevision;
        }
        dictionary ??= new DmmTextDictionary(Path.Combine(root, "unused-resource-path"));
        flush = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }
    public static void KeyValue(string __0, string __1) => Record("i18n", __0, __1);
    public static void Text(string __0) => Record("text", __0, __0);
    public static void Slice(string __0, int __1, int __2)
    { if (__0 != null && __1 >= 0 && __2 >= 0 && __1 <= __0.Length - __2) Text(__0.Substring(__1, __2)); }
    public static void Characters(object __0, int __1, int __2)
    {
        if (__0 == null || __1 < 0 || __2 <= 0) return;
        var values = __0 is char[] array ? array : Interop.Items(__0).Select(Convert.ToChar).ToArray();
        if (__1 <= values.Length - __2) Text(new string(values, __1, __2));
    }
    public static void Initial(object __instance)
    { if (Interop.TypeOf(__instance).GetProperty("text")?.GetValue(__instance) is string text) Text(text); }
    public static void ChangeFile()
    {
        if (dictionaryRoot != null && dictionaryRevision != LiveGameState.TextResourcesRevision)
        {
            try { dictionary = new DmmTextDictionary(dictionaryRoot); dictionaryRevision = LiveGameState.TextResourcesRevision; seen.Clear(); }
            catch (Exception error) { session?.Log("采集词典重载失败: " + error.GetBaseException().Message); }
        }
        var delta = (key?.Invoke(null, new object[] { "[+]" }) is true ? 1 : 0) - (key?.Invoke(null, new object[] { "[-]" }) is true ? 1 : 0);
        if (delta == 0) return;
        lock (sync) { Flush(); fileIndex += delta; seen.RemoveWhere(p => p.Item1 == "text"); session?.Log($"原文采集分组: {fileIndex}"); }
    }
    private static void Add(string file, string key, string value)
    {
        if (!dictionaries.TryGetValue(file, out var map)) dictionaries.Add(file, map = new(StringComparer.Ordinal));
        map.TryAdd(key, value);
    }
    private static string FileName(string name, string extension = ".json") => name + (fileIndex == 0 ? "" : "_" + fileIndex) + extension;
    private static void Record(string kind, string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (sync)
        {
            if (failed || writer == null || !seen.Add((kind, key))) return;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new { kind, key, value }));
                if (kind == "i18n") { if (dictionary?.HasKey(key) != true) Add("localization.json", key, value); return; }
                foreach (var (file, text) in dictionary!.Missing(value))
                {
                    if (file == "orig") Add(FileName("generic_orig"), text, text);
                    else if (file == "fmt") Add(FileName("generic", ".fmt.json"), text, text);
                    else Add(FileName("generic"), "[__split__]" + text, "[__split__]" + text);
                }
            }
            catch (IOException error) { failed = true; session?.Log(error.Message); }
        }
    }
    private static void Flush()
    {
        lock (sync)
        {
            if (failed || writer == null) return;
            try
            {
                foreach (var (name, additions) in dictionaries)
                {
                    var file = Path.Combine(root, name);
                    var map = File.Exists(file) ? Interop.ReadDictionary(file) : new Dictionary<string, string>();
                    foreach (var entry in additions) map.TryAdd(entry.Key, entry.Value);
                    var temp = file + ".tmp";
                    try { File.WriteAllText(temp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, file, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                dictionaries.Clear();
            }
            catch (Exception error) { session?.Log(error.GetBaseException().Message); }
        }
    }
    public void Dispose()
    {
        flush?.Dispose(); flush = null;
        lock (sync) { Flush(); writer?.Dispose(); writer = null; session = null; seen.Clear(); warnings.Clear(); dictionary = null; dictionaryRoot = null; dictionaries.Clear(); key = null; }
    }
}
