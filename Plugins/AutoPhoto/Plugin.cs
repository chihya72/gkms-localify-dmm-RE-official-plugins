using Gkms.Sdk;

namespace Gkms.Plugins.AutoPhoto;

public sealed class Plugin : ILocalifyPlugin
{
    private static int[] counts = Array.Empty<int>();
    private static readonly Dictionary<object, int[]> originals = new();
    public void Start(IPluginContext context)
    {
        counts = context.Option("counts", new[] { 10, 20, 30 });
        if (counts.Length != 3 || counts.Any(x => x <= 0)) throw new InvalidDataException("counts 需要三个正整数。");
        var limit = context.Require("Assembly-CSharp", "Campus.Live.LiveUtility", "GetAutoPhotoLimitCount");
        context.Patch(limit, postfix: GameMethods.Handler<Plugin>(nameof(Limit)));
        var options = context.Require("Assembly-CSharp", "Campus.Live.LiveUtility", "AutoPhotoCountTypeValues");
        context.Patch(options, postfix: GameMethods.Handler<Plugin>(nameof(Options)));
    }
    public static void Options(object __result)
    {
        if (__result == null) return;
        var values = Interop.Items(__result).Select(Convert.ToInt32).ToArray();
        if (values.Length < 3) return;
        originals.TryAdd(__result, values.Take(3).ToArray());
        Write(__result, counts);
    }
    private static void Write(object target, int[] values)
    {
        if (target is int[] arr) { values.CopyTo(arr, 0); return; }
        var index = Interop.TypeOf(target).GetProperty("Item") ?? throw new MissingMemberException("AutoPhotoCountTypeValues.Item");
        for (var i = 0; i < values.Length; i++) index.SetValue(target, values[i], new object[] { i });
    }
    public static void Limit(ref int __result) => __result = __result switch
        { 1 or 3 => counts[2], 5 => counts[1], 10 => counts[0], _ => __result };
    public void Dispose()
    {
        foreach (var (array, values) in originals)
            if (Interop.Items(array).Take(3).Select(Convert.ToInt32).SequenceEqual(counts)) Write(array, values);
        originals.Clear();
    }
}
