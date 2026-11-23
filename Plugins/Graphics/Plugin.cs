using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Graphics;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    private static int quality, pixels, volume, reflection, lod;
    private static float scale;
    private static MethodInfo? setReflection, setLod;
    private static MethodInfo? apply;
    private static readonly Dictionary<object, object[]> originals = new();
    private static IPluginContext? session;
    private static readonly Dictionary<(object Instance, string Method), (MethodInfo Setter, object Value)> levels = new();
    private static bool applying;
    private readonly List<string> warnings = new();
    public IReadOnlyList<string> Warnings => warnings;
    public void Start(IPluginContext context)
    {
        quality = context.Option("quality", 3);
        pixels = context.Option("maxBufferPixel", 3384);
        volume = context.Option("volumeIndex", 3);
        scale = context.Option("renderScale", .77f);
        reflection = context.Option("reflectionQuality", 4);
        lod = context.Option("lodQuality", 4);
        if (!float.IsFinite(scale)) throw new InvalidDataException("渲染倍率须为有限数值。");
        var target = context.Require("campus-submodule.Runtime", "Campus.Common.CampusQualityManager", "ApplySetting",
            "System.Int32", "System.Int32", "System.Single", "System.Int32");
        apply = target; session = context;
        var methods = target.DeclaringType!.GetMethods(GameMethods.Declared);
        setReflection = methods.SingleOrDefault(m => m.Name == "SetReflectionQuality" && m.GetParameters().Length == 1);
        setLod = methods.SingleOrDefault(m => m.Name == "SetLODQuality" && m.GetParameters().Length == 1);
        foreach (var (method, selected, name) in new[] { (setReflection, reflection, "反射质量"), (setLod, lod, "LOD 质量") })
            if (method == null || !method.GetParameters()[0].ParameterType.IsEnum)
                warnings.Add($"{name}: 当前客户端缺少兼容入口或档位，已跳过。");
        foreach (var method in new[] { setReflection, setLod }.OfType<MethodInfo>())
        {
            try { context.Patch(method, GameMethods.Handler<Plugin>(nameof(Level)).MakeGenericMethod(Interop.CallbackType(method.GetParameters()[0].ParameterType))); }
            catch (NotSupportedException error)
            {
                warnings.Add($"{method.Name}: 原值跟踪入口不兼容，恢复依赖可读取的当前属性。");
                context.Log($"跳过不兼容画质跟踪入口 {method.Name}: {error.Message}");
            }
        }
        context.Patch(target, GameMethods.Handler<Plugin>(nameof(Apply)));
        if (scale > 1)
        {
            var limited = context.Require("vl-unity.Runtime", "VL.VLUtility", "GetLimitedResolution",
                "System.Int32", "System.Int32", "UnityEngine.Vector2", "System.Int32", "System.Single", "System.Boolean");
            context.Patch(limited, GameMethods.Handler<Plugin>(nameof(ScaleResolution)));
        }
        foreach (var call in LiveGameState.Calls("SetReflectionQuality").Concat(LiveGameState.Calls("SetLODQuality")))
            levels[(call.Instance, call.Method.Name)] = (call.Method, call.Arguments[0]);
        using (LiveGameState.Replay())
            foreach (var call in LiveGameState.Calls("ApplySetting")) call.Method.Invoke(call.Instance, call.Arguments.ToArray());
    }
    public static void ScaleResolution(ref int __0, ref int __1)
    {
        __0 = (int)Math.Clamp((double)(__0 * scale), int.MinValue, int.MaxValue);
        __1 = (int)Math.Clamp((double)(__1 * scale), int.MinValue, int.MaxValue);
    }
    private static void SetLevel(object instance, MethodInfo? method, int index)
    {
        if (method == null) return;
        var type = method.GetParameters()[0].ParameterType;
        if (!type.IsEnum) return;
        var values = Enum.GetValues(type).Cast<object>().ToArray();
        if (values.Length == 0) values = new[] {0,10,20,30,40,100}.Select(v => Enum.ToObject(type,v)).ToArray();
        // DMM compares int to size_t: negative indices also clamp to the last level.
        if ((uint)index >= (uint)values.Length) index = values.Length - 1;
        if (!levels.ContainsKey((instance, method.Name)))
        {
            var name = method.Name[3..];
            var owner = Interop.TypeOf(instance);
            var property = owner.GetProperty(name, Interop.Members) ?? owner.GetProperty("_" + char.ToLowerInvariant(name[0]) + name[1..], Interop.Members);
            if (property?.PropertyType == type && property.GetValue(instance) is { } original)
                levels[(instance, method.Name)] = (method, original);
        }
        applying = true;
        using var replay = LiveGameState.Replay();
        try { method.Invoke(instance, new[] { values[index] }); }
        finally { applying = false; }
    }
    public static void Level<T>(object __instance, ref T __0, MethodBase __originalMethod)
    {
        if (!typeof(T).IsEnum) return;
        if (!applying) levels[(__instance, __originalMethod.Name)] = ((MethodInfo)__originalMethod, __0!);
        // The original only chooses LOD/reflection inside ApplySetting. Independent
        // game calls must pass through; this hook records their values for hot disable.
    }
    public static void Apply(object __instance, ref int __0, ref int __1, ref float __2, ref int __3)
    {
        originals[__instance] = new object[] { __0, __1, __2, __3 };
        SetLevel(__instance, setLod, lod); SetLevel(__instance, setReflection, reflection);
        (__0, __1, __2, __3) = (quality, pixels, scale, volume);
    }
    public void Dispose()
    {
        using var replay = LiveGameState.Replay();
        foreach (var (instance, values) in originals)
            try { apply?.Invoke(instance, values); }
            catch (Exception error) { session?.Log("恢复画质: " + error.GetBaseException().Message); }
        foreach (var (key, level) in levels)
            try { level.Setter.Invoke(key.Instance, new[] { level.Value }); }
            catch (Exception error) { session?.Log("恢复反射/细节质量: " + error.GetBaseException().Message); }
        originals.Clear(); levels.Clear(); apply = setReflection = setLod = null; session = null; applying = false;
    }
}
