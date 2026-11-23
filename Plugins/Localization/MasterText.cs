using System.Collections.Concurrent;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

internal sealed class MasterText(TextDictionary resources) : ILocalifyPlugin, IPluginDiagnostics
{
    private static readonly ConcurrentDictionary<Type, DmmMasterTable> tables = new();
    private static readonly ConcurrentDictionary<Type, byte> reported = new();
    private static IPluginContext? session;
    private readonly List<string> warnings = new();
    // v2 resource packs include schemas for both old and current clients. These old
    // tables were replaced, not renamed in place; only retire them when every current
    // replacement is present and has actually been bound successfully.
    private static readonly Dictionary<string, string[]> replacedSchemas = new()
    {
        ["ProduceDescription"] = new[] { "ProduceDescriptionLabel", "ProduceDescriptionSwap" },
        ["ProduceDescriptionProduceCardGrowEffectType"] = new[] { "ProduceDescriptionProduceCardGrowEffect" },
        ["ProduceDescriptionProduceEffectType"] = new[] { "ProduceDescriptionProduceEffect" },
        ["ProduceDescriptionProduceExamEffectType"] = new[] { "ProduceDescriptionExamEffect" },
        ["ProduceDescriptionProducePlanType"] = new[] { "ProduceDescriptionProducePlan" }
    };
    public IReadOnlyList<string> Warnings => warnings;
    public void Start(IPluginContext context)
    {
        session = context;
        context.PatchAdvanced(context.RequireMessageReader(),
            finalizer: GameMethods.Handler<MasterText>(nameof(MessageParsed)));
        LoadResources(context);
    }
    private void LoadResources(IPluginContext context)
    {
        tables.Clear(); reported.Clear();
        var messages = new List<string>();
        var parsed = resources.MasterTables;
        var skipped = new List<string>();
        var missingTypes = new HashSet<string>();
        foreach (var (file, table) in parsed)
        {
            var type = context.FindType("Assembly-CSharp", "Campus.Common.Proto.Client.Master." + Path.GetFileNameWithoutExtension(file));
            if (type == null) { var name = Path.GetFileNameWithoutExtension(file); skipped.Add(name); missingTypes.Add(name); continue; }
            tables[type] = table;
        }
        var activeNames = tables.Keys.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var retired = skipped.Where(name => missingTypes.Contains(name) && replacedSchemas.TryGetValue(name, out var replacements) && replacements.All(activeNames.Contains)).ToArray();
        foreach (var name in retired)
        { skipped.Remove(name); context.Log($"兼容旧客户端的资源表 {name} 不适用于当前客户端；已接入当前表 {string.Join(", ", replacedSchemas[name])}。"); }
        foreach (var name in skipped) context.Log($"主数据未接入 {name}: 当前客户端没有兼容的类型或反序列化入口。");
        if (skipped.Count != 0) messages.Add("未接入的资源主数据表: " + string.Join(", ", skipped));
        warnings.Clear(); warnings.AddRange(messages); foreach (var message in messages) context.Log(message);
        context.Log($"已接入 {tables.Count}/{parsed.Count - retired.Length} 张当前主数据表（资源包另含 {retired.Length} 张旧版兼容表）。");
    }
    public static void MessageParsed(object? __0, Exception? __exception)
    {
        // DMM completes game parsing first; failed parses are never translated or restored.
        if (__0 == null || __exception != null) return;
        if (!tables.TryGetValue(Interop.TypeOf(__0), out var table)) return;
        try { table.Apply(__0); }
        catch (Exception error)
        {
            if (reported.TryAdd(Interop.TypeOf(__0), 0))
                session?.Log($"{Interop.TypeOf(__0).Name}: {error.GetBaseException().Message}");
        }
    }
    public void Dispose() { tables.Clear(); reported.Clear(); warnings.Clear(); session = null; }
}
