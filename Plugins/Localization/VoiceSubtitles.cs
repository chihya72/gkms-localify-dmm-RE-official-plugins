using System.Collections.Concurrent;
using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

public sealed class VoiceSubtitles : ILocalifyPlugin
{
    public readonly record struct Playback(object? Player, object? Actor);
    private static VoiceSubtitles? instance;
    [ThreadStatic] private static Playback playing;
    private readonly ConcurrentQueue<Action> events = new();
    private readonly Dictionary<string, object?> actors = new();
    private IPluginContext context = null!;
    private SubtitleConfig config = null!;
    private DialogueBubble bubble = null!;
    private object? page, homeView, player;
    private string? text, cue;
    private bool shown, freeLook, ticking, stopRequested, uiFailed, deferred, visible;
    private MethodInfo stopped = null!;

    public static string ConfigFile(string localFiles) =>
        Path.Combine(localFiles, "genericTrans", "genericTrans", "lyrics", "home_voice_subtitles.json");

    public void Start(IPluginContext ctx)
    {
        context = ctx;
        config = SubtitleConfig.Load(ConfigFile(ResourceUpdater.ResourceDirectory));
        bubble = new DialogueBubble(ctx, config);
        const string campus = "campus-submodule.Runtime", sound = "Campus.Common.Sound.CampusSoundPlayer";
        const string game = "Assembly-CSharp", home = "Campus.OutGame.";
        stopped = ctx.Require(campus, sound, "IsEndOrStop");
        void Bind(string assembly, string type, string method, int count, string? before, string? after)
        {
            var target = ctx.RequireType(assembly, type).GetMethods(GameMethods.Declared)
                .Single(m => m.Name == method && m.GetParameters().Length == count);
            if (after == nameof(ReturnFromPlay))
                ctx.PatchAdvanced(target, before == null ? null : GameMethods.Handler<VoiceSubtitles>(before),
                    finalizer: GameMethods.Handler<VoiceSubtitles>(after));
            else ctx.Patch(target, before == null ? null : GameMethods.Handler<VoiceSubtitles>(before),
                after == null ? null : GameMethods.Handler<VoiceSubtitles>(after));
        }
        Bind(game, home + "HomePageViewBase", "Initialize", 0, null, nameof(Initialized));
        Bind(game, home + "HomePageViewBase", "OnToggle", 2, null, nameof(Toggle));
        Bind(game, home + "HomePageGroupPresenter", "OnScreenVisitStart", 0, null, nameof(Visit));
        Bind(game, home + "HomePageGroupPresenter", "OnScreenVisitEnd", 0, null, nameof(Visit));
        Bind(game, home + "HomeTopScreenPresenter", "OnOpenOutInactive", 0, nameof(Leave), null);
        Bind(game, home + "HomeTopScreenPresenter", "OnDestroy", 0, nameof(Destroyed), null);
        Bind(game, home + "Home3DManager", "SetFreeLookEnable", 1, nameof(FreeLook), null);
        Bind(game, home + "HomeActorBase", "OnLoadEnd", 0, null, nameof(ActorReady));
        Bind(game, home + "HomeTopActor", "OnLoadEnd", 0, null, nameof(ActorReady));
        Bind(campus, sound, "Play", 0, nameof(Play), nameof(ReturnFromPlay));
        Bind(campus, sound, "PlayWith", 2, nameof(PlayWith), nameof(ReturnFromPlay));
        Bind(campus, sound, "Stop", 0, nameof(Stop), null);
        Bind(campus, sound, "ResetState", 0, nameof(Stop), null);
        Bind(campus, "Campus.Common.CampusSoundManager", "OnSoundPlayed", 1, nameof(Heard), null);
        Bind(campus, "Campus.Common.CampusQualityManager", "Update", 0, null, nameof(Tick));
        instance = this;
        Tick();
    }

    private static bool Same(object? a, object? b) => Equals(a, b) || a != null && b != null &&
        Interop.PointerOf(a) is var p && p != IntPtr.Zero && p == Interop.PointerOf(b);

    private static string ClassName(object obj)
    {
        var type = Interop.TypeOf(obj).GetMethod("GetIl2CppType", Type.EmptyTypes)?.Invoke(obj, null);
        return type == null ? Interop.TypeOf(obj).Name : (string)DialogueBubble.Get(type, "Name")!;
    }
    private static string? Tab(object obj) => ClassName(obj) switch
    {
        "HomeTopActor" or "HomePageView" => "Home",
        "HomeStoryActor" or "HomeStoryPageView" => "Story",
        "HomeIdolActor" or "HomeIdolPageView" => "Idol",
        "HomeContestActor" or "HomePvpPageView" => "Pvp", _ => null
    };

    private void ChangePage(object? next)
    {
        if (next == null) { visible = false; freeLook = false; ClearVoice(); return; }
        visible = true;
        if (next != null && Tab(next) == "Home") homeView = next;
        if (Same(page, next)) { deferred = true; return; }
        ClearVoice(); bubble.Dispose(); page = next; freeLook = false; uiFailed = false; deferred = true;
    }
    private void ClearVoice() { bubble.Hide(); text = cue = null; player = null; shown = stopRequested = false; }
    private static bool HasText(string? text) => text != null && text.Trim(' ', '\t', '\r', '\n').Length != 0;
    private static void Post(Action<VoiceSubtitles> action)
    {
        var s = instance;
        if (s != null) s.events.Enqueue(() => action(s));
    }

    public static void Initialized(object __instance) => Post(s =>
    {
        if (Tab(__instance) == "Home" && !Same(s.homeView, __instance))
        {
            s.ClearVoice(); s.bubble.Dispose(); s.homeView = __instance;
            s.uiFailed = false; s.deferred = true;
        }
    });
    public static void Toggle(object __instance, bool __0) => Post(s =>
    {
        if (__0) s.ChangePage(Tab(__instance) == null ? null : __instance);
        else if (Same(s.page, __instance)) s.ChangePage(null);
    });
    public static void Visit(object __instance) => Post(s =>
    {
        var current = DialogueBubble.Call(__instance, "GetCurrentPage");
        if (current == null) { s.ChangePage(null); return; }
        var type = s.context.FindType("Assembly-CSharp", "Campus.OutGame." + ClassName(current));
        if (type != null) current = DialogueBubble.As(current, type);
        var view = DialogueBubble.Get(current, "_view");
        s.ChangePage(view != null && Tab(view) != null ? view : null);
    });
    public static void Leave() => Post(s => s.ChangePage(null));
    public static void Destroyed() => Post(s => { s.ChangePage(null); s.bubble.Dispose(); s.homeView = s.page = null; s.actors.Clear(); });
    public static void FreeLook(bool __0) => Post(s => { s.freeLook = __0; if (__0 && !s.shown) s.ClearVoice(); });
    public static void ActorReady(object __instance) => Post(s =>
    {
        if (Tab(__instance) is not { } tab) return;
        var actor = DialogueBubble.Get(__instance, "Actor");
        if (s.page != null && Tab(s.page) == tab && !Same(s.actors.GetValueOrDefault(tab), actor)) s.ClearVoice();
        s.actors[tab] = actor;
        s.deferred = true;
    });

    public static void Play(object __instance, out Playback __state)
    {
        __state = playing; playing = new(__instance, Same(playing.Player, __instance) ? playing.Actor : null);
    }
    public static void PlayWith(object __instance, object? __0, out Playback __state)
    {
        __state = playing; playing = new(__instance, __0);
    }
    public static void ReturnFromPlay(Playback __state) => playing = __state;
    public static void Heard(string __0)
    {
        var voice = playing;
        if (voice.Player == null || __0 == null || !__0.StartsWith("sud_vo_", StringComparison.Ordinal) || !__0.Contains("_home_")) return;
        Post(s =>
        {
            if (!s.config.Enabled || !s.visible || s.page == null || Tab(s.page) is not { } tab) return;
            if (voice.Actor != null && !Same(s.actors.GetValueOrDefault(tab), voice.Actor)) return;
            if (s.freeLook) { if (s.shown && Same(s.player, voice.Player)) s.stopRequested = true; return; }
            s.ClearVoice();
            s.cue = __0;
            s.text = s.config.Lookup(__0);
            if (HasText(s.text)) s.player = voice.Player;
        });
    }
    public static void Stop(object __instance) => Post(s => { if (Same(s.player, __instance)) s.stopRequested = true; });
    public static void Tick()
    {
        var s = instance;
        if (s == null || s.ticking) return;
        s.ticking = true;
        try
        {
            while (s.events.TryDequeue(out var action)) action();
            if (!s.config.Enabled || !s.visible) { s.ClearVoice(); return; }
            if (s.player == null || s.page == null) return;
            if (!s.bubble.Alive(s.page)) { s.ClearVoice(); return; }
            s.stopRequested |= !s.bubble.Alive(s.player) || (bool)s.stopped.Invoke(s.player, null)!;
            if (s.freeLook)
            {
                if (s.shown)
                {
                    var alpha = s.bubble.FollowFade();
                    if (alpha < 0 || s.stopRequested && alpha <= .001f) s.ClearVoice();
                }
                return;
            }
            if (s.stopRequested) { s.ClearVoice(); return; }
            if (s.uiFailed || !s.bubble.Alive(s.homeView) || !s.bubble.Alive(s.actors.GetValueOrDefault(Tab(s.page)!))) return;
            if (!s.shown)
            {
                if (s.deferred) { s.deferred = false; return; }
                s.shown = s.bubble.Show(s.page, s.homeView!, s.text!);
            }
            if (s.shown && !s.bubble.Position(s.homeView!) && s.bubble.FollowFade() < 0) s.ClearVoice();
        }
        catch (Exception e)
        {
            s.player = null; s.text = null; s.shown = false;
            s.uiFailed = true;
            s.bubble.Dispose(); s.context.Log($"语音气泡: {e.GetBaseException().Message}");
        }
        finally { s.ticking = false; }
    }
    public void Dispose()
    {
        instance = null; playing = default; events.Clear(); actors.Clear();
        homeView = page = player = null; bubble?.Dispose();
    }
}
