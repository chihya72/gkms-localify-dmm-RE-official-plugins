using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.CardCutIn;

public sealed class Plugin : ILocalifyPlugin
{
    private static MethodInfo master = null!, rows = null!, cardId = null!;
    private static object? source, sort;
    private static Dictionary<string, List<(string Id, int Rank)>> choices = new(StringComparer.Ordinal);

    public void Start(IPluginContext context)
    {
        const string game = "Assembly-CSharp";
        master = context.Require(game, "Campus.Common.Master.MasterManager", "get_ProduceCardSkinMaster");
        rows = master.ReturnType.GetMethods(GameMethods.Declared)
            .Single(m => m.Name == "GetAllWithSortByKey" && m.GetParameters().Length == 1);
        sort = Interop.New(rows.GetParameters()[0].ParameterType);
        const string card = "Campus.InGame.Card.ExamCardData";
        cardId = context.Require(game, card, "get_Id");
        context.Patch(context.Require(game, card, "get_ProduceCardSkinId"), postfix: GameMethods.Handler<Plugin>(nameof(SelectSkin)));
    }

    private static void Index(object table)
    {
        var list = rows.Invoke(table, new[] { sort });
        if (list == null) return;
        var count = Convert.ToInt32(Interop.TypeOf(list).GetProperty("Count")!.GetValue(list));
        var item = Interop.TypeOf(list).GetProperty("Item")!;
        var next = new Dictionary<string, List<(string Id, int Rank)>>(StringComparer.Ordinal);
        var valid = 0;
        for (var i = 0; i < count; i++)
        {
            if (item.GetValue(list, new object[] { i }) is not { } row) continue;
            var type = Interop.TypeOf(row);
            var rank = Convert.ToInt32(type.GetProperty("CutInType")!.GetValue(row));
            if (type.GetProperty("ProduceCardId")!.GetValue(row) is not string card ||
                type.GetProperty("Id")!.GetValue(row) is not string skin) continue;
            valid++;
            if (rank <= 0) continue;
            if (!next.TryGetValue(card, out var variants)) next[card] = variants = new();
            variants.Add((skin, rank));
        }
        // DMM's strict `cutIn > slot.first` retains the first row on ties.
        foreach (var key in next.Keys.ToArray()) next[key] = next[key].OrderByDescending(v => v.Rank).ToList();
        choices = next;
        source = valid == 0 ? null : table;
    }

    public static void SelectSkin(object __instance, ref string? __result)
    {
        if (!string.IsNullOrEmpty(__result) || cardId.Invoke(__instance, null) is not string id ||
            master.Invoke(null, null) is not { } table) return;
        if (!Equals(table, source)) Index(table);
        if (!choices.TryGetValue(id, out var variants)) return;
        // Match the DMM plugin: choose the highest cut-in even when its skin is not owned.
        __result = variants[0].Id;
    }

    public void Dispose() { source = null; choices.Clear(); }
}
