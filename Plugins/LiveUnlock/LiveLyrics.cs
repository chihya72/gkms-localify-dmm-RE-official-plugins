using Gkms.Sdk;

namespace Gkms.Plugins.LiveUnlock;

internal sealed class LiveLyrics : ILocalifyPlugin
{
    // NativeObject equality follows the native object, even when each call creates a new wrapper.
    private static readonly HashSet<object> initialized = new();
    public void Start(IPluginContext context)
    {
        context.Patch(context.Require("Assembly-CSharp", "Campus.Live.LiveScenePresenter", "SetLyricsActive", "System.Boolean"),
            GameMethods.Handler<LiveLyrics>(nameof(ShowInitially)));
        using var replay = LiveGameState.Replay();
        foreach (var call in LiveGameState.Calls("SetLyricsActive")) call.Method.Invoke(call.Instance, call.Arguments.ToArray());
    }
    public static void ShowInitially(object __instance, ref bool __0)
    {
        if (!initialized.Add(__instance)) return;
        __0 = true;
    }
    public void Dispose()
    {
        using var replay = LiveGameState.Replay();
        foreach (var call in LiveGameState.Calls("SetLyricsActive")) call.Method.Invoke(call.Instance, call.Arguments.ToArray());
        initialized.Clear();
    }
}
