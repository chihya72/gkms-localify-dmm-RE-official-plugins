using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Capture;

internal sealed class TextureCapture : ILocalifyPlugin
{
    private static readonly HashSet<int> seen = new();
    private static MethodInfo temporary = null!, release = null!, blit = null!, read = null!, apply = null!, encode = null!, destroy = null!, instanceId = null!;
    private static PropertyInfo active = null!;
    private static Type image = null!, rect = null!;
    private static string root = "";
    private static IPluginContext? context;

    public void Start(IPluginContext ctx)
    {
        context = ctx;
        const string core = "UnityEngine.CoreModule", rt = "UnityEngine.RenderTexture", tex = "UnityEngine.Texture2D";
        root = Directory.CreateDirectory(Path.Combine(ctx.Services().PackageDirectory, "output", "texture2d")).FullName;
        temporary = ctx.Require(core, rt, "GetTemporary", "System.Int32", "System.Int32");
        release = ctx.Require(core, rt, "ReleaseTemporary", rt);
        active = ctx.RequireType(core, rt).GetProperty("active")!;
        blit = ctx.Require(core, "UnityEngine.Graphics", "Blit", "UnityEngine.Texture", rt);
        image = ctx.RequireType(core, tex);
        rect = ctx.RequireType(core, "UnityEngine.Rect");
        read = ctx.Require(core, tex, "ReadPixels", "UnityEngine.Rect", "System.Int32", "System.Int32", "System.Boolean");
        apply = ctx.Require(core, tex, "Apply", "System.Boolean", "System.Boolean");
        encode = ctx.Require("UnityEngine.ImageConversionModule", "UnityEngine.ImageConversion", "EncodeToPNG", tex);
        destroy = ctx.Require(core, "UnityEngine.Object", "Destroy", "UnityEngine.Object");
        instanceId = ctx.Require(core, "UnityEngine.Object", "GetInstanceID");
        ctx.Patch(ctx.Require("UnityEngine.UIModule", "UnityEngine.CanvasRenderer", "SetTexture", "UnityEngine.Texture"),
            GameMethods.Handler<TextureCapture>(nameof(Capture)), priority: 800);
    }

    public static void Capture(object __0)
    {
        if (__0 == null || !image.IsInstanceOfType(__0)) return;
        var id = (int)instanceId.Invoke(__0, null)!;
        if (seen.Contains(id)) return;
        object? buffer = null, cpu = null, previous = null;
        try
        {
            var type = Interop.TypeOf(__0);
            var name = type.GetProperty("name")?.GetValue(__0) as string;
            var fileName = TextureNames.DumpName(name);
            var target = Path.Combine(Directory.CreateDirectory(Path.Combine(root, TextureNames.Category(name ?? "texture"))).FullName, fileName + ".png");
            if (File.Exists(target)) { seen.Add(id); return; }
            var width = (int)type.GetProperty("width")!.GetValue(__0)!;
            var height = (int)type.GetProperty("height")!.GetValue(__0)!;
            if (width <= 0 || height <= 0) return;
            previous = active.GetValue(null);
            buffer = temporary.Invoke(null, new object[] { width, height });
            blit.Invoke(null, new[] { __0, buffer });
            active.SetValue(null, buffer);
            cpu = Interop.New(image, width, height);
            read.Invoke(cpu, new[] { Interop.New(rect, 0f, 0f, (float)width, (float)height), 0, 0, false });
            apply.Invoke(cpu, new object[] { false, false });
            var bytes = encode.Invoke(null, new[] { cpu });
            var png = Interop.ReadBytes(bytes);
            if (png.Length == 0) throw new InvalidDataException("EncodeToPNG 未返回有效字节。");
            File.WriteAllBytes(target, png);
            seen.Add(id);
        }
        catch (Exception e) { context?.Log(e.GetBaseException().Message); }
        finally
        {
            if (buffer != null) { active.SetValue(null, previous); release.Invoke(null, new[] { buffer }); }
            if (cpu != null) destroy.Invoke(null, new[] { cpu });
        }
    }
    public void Dispose() { seen.Clear(); context = null; }
}
