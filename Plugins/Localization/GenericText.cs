using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

public sealed class GenericText(TextDictionary resources) : ILocalifyPlugin, IPluginDiagnostics
{
    private static readonly HashSet<string> warnings = new();
    public IReadOnlyList<string> Warnings => warnings.ToArray();
    private static TextDictionary? dictionary;
    private static bool textTest;
    private static Type? tmpType;
    public void Start(IPluginContext context)
    {
        dictionary = resources;
        LiveGameState.TextResourcesRevision++;
        textTest = context.Option("textTest", false);
        const string tmp = "Unity.TextMeshPro";
        const string type = "TMPro.TMP_Text";
        tmpType = context.FindType(tmp, type);
        var count = 0;
        void Bind(MethodInfo? target, string handler, string? after = null, bool setter = false)
        {
            if (target == null) return;
            try { if (setter) context.PatchAdvanced(target, GameMethods.Handler<SetterObserver>(nameof(SetterObserver.EnterSetter)),
                finalizer: GameMethods.Handler<SetterObserver>(nameof(SetterObserver.LeaveSetter)), priority: 1000);
                context.Patch(target, GameMethods.Handler<GenericText>(handler), after == null ? null : GameMethods.Handler<GenericText>(after)); count++; }
            catch (NotSupportedException error)
            { var message = $"跳过不兼容文本入口 {target.DeclaringType?.FullName}.{target.Name}: {error.Message}"; warnings.Add(message); context.Log(message); }
        }
        Bind(context.Find(tmp, type, "set_text", "System.String"), nameof(Translate), nameof(AfterText), setter: true);
        Bind(context.Find(tmp, type, "SetText", "System.String"), nameof(Translate), nameof(AfterText));
        Bind(context.Find(tmp, type, "SetText", "System.String", "System.Boolean"), nameof(Translate), nameof(AfterText));
        Bind(context.Find(tmp, type, "PopulateTextBackingArray", "System.String", "System.Int32", "System.Int32"), nameof(TranslateSlice), nameof(AfterText));
        Bind(context.Find("UnityEngine.UI", "UnityEngine.UI.Text", "set_text", "System.String"), nameof(Translate));
        Bind(context.Find("UnityEngine.UIElementsModule", "UnityEngine.UIElements.TextField", "set_value", "System.String"), nameof(Translate));
        var charTarget = context.FindType(tmp, type)?.GetMethods(GameMethods.Declared).SingleOrDefault(m =>
            m.Name == "SetCharArray" && m.GetParameters() is [{ ParameterType: var array }, { ParameterType: var a }, { ParameterType: var b }]
            && (array.IsArray && array.GetElementType() == typeof(char) || array.IsGenericType && array.GetGenericArguments().SequenceEqual(new[] { typeof(char) }))
            && a == typeof(int) && b == typeof(int));
        Bind(charTarget, nameof(TranslateChars));
        var awake = context.Find(tmp, "TMPro.TextMeshProUGUI", "Awake");
        if (awake?.DeclaringType?.GetProperty("text") is { CanRead: true, CanWrite: true })
            Bind(awake, nameof(TranslateInitial));
        // Match original DMM write/Awake boundaries. Rendering is not a second
        // translation pass: it can occur after layout has already measured Japanese.
        if (count == 0) throw new MissingMethodException("未找到文本显示入口。");
        GameTextFont.TranslationHooksActive = true;
        context.Log($"{dictionary!.Count} 个词条，{count} 个显示入口。");
    }
    private static string TranslateValue(string source, out bool matched)
    {
        // Hook.cpp translates each actual input at this entry point.
        matched = dictionary!.TryTranslate(source, out var value);
        return value;
    }
    public static void Translate(object __instance, ref string __0, MethodBase __originalMethod, out bool __state)
    {
        __state = false;
        if (__0 == null || dictionary == null || SkipSetter(__instance, __originalMethod)) return;
        var translated = TranslateValue(__0, out var matched);
        var tmp = tmpType?.IsInstanceOfType(__instance) == true;
        // GetGenericText can succeed even when an exact translation equals its key.
        if (tmp && matched) GameTextFont.Apply(__instance);
        __state = tmp && !matched;
        if (textTest && !matched)
        {
            var marker = __originalMethod.Name switch
            {
                "set_text" when __originalMethod.DeclaringType?.FullName == "UnityEngine.UI.Text" => "[UI]",
                "set_text" => "[TT]",
                "SetText" => __originalMethod.GetParameters().Length == 1 ? "[T1]" : "[TS]",
                _ => ""
            };
            translated = marker + __0;
        }
        __0 = translated;
    }
    public static void AfterText(object __instance, bool __state)
    { if (__state) GameTextFont.Apply(__instance); }

    public static void TranslateInitial(object __instance)
    {
        var property = Interop.TypeOf(__instance).GetProperty("text")!;
        if (property.GetValue(__instance) is not string value || dictionary == null) { GameTextFont.Apply(__instance); return; }
        var translated = TranslateValue(value, out var matched);
        var written = matched && textTest ? "[TA]" + translated : translated;
        if (matched)
        {
            // DMM Awake calls the normal setter (and therefore its hook).
            property.SetValue(__instance, written);
        }
        GameTextFont.Apply(__instance);
    }
    public static void TranslateSlice(object __instance, ref string __0, ref int __1, ref int __2, out bool __state)
    {
        __state = false;
        if (__0 == null || dictionary == null || __1 < 0 || __2 < 0 || __1 > __0.Length - __2) return;
        var segment = __0.Substring(__1, __2);
        var translated = TranslateValue(segment, out var matched);
        if (matched) GameTextFont.Apply(__instance);
        __state = !matched;
        if (textTest && !matched)
        {
            // Hook.cpp prefixes the WHOLE input, keeps start, and adds four to count.
            __0 = "[TP]" + __0; __2 += 4;
            translated = __0.Substring(__1, __2);
            return;
        }
        if (!matched && segment == translated) return;
        __0 = translated; __1 = 0; __2 = translated.Length;
    }

    public static bool TranslateChars(object __instance, object __0, int __1, int __2)
    {
        if (__0 == null || dictionary == null || __1 < 0 || __2 <= 0) return true;
        var length = __0 is char[] chars ? chars.Length : Convert.ToInt32(Interop.TypeOf(__0).GetProperty("Length")?.GetValue(__0));
        if (__1 > length - __2) return true;
        var item = Interop.TypeOf(__0).GetProperty("Item");
        if (__0 is not char[] && item == null) return true;
        var source = __0 is char[] array ? new string(array, __1, __2) :
            new string(Enumerable.Range(__1, __2).Select(i => (char)item!.GetValue(__0, new object[] { i })!).ToArray());
        var result = TranslateValue(source, out var matched);
        if (textTest && !matched) result = "[CA]" + source;
        var text = Interop.TypeOf(__instance).GetProperty("text");
        if ((!matched && result == source) || text?.CanWrite != true) return true;
        GameTextFont.Apply(__instance);
        // Hook.cpp calls TMP_Text_set_text_Orig here; do not translate the setter
        // again. Nested PopulateTextBackingArray remains an independent DMM hook.
        HookExecution.MarkReplacement();
        var previous = bypass;
        bypass = new(__instance, text.SetMethod!, setterFrame);
        try { text.SetValue(__instance, result); }
        finally { bypass = previous; }
        return false;
    }
    // This is only a call-scoped _Orig bypass, with no text/object provenance cache.
    [ThreadStatic] private static SetterFrame? setterFrame;
    [ThreadStatic] private static SetterBypass? bypass;
    private sealed record SetterBypass(object Instance, MethodBase Method, SetterFrame? Parent)
    { public bool Consumed; }
    public sealed record SetterFrame(object Instance, MethodBase Method, SetterFrame? Parent, bool Skip);
    public sealed class SetterObserver
    {
        public static void EnterSetter(object __instance, MethodBase __originalMethod, out SetterFrame __state)
        {
            var skip = bypass is { Consumed: false } request && Equals(request.Instance, __instance) &&
                Equals(request.Method, __originalMethod) && ReferenceEquals(request.Parent, setterFrame);
            if (skip) bypass!.Consumed = true;
            __state = new(__instance, __originalMethod, setterFrame, skip); setterFrame = __state;
        }
        public static void LeaveSetter(SetterFrame? __state) { if (__state != null) setterFrame = __state.Parent; }
    }
    private static bool SkipSetter(object instance, MethodBase method) => setterFrame is { Skip: true } frame &&
        Equals(frame.Instance, instance) && Equals(frame.Method, method);
    public void Dispose() { GameTextFont.TranslationHooksActive = false; dictionary = null; textTest = false; warnings.Clear(); tmpType = null; }
}
