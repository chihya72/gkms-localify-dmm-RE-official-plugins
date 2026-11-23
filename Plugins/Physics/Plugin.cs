using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Physics;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    private static readonly HashSet<string> warnings = new();
    public IReadOnlyList<string> Warnings => warnings.ToArray();
    private static readonly Dictionary<string, object> values = new();
    private static PropertyInfo bones = null!;
    private static BoneLimits limits = null!;
    private static readonly Dictionary<(object Target, string Name), (PropertyInfo Property, object? Value)> originals = new();
    private static IPluginContext? session;
    private static readonly HashSet<object> activeBones = new();
    public void Start(IPluginContext context)
    {
        foreach (var (name, fallback) in new Dictionary<string, float> { ["damping"]=.33f, ["stiffness"]=.08f,
                     ["spring"]=1f, ["pendulum"]=.055f, ["pendulumRange"]=.15f, ["average"]=.2f, ["rootWeight"]=.5f })
        {
            var value = context.Option(name, fallback);
            if (!float.IsFinite(value)) throw new InvalidDataException(name);
            values[name] = value;
        }
        values["useArmCorrection"] = context.Option("useArmCorrection", true);
        session = context;
        limits = new BoneLimits(context);
        var target = context.RequireType("campus-submodule.Runtime", "Campus.Common.CampusActorAnimation")
            .GetMethods(GameMethods.Declared).Single(m => m.Name == "Setup" && m.GetParameters() is [_, { ParameterType.Name: "CampusActorAnimationInitializeData" }]);
        bones = target.GetParameters()[1].ParameterType.GetProperty("swingBreastBones") ?? throw new MissingMemberException("swingBreastBones");
        context.Patch(target, GameMethods.Handler<Plugin>(nameof(Apply)));
        foreach (var call in LiveGameState.Calls("Setup"))
            foreach (var item in Interop.Items(bones.GetValue(call.Arguments[1]))) if (!activeBones.Contains(item)) ApplyBone(item);
        foreach (var bone in LiveGameState.FindObjects(context, context.FindType("ActorAnimation.Runtime", "ActorAnimation.ActorSwingBreastBone")))
            if (!activeBones.Contains(bone)) ApplyBone(bone);
    }

    public static void Apply(object __1)
    {
        if (__1 == null || bones.GetValue(__1) is not { } list) return;
        var type = Interop.TypeOf(list);
        var count = (int)type.GetProperty("Count")!.GetValue(list)!;
        var index = type.GetProperty("Item", new[] { typeof(int) })!;
        for (var i = 0; i < count; i++)
        {
            if (index.GetValue(list, new object[] { i }) is not { } bone) continue;
            ApplyBone(bone);
        }
    }
    private static void ApplyBone(object bone)
    {
        activeBones.Add(bone);
        foreach (var (key, value) in values)
            if (Interop.TypeOf(bone).GetProperty(key) is { CanRead: true, CanWrite: true } prop) Set(bone, prop, value);
            else Warn("物理字段未接入: " + Interop.TypeOf(bone).FullName + "." + key);
        limits.Apply(bone);
        Dirty(bone);
    }
    internal static void Warn(string warning) { if (warnings.Add(warning)) session?.Log(warning); }
    private static void Dirty(object bone)
    {
        var type = Interop.TypeOf(bone);
        var dirty = type.GetProperty("isDirty", Interop.Members) ?? type.GetProperty("<isDirty>k__BackingField", Interop.Members);
        if (dirty is { CanWrite: true }) dirty.SetValue(bone, true);
    }
    internal static object? Original(object target, PropertyInfo property)
    {
        var key = (target, property.Name);
        if (!originals.TryGetValue(key, out var original))
            originals.Add(key, original = (property, property.GetValue(target)));
        return original.Value;
    }
    internal static void Set(object target, PropertyInfo property, object? value)
    { _ = Original(target, property); property.SetValue(target, value); }
    public void Dispose()
    {
        foreach (var (key, original) in originals.Reverse())
            try { original.Property.SetValue(key.Target, original.Value); }
            catch (Exception error) { session?.Log($"恢复物理参数 {key.Name}: {error.GetBaseException().Message}"); }
        foreach (var bone in activeBones)
            try { Dirty(bone); } catch (Exception error) { session?.Log("刷新物理状态: " + error.GetBaseException().Message); }
        activeBones.Clear(); originals.Clear(); values.Clear(); warnings.Clear(); session = null;
    }
}
