using System.Collections;
using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

internal sealed partial class DialogueBubble
{
    private object? level, switcher, headerFade, contentCanvas, headerCanvas;
    private long nextLookup;
    private bool layoutUnavailable;
    private MethodInfo? transformPoint, worldToScreen, screenToLocal;

    private void StyleFrame(object animation)
    {
        var imageType = context.FindType("UnityEngine.UI", "UnityEngine.UI.Image");
        var atlasType = context.FindType(Core, "UnityEngine.U2D.SpriteAtlas");
        var body = Call(animation, "Find", "Message Background/Vertical/Message Image");
        var frame = body == null ? null : Call(body, "Find", "frame");
        if (imageType == null || atlasType == null || body == null || frame == null) return;
        var resource = load.Invoke(null, new[] { "commonatlas", Native(atlasType) });
        if (!Alive(resource)) return;
        var atlas = As(resource!, atlasType);
        var backgroundObject = Get(body, "gameObject")!;
        foreach (var effectName in new[] { "LinearGradientGlass", "LinearGradient", "BackgroundBlur" })
        {
            var type = context.FindType("campus-submodule.Runtime", "Campus.Common." + effectName);
            if (type != null && childComponent.Invoke(backgroundObject, new[] { Native(type), true }) is { } component)
                Set(As(component, type), "enabled", false);
        }
        foreach (var (obj, spriteName) in new[] { (backgroundObject, "bg_round_r32_64px"), (Get(frame,"gameObject")!, "frame_round_r32_b4_64px") })
        {
            var sprite = Call(atlas, "GetSprite", spriteName);
            if (!Alive(sprite)) continue;
            var image = Component(obj, imageType);
            var color = Interop.TypeOf(image).GetProperty("color")!;
            color.SetValue(image, Interop.New(color.PropertyType, 1f, 1f, 1f, 1f));
            Interop.TypeOf(image).GetProperty("material")?.SetValue(image,null);
            Interop.TypeOf(image).GetProperty("overrideSprite")?.SetValue(image,null);
            Set(image,"sprite",sprite!); Set(image,"type",1);
        }
    }

    private object? InParents(object node, Type type)
    {
        var get = context.Require(Core, "UnityEngine.GameObject", "GetComponent", "System.Type");
        for (object? p = node; Alive(p); p = Get(p!, "parent"))
        {
            var c = get.Invoke(Get(p!, "gameObject"), new[] { Native(type) });
            if (Alive(c)) return As(c!, type);
        }
        return null;
    }

    private bool Locate(object page)
    {
        if (Alive(level) && Alive(switcher) && Alive(contentCanvas) && Alive(headerCanvas) && Alive(headerFade)) return true;
        if (Environment.TickCount64 < nextLookup) return false;
        nextLookup = Environment.TickCount64 + 250;
        var levelType = context.FindType("Assembly-CSharp","Campus.Common.ProduceLevelButtonView");
        var headerType = context.FindType("Assembly-CSharp","Campus.OutGame.OutGameHeaderView");
        var canvas = context.FindType("UnityEngine.UIModule","UnityEngine.Canvas");
        if (levelType == null || headerType == null || canvas == null) return false;
        var find = context.Require(Core, "UnityEngine.Object","FindObjectsOfType","System.Type","System.Boolean");
        var controls = (IEnumerable)find.Invoke(null, new[] { Native(levelType), false })!;
        foreach (var item in controls)
        {
            var tr = Rect(As(item, levelType));
            if (InParents(tr, headerType) is not { } header) continue;
            level = tr; headerFade = Get(header,"_contentFade"); break;
        }
        var homeSwitcher = Call(Get(page,"transform")!, "Find", "PageRoot/ContentRoot/LeftRoot/CharacterSwitcherRoot");
        if (!Alive(level) || !Alive(homeSwitcher)) return false;
        switcher = As(homeSwitcher!, rectTransform);
        var source = InParents(level!, canvas); var target = InParents(box!, canvas);
        if (source == null || target == null) return false;
        headerCanvas = Get(source, "rootCanvas"); contentCanvas = Get(target,"rootCanvas");
        return Alive(headerCanvas) && Alive(contentCanvas) && Alive(headerFade);
    }

    private object? LocalPoint(object source, float x, float y, object parent)
    {
        transformPoint ??= context.Require(Core,"UnityEngine.Transform","TransformPoint","UnityEngine.Vector3");
        var xyz = Interop.New(transformPoint.GetParameters()[0].ParameterType, x, y, 0f);
        var world = transformPoint.Invoke(source, new[] { xyz });
        const string ui = "UnityEngine.UIModule", utility = "UnityEngine.RectTransformUtility";
        worldToScreen ??= context.Require(ui,utility,"WorldToScreenPoint","UnityEngine.Camera","UnityEngine.Vector3");
        screenToLocal ??= context.Require(ui,utility,"ScreenPointToLocalPointInRectangle","UnityEngine.RectTransform",
            "UnityEngine.Vector2","UnityEngine.Camera","UnityEngine.Vector2&");
        object? Camera(object canvas) => Convert.ToInt32(Get(canvas,"renderMode")) == 0 ? null : Get(canvas,"worldCamera");
        var point = worldToScreen.Invoke(null,new[] { Camera(headerCanvas!), world });
        var args = new[] { parent, point, Camera(contentCanvas!), null };
        return (bool)screenToLocal.Invoke(null,args)! ? args[3] : null;
    }

    public bool Position(object page)
    {
        if (layoutUnavailable) return false;
        try
        {
            if (!Locate(page)) return false;
            var parent = As(Get(box!, "parent")!, rectTransform);
            var content = Get(parent, "rect")!;
            var header = As(Get(level!, "parent")!, rectTransform);
            var h = Get(header, "rect")!; var l = Get(level!, "rect")!;
            var left = LocalPoint(header, Number(h,"x")+config.LeftOffset, Number(h,"y")+Number(h,"height"),parent);
            var bottom = LocalPoint(level!, Number(l,"x"), Number(l,"y"),parent);
            if (left == null || bottom == null) return false;
            var r = Get(As(Get(switcher!, "parent")!, rectTransform),"rect")!;
            if (Number(r,"height") <= 0) return false;
            var button = Get(switcher!, "rect")!;
            var min = Number(Get(switcher!, "anchorMin")!, "y");
            var max = Number(Get(switcher!, "anchorMax")!, "y");
            var anchor = min + (max-min)*Number(Get(switcher!, "pivot")!, "y");
            var offset = Number(Get(switcher!,"anchoredPosition")!,"y") + Number(button,"y") + Number(button,"height");
            var top = Number(content,"y") + Number(content,"height")*(anchor + offset/Number(r,"height"));
            Vector(box!, "anchoredPosition", Number(left,"x")-Number(content,"x"),
                (Number(bottom,"y")+top)/2-Number(content,"y")-Number(content,"height")/2);
            return FollowFade() >= 0;
        }
        catch { layoutUnavailable = true; throw; }
    }
}
