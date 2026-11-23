using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Physics;

internal sealed class BoneLimits
{
    private readonly bool scaleEnabled, limitEnabled;
    private readonly float scale;
    private readonly float[][] factors;
    private static readonly string[] axes = { "axisX", "axisY", "axisZ" };
    public BoneLimits(IPluginContext ctx)
    {
        scaleEnabled = ctx.Option("useScale", false);
        scale = ctx.Option("scale", 1f);
        limitEnabled = ctx.Option("useLimit", true);
        factors = axes.Select(axis => ctx.Option(axis, new[] { 1f, 1f })).ToArray();
        if (!float.IsFinite(scale) || scale <= 0 || factors.Any(pair => pair.Length != 2 || pair.Any(v => !float.IsFinite(v))))
            throw new InvalidDataException("骨骼缩放和每轴的两个限位系数须有效。");
    }

    public void Apply(object bone)
    {
        var type = Interop.TypeOf(bone);
        if (scaleEnabled)
            foreach (var name in new[] { "leftBreast", "rightBreast", "leftBreastEnd", "rightBreastEnd" })
            {
                var transform = type.GetProperty(name)?.GetValue(bone);
                if ((transform == null ? null : Interop.TypeOf(transform))?.GetProperty("localScale") is { } prop)
                    Plugin.Set(transform!, prop, Interop.New(prop.PropertyType, scale, scale, scale));
                else if (type.GetProperty(name) == null) Plugin.Warn("物理缩放字段未接入: " + name);
            }
        var limit = type.GetProperty("limitInfo")?.GetValue(bone);
        if (limit == null) return;
        if (Interop.TypeOf(limit).GetProperty("useLimit") is { } useLimit)
            Plugin.Set(limit, useLimit, limitEnabled ? 1 : 0);
        if (!limitEnabled || factors.All(p => p[0] == 1 && p[1] == 1)) return;
        var properties = axes.Select(a => Interop.TypeOf(limit).GetProperty(a) ?? throw new MissingMemberException(a)).ToArray();
        var original = properties.Select(p =>
        {
            // Hook.cpp applies factors to the current axes on every actual Setup.
            // Hot reload restores the recorded original before applying the new config.
            var value = p.GetValue(limit)!;
            return new[] { (int)p.PropertyType.GetProperty("x")!.GetValue(value)!, (int)p.PropertyType.GetProperty("y")!.GetValue(value)! };
        }).ToArray();
        for (var i = 0; i < properties.Length; i++)
        {
            var x = (int)(original[i][0] * factors[i][0]); var y = (int)(original[i][1] * factors[i][1]);
            Plugin.Set(limit, properties[i], Interop.New(properties[i].PropertyType, x, y));
        }
    }
}
