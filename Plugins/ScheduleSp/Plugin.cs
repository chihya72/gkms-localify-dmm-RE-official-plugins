using Gkms.Sdk;

namespace Gkms.Plugins.ScheduleSp;

public sealed class Plugin : ILocalifyPlugin
{
    public void Start(IPluginContext context)
    {
        var display = context.Require("Assembly-CSharp", "Campus.InGame.ExamExtensions", "ExchangeSpToNormal",
            "Campus.Common.Proto.Client.Enums.ProduceStepType");
        if (display.ReturnType != display.GetParameters()[0].ParameterType || !display.ReturnType.IsEnum)
            throw new InvalidDataException("日程类型与客户端不匹配。");
        context.Patch(display, GameMethods.Handler<Plugin>(nameof(KeepLabel)).MakeGenericMethod(Interop.CallbackType(display.ReturnType)));
    }

    public static bool KeepLabel<T>(T __0, ref T __result) where T : struct, Enum
    {
        __result = __0;
        return false;
    }

    public void Dispose() { }
}
