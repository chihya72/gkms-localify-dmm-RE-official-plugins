using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.LiveUnlock;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    public IReadOnlyList<string> Warnings => components.Warnings;
    [ThreadStatic] private static int meishiDepth;
    [ThreadStatic] private static bool augmenting;
    private static IPluginContext? context;
    private static Type masters = null!;
    private static readonly Dictionary<string, Type> collectionTypes = new();
    private static readonly Dictionary<object, int> populated = new();
    private static readonly List<(object Collection, string Id, object Row)> inserted = new();
    private static readonly List<(object List, object Row)> appended = new();
    private readonly PluginComponents components = new();

    public void Start(IPluginContext ctx)
    {
        context = ctx;
        const string assembly = "Assembly-CSharp";
        if (ctx.Option("live", true)) { BindLive(ctx); components.Start(ctx, new LiveLyrics(), "Live 歌词初始开关"); }
        if (!ctx.Option("costumes", true)) return;
        masters = ctx.RequireType(assembly, "Campus.Common.Master.MasterManager");
        BindCostumes(ctx);
    }
    private static void BindLive(IPluginContext ctx)
    {
        const string assembly = "Assembly-CSharp";
        var skins = ctx.RequireType(assembly, "Campus.Common.User.UserIdolCardSkinCollection");
        ctx.Patch(skins.GetMethods(Interop.Members).Single(m => m.Name == "Exists" && m.GetParameters().Length == 1),
            postfix: GameMethods.Handler<Plugin>(nameof(OwnsLive)));
        var meishi = ctx.RequireType(assembly, "Campus.OutGame.MeishiBasePIdolTabPageModel");
        var ctor = meishi.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(meishi.FullName, ".ctor()");
        ctx.PatchAdvanced(ctor, GameMethods.Handler<Plugin>(nameof(EnterMeishi)), finalizer: GameMethods.Handler<Plugin>(nameof(ExitMeishi)));
        var thumbnail = ctx.RequireType(assembly, "Campus.OutGame.PictureBook.PictureBookLiveThumbnailView");
        ctx.Patch(thumbnail.GetMethods(GameMethods.Declared).Single(m => m.Name == "SetReleaseDataAsync" && m.GetParameters().Length == 6),
            GameMethods.Handler<Plugin>(nameof(Release)));
        ctx.Patch(thumbnail.GetMethods(GameMethods.Declared).Single(m => m.Name == "SetUnReleaseDataAsync" && m.GetParameters().Length == 4),
            GameMethods.Handler<Plugin>(nameof(Unreleased)));
    }
    private static void BindCostumes(IPluginContext ctx)
    {
        const string assembly = "Assembly-CSharp";
        var hooked = new HashSet<MethodInfo>();
        foreach (var name in new[] { "UserCostumeCollection", "UserCostumeHeadCollection" })
        {
            var collection = ctx.RequireType(assembly, "Campus.Common.User." + name);
            collectionTypes[name] = collection;
            foreach (var method in collection.GetMethods(Interop.Members).Where(m =>
                m.Name == "GetAll" && m.GetParameters().Length == 0 || m.Name == "FindBy" && m.GetParameters().Length == 1)
                .GroupBy(m => m.Name).Select(g => g.OrderBy(m => m.DeclaringType == collection ? 0 : 1).First()))
                if (hooked.Add(method))
                {
                    if (method.Name == "GetAll") ctx.Patch(method, GameMethods.Handler<Plugin>(nameof(Populate)));
                    else ctx.Patch(method, postfix: GameMethods.Handler<Plugin>(nameof(FindBy)));
                }
            if (!collection.GetMethods(Interop.Members).Any(m => m.Name == "Remove" && m.GetParameters().Length == 1))
                throw new MissingMethodException(collection.FullName, "Remove");
        }
        ctx.Patch(ctx.Require(assembly, "Campus.Photography.PhotographyCostumeSettingListItemModel", "get_IsDisabled"),
            postfix: GameMethods.Handler<Plugin>(nameof(Selectable)));
        foreach (var name in new[] { "GetCostumes", "GetCostumeHeads" })
            ctx.Patch(ctx.Require(assembly, "Campus.Photography.PhotographyCostumeSettingData", name, "System.String"),
                postfix: GameMethods.Handler<Plugin>(nameof(Photography)));
    }

    public static void EnterMeishi(out bool __state) { meishiDepth++; __state = true; }
    public static Exception? ExitMeishi(Exception? __exception, bool __state)
    { if (__state) meishiDepth--; return __exception; }
    public static void OwnsLive(object? __0, ref bool __result)
    {
        var id = __0?.ToString();
        if (meishiDepth == 0 && id != null && (id.StartsWith("music", StringComparison.Ordinal) || id.StartsWith("i_card-skin", StringComparison.Ordinal)))
            __result = true;
    }
    public static void Release(ref bool __2, ref bool __4) => __2 = __4 = true;
    public static void Unreleased(ref bool __1) => __1 = true;
    public static void Selectable(ref bool __result) => __result = false;
    public static void Collection(object? __result) { if (__result != null) Populate(__result); }

    private static object[] Rows(string name)
    {
        var master = masters.GetProperty(name, Interop.Members)?.GetValue(null);
        return master == null ? Array.Empty<object>() : Interop.Items(Interop.Call(master, "GetAllWithSortByKey", 0)).ToArray();
    }
    private static string Text(object row, string name) => Interop.Get(row, name)?.ToString() ?? "";

    private static HashSet<string> CostumeIds(bool head)
    {
        var skins = Rows("IdolCardSkinMaster");
        var bodies = skins.Select(s => Text(s, "CostumeId")).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal);
        var result = head ? skins.Select(s => Text(s, "CostumeHeadId")).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal) : new(bodies, StringComparer.Ordinal);
        var photo = Rows("CostumePhotoGroupMaster").SelectMany(r => Interop.Items(Interop.Get(r, "CostumeIds")))
            .Select(id => id.ToString()!).ToHashSet(StringComparer.Ordinal);
        if (photo.Count == 0) return result;
        // Preserve the original release-time filter; future rows may have no downloadable assets.
        var rows = Rows("CostumeMaster");
        var groups = rows.Where(r => bodies.Contains(Text(r, "Id")) || photo.Contains(Text(r, "Id")))
            .Select(r => Text(r, "CostumeColorGroupId")).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in rows)
        {
            var id = Text(row, "Id");
            var time = Convert.ToInt64(Interop.Get(row, "ViewStartTime"));
            if (time > 4_000_000_000L) time /= 1000;
            if (time > now) continue;
            if (!photo.Contains(id) && !groups.Contains(Text(row, "CostumeColorGroupId")) && !(time > 0 && time <= now)) continue;
            if (!head) result.Add(id);
            else foreach (var field in new[] { "CostumeHeadId", "DefaultCostumeHeadId" })
                if (Text(row, field) is { Length: > 0 } headId) result.Add(headId);
        }
        return result;
    }

    public static void Populate(object __instance)
    {
        if (augmenting) return;
        augmenting = true;
        try
        {
            // A shared generic IL2CPP method can expose a base wrapper even for a derived native collection.
            var name = Interop.TypeOf(__instance).Name;
            if (!collectionTypes.ContainsKey(name))
            {
                var nativeType = Interop.TypeOf(__instance).GetMethod("GetIl2CppType", Type.EmptyTypes)?.Invoke(__instance, null);
                name = nativeType == null ? name : Interop.Get(nativeType, "Name")?.ToString() ?? name;
                if (!collectionTypes.TryGetValue(name, out var type)) return;
                __instance = Interop.Cast(__instance, type);
            }
            var count = Convert.ToInt32(Interop.Get(__instance, "Count"));
            if (populated.TryGetValue(__instance, out var previous) && previous == count) return;
            var head = name == "UserCostumeHeadCollection";
            var idField = head ? "CostumeHeadId" : "CostumeId";
            var rows = Interop.Items(Interop.Call(__instance, "GetAllList", new object?[] { null })).ToArray();
            if (rows.Length == 0) return;
            var ids = CostumeIds(head);
            if (ids.Count == 0) return;
            foreach (var id in ids)
            {
                if (Interop.Call(__instance, "Exists", id) is true) continue;
                var clone = Interop.Call(rows[0], "Clone")!;
                Interop.Set(clone, idField, id);
                Interop.Call(__instance, "Add", clone);
                inserted.Add((__instance, id, clone));
            }
            populated[__instance] = Convert.ToInt32(Interop.Get(__instance, "Count"));
        }
        catch (Exception error) { context?.Log(error.GetBaseException().Message); }
        finally { augmenting = false; }
    }

    // Hook.cpp UserCostumeCollection_FindBy first executes the predicate call,
    // then returns the full user list augmented only from IdolCardSkinMaster.
    public static void FindBy(object __instance, ref object __result)
    {
        var name = Interop.TypeOf(__instance).Name;
        if (name is not ("UserCostumeCollection" or "UserCostumeHeadCollection")) return;
        var list = Interop.Call(__instance, "GetAllList", new object?[] { null });
        if (list == null) return;
        var rows = Interop.Items(list).ToArray();
        if (rows.Length != 0)
        {
            var field = name == "UserCostumeHeadCollection" ? "CostumeHeadId" : "CostumeId";
            var ids = rows.Select(r => Text(r, field)).ToHashSet(StringComparer.Ordinal);
            foreach (var skin in Rows("IdolCardSkinMaster"))
            {
                var id = Text(skin, field);
                if (id.Length == 0 || !ids.Add(id)) continue;
                var clone = Interop.Call(rows[0], "Clone")!;
                Interop.Set(clone, field, id); Interop.Call(list, "Add", clone); appended.Add((list, clone));
            }
        }
        __result = list;
    }

    public static void Photography(object __result, string __0, MethodBase __originalMethod)
    {
        if (__result == null || string.IsNullOrEmpty(__0)) return;
        try
        {
            var head = __originalMethod.Name == "GetCostumeHeads";
            var existing = Interop.Items(__result).Select(r => Text(r, "Id")).ToHashSet(StringComparer.Ordinal);
            foreach (var skin in Rows("IdolCardSkinMaster"))
            {
                if (!Text(skin, "IdolCardId").StartsWith("i_card-" + __0, StringComparison.Ordinal)) continue;
                var item = Interop.Call(skin, head ? "GetCostumeHead" : "GetCostume");
                if (item != null && Text(item, "Id") is { Length: > 0 } id && existing.Add(id)) Interop.Call(__result, "Add", item);
            }
        }
        catch (Exception error) { context?.Log(error.GetBaseException().Message); }
    }

    public void Dispose()
    {
        components.Dispose();
        foreach (var (list, row) in appended) Interop.Call(list, "Remove", row);
        appended.Clear();
        augmenting = true;
        foreach (var (collection, id, row) in inserted.AsEnumerable().Reverse())
        {
            try
            {
                var current = Interop.Call(collection, "FindById", id);
                // A later server update may have replaced our temporary row with a real purchase.
                if (SameInstance(current, row)) Interop.Call(collection, "Remove", row);
            }
            catch (Exception error) { context?.Log(error.GetBaseException().Message); }
        }
        inserted.Clear(); populated.Clear(); collectionTypes.Clear(); context = null; augmenting = false; meishiDepth = 0;
    }
    private static bool SameInstance(object? left, object right) => ReferenceEquals(left, right) || left != null &&
        Interop.PointerOf(left) is var p && p != IntPtr.Zero && p == Interop.PointerOf(right);
}
