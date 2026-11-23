using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

// The source is a prefab asset. Only the cloned MessageContainer receives layout/text changes.
internal sealed partial class DialogueBubble : IDisposable
{
    private const string Core = "UnityEngine.CoreModule";
    private readonly IPluginContext context;
    private readonly SubtitleConfig config;
    private readonly Type gameObject, rectTransform, presenter, container, group;
    private readonly MethodInfo load, clone, destroy, alive, childComponent, addComponent;
    private readonly Dictionary<Type, object> nativeTypes = new();
    private object? root, box, dialogue, message, label, canvasGroup;
    private float width, height;

    public DialogueBubble(IPluginContext context, SubtitleConfig config)
    {
        this.context = context;
        this.config = config;
        gameObject = context.RequireType(Core, "UnityEngine.GameObject");
        rectTransform = context.RequireType(Core, "UnityEngine.RectTransform");
        presenter = context.RequireType("Assembly-CSharp", "Campus.Common.VerticalAdvPresenter");
        container = context.RequireType("ADV.Runtime", "Campus.ADV.MessageContainer");
        group = context.RequireType("UnityEngine.UIModule", "UnityEngine.CanvasGroup");
        load = context.Require(Core, "UnityEngine.Resources", "Load", "System.String", "System.Type");
        clone = context.Require(Core, "UnityEngine.Object", "Instantiate", "UnityEngine.Object", "UnityEngine.Transform", "System.Boolean");
        destroy = context.Require(Core, "UnityEngine.Object", "Destroy", "UnityEngine.Object");
        alive = context.Require(Core, "UnityEngine.Object", "op_Implicit", "UnityEngine.Object");
        childComponent = context.Require(Core, "UnityEngine.GameObject", "GetComponentInChildren", "System.Type", "System.Boolean");
        addComponent = context.Require(Core, "UnityEngine.GameObject", "AddComponent", "System.Type");
    }

    internal static object? Get(object obj, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var t = Interop.TypeOf(obj); t != null; t = t.BaseType)
        {
            if (t.GetProperty(name, flags | BindingFlags.DeclaredOnly) is { } p) return p.GetValue(obj);
            if (t.GetField(name, flags | BindingFlags.DeclaredOnly) is { } f) return f.GetValue(obj);
        }
        throw new MissingMemberException(Interop.TypeOf(obj).FullName, name);
    }
    internal static object? Call(object obj, string name, params object?[] args)
    {
        var method = Interop.TypeOf(obj).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && !m.ContainsGenericParameters && m.GetParameters().Length == args.Length &&
                m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(match => match))
            ?? throw new MissingMethodException(Interop.TypeOf(obj).FullName, name);
        return method.Invoke(obj, args);
    }
    private static void Set(object obj, string name, object value)
    {
        var p = Interop.TypeOf(obj).GetProperty(name) ?? throw new MissingMemberException(Interop.TypeOf(obj).FullName, name);
        p.SetValue(obj, p.PropertyType.IsEnum ? Enum.ToObject(p.PropertyType, value) : value);
    }
    internal static object As(object obj, Type type) => Interop.Cast(obj, type);
    private object Native(Type type) => nativeTypes.TryGetValue(type, out var v) ? v :
        nativeTypes[type] = Interop.NativeTypeObject(type);
    private object Component(object obj, Type type) => As(childComponent.Invoke(obj, new[] { Native(type), true }) ??
        throw new InvalidOperationException($"对白预制件缺少 {type.Name}。"), type);
    internal bool Alive(object? obj) => obj != null && (bool)alive.Invoke(null, new[] { obj })!;
    private object Rect(object obj) => As(Get(obj, "transform")!, rectTransform);
    private static void Vector(object obj, string name, float x, float y)
    {
        var p = Interop.TypeOf(obj).GetProperty(name)!;
        p.SetValue(obj, Interop.New(p.PropertyType, x, y));
    }
    private void Stretch(object transform, float x = 0, float y = 0)
    {
        var r = As(transform, rectTransform);
        Vector(r, "anchorMin", 0, 0); Vector(r, "anchorMax", 1, 1); Vector(r, "pivot", .5f, .5f);
        Vector(r, "sizeDelta", -x, -y); Vector(r, "anchoredPosition", 0, 0);
    }
    private static float Number(object obj, string name) => Convert.ToSingle(Get(obj, name));
    private void Build(object page)
    {
        var parent = Get(page, "transform");
        while (Alive(parent) && (string?)Get(parent!, "name") != "UIContentArea") parent = Get(parent!, "parent");
        if (!Alive(parent)) return;
        var prefab = As(load.Invoke(null, new[] { "verticaladv", Native(gameObject) }) ??
            throw new InvalidOperationException("缺少 verticaladv 预制件。"), gameObject);
        var engine = Get(Component(prefab, presenter), "_enginePrefab")!;
        var original = Get(Component(Get(engine, "gameObject")!, container), "gameObject")!;
        root = As(clone.Invoke(null, new[] { original, parent, false })!, gameObject);
        Call(root, "SetActive", false);
        Set(root, "name", "HomeVoiceNativeDialogueBubble");
        box = Rect(root);
        Call(box, "SetAsLastSibling");
        Vector(box, "anchorMin", 0, .5f); Vector(box, "anchorMax", 0, .5f); Vector(box, "pivot", 0, .5f);
        var get = context.Require(Core, "UnityEngine.GameObject", "GetComponent", "System.Type");
        var rawGroup = get.Invoke(root, new[] { Native(group) }) ?? addComponent.Invoke(root, new[] { Native(group) });
        canvasGroup = As(rawGroup!, group);
        Set(canvasGroup, "blocksRaycasts", false); Set(canvasGroup, "interactable", false);
        dialogue = Component(root, container);
        Call(dialogue, "Show");
        message = Get(dialogue, "_messageText")!;
        label = Get(message, "TargetTMP")!;
        Set(label, "fontSize", config.FontSize); Set(label, "enableAutoSizing", false);
        Set(label, "enableWordWrapping", true); Set(label, "richText", false); Set(label, "raycastTarget", false);
        Set(label, "alignment", 513);
        var animation = Call(box, "Find", "Animation Root");
        if (Alive(animation))
        {
            Stretch(animation!);
            foreach (var path in new[] { "Message Background", "Message Background/Vertical", "Message Background/Vertical/Message Image" })
                if (Call(animation!, "Find", path) is { } node) Stretch(node);
            try { StyleFrame(animation!); }
            catch (Exception error) { context.Log($"气泡沿用预制件边框: {error.GetBaseException().Message}"); }
            foreach (var path in new[] { "Speaker Image", "Name Container", "Message Background/Vertical/Break Icon Container" })
                if (Call(animation!, "Find", path) is { } node) Call(Get(node,"gameObject")!, "SetActive", false);
        }
        Stretch(Get(label, "transform")!, config.Padding[0], config.Padding[1]);
        var groups = context.Find(Core, "UnityEngine.GameObject", "GetComponentsInChildren", "System.Type", "System.Boolean");
        if (groups?.Invoke(root, new[] { Native(group), true }) is System.Collections.IEnumerable components)
            foreach (var raw in components)
            {
                var g = As(raw, group);
                Set(g, "alpha", 1f); Set(g, "interactable", false); Set(g, "blocksRaycasts", false);
            }
    }

    public bool Show(object page, object home, string text)
    {
        if (!Alive(root)) { Dispose(); Build(page); }
        if (!Alive(root)) return false;
        var natural = Call(label!, "GetPreferredValues", text, float.PositiveInfinity, float.PositiveInfinity)!;
        width = Math.Clamp(MathF.Ceiling(Number(natural, "x") + config.Padding[0]), config.MinWidth, config.MaxWidth);
        var measured = Call(label!, "GetPreferredValues", text, width - config.Padding[0], float.PositiveInfinity)!;
        height = MathF.Ceiling(Math.Max(config.FontSize, Number(measured, "y")) + config.Padding[1]);
        Vector(box!, "sizeDelta", width, height);
        Vector(box!, "anchoredPosition", config.LeftOffset, 0);
        if (!Position(home)) return false;
        Call(dialogue!, "SetMessage", text, false);
        Call(message!, "CompleteAnimation");
        if (FollowFade() < 0) return false;
        Call(root!, "SetActive", true);
        return true;
    }
    public float FollowFade()
    {
        if (!Alive(root) || !Alive(headerFade) || !Alive(canvasGroup)) return -1;
        var alpha = Number(headerFade!, "alpha");
        alpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 0;
        Set(canvasGroup!, "alpha", alpha); return alpha;
    }
    public void Hide() { if (Alive(root)) Call(root!, "SetActive", false); }
    public void Dispose()
    {
        if (Alive(root)) destroy.Invoke(null, new[] { root });
        root = box = dialogue = message = label = canvasGroup = null;
        level = switcher = headerFade = headerCanvas = contentCanvas = null; layoutUnavailable = false; nextLookup = 0;
    }
}
