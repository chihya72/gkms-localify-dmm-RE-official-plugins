using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.FrameRate;

public sealed class Plugin : ILocalifyPlugin
{
    private static int fps;
    private static MethodInfo? gameSet, gameGet, unitySet, unityGet;
    private static readonly Dictionary<object, float> originals = new();
    private static int? unityOriginal;
    private static bool applying;
    private static IPluginContext? session;
    public void Start(IPluginContext context)
    {
        fps = context.Option("fps", 60);
        if (fps == 0) return;
        session = context;
        const string assembly = "campus-submodule.Runtime", manager = "Campus.Common.CampusQualityManager";
        gameSet = context.Require(assembly, manager, "set_TargetFrameRate", "System.Single");
        gameGet = context.Find(assembly, manager, "get_TargetFrameRate");
        unitySet = context.Find("UnityEngine.CoreModule", "UnityEngine.Application", "set_targetFrameRate", "System.Int32");
        unityGet = context.Find("UnityEngine.CoreModule", "UnityEngine.Application", "get_targetFrameRate");
        unityOriginal = unityGet?.Invoke(null, null) is int rate ? rate : null;
        context.Patch(gameSet,
            GameMethods.Handler<Plugin>(nameof(GameRate)));
        if (unitySet != null) context.Patch(unitySet, GameMethods.Handler<Plugin>(nameof(UnityRate)));
        var update = context.Find(assembly, manager, "Update");
        if (update != null && gameGet != null) context.Patch(update, postfix: GameMethods.Handler<Plugin>(nameof(ApplyCurrent)));
        var settings = context.Find(assembly, manager, "ApplySetting", "System.Int32", "System.Int32", "System.Single", "System.Int32");
        if (settings != null) context.Patch(settings, prefix: GameMethods.Handler<Plugin>(nameof(ApplySettings)), priority: 500);
        unitySet?.Invoke(null, new object[] { fps });
    }
    public static void GameRate(object __instance, ref float __0)
    { if (!applying) originals[__instance] = __0; __0 = fps; }
    public static void UnityRate(ref int __0)
    { if (!applying && __0 != fps) unityOriginal = __0; __0 = fps; }
    public static void ApplyCurrent(object __instance)
    {
        if (originals.ContainsKey(__instance) || gameGet == null) return;
        ApplySettings(__instance);
    }
    public static void ApplySettings(object __instance)
    {
        if (gameGet != null && Convert.ToSingle(gameGet.Invoke(__instance, null)) is var rate && (!originals.ContainsKey(__instance) || rate != fps))
            originals[__instance] = rate;
        applying = true;
        try { gameSet!.Invoke(__instance, new object[] { (float)fps }); }
        finally { applying = false; }
    }
    public void Dispose()
    {
        foreach (var (instance, rate) in originals)
            try { gameSet?.Invoke(instance, new object[] { rate }); }
            catch (Exception error) { session?.Log("恢复游戏帧率: " + error.GetBaseException().Message); }
        if (unityOriginal.HasValue && unityGet?.Invoke(null, null) is int current && current == fps)
            unitySet?.Invoke(null, new object[] { unityOriginal.Value });
        originals.Clear(); unityOriginal = null; gameGet = gameSet = unityGet = unitySet = null; session = null; applying = false;
    }
}
