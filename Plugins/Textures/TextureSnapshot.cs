using System.Reflection;
using System.Runtime.InteropServices;
using Gkms.Sdk;

namespace Gkms.Plugins.Textures;

internal sealed class TextureSnapshot : IDisposable
{
    private readonly object original, format;
    private readonly int width, height;
    private readonly bool mipmaps, readable;
    private readonly MethodInfo reinitialize, apply, loadImage;
    private readonly MethodInfo? loadRaw;
    private readonly Dictionary<PropertyInfo, object?> sampling = new();
    private byte[] png;
    private byte[]? raw;
    public string Description { get; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ReadBool();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WriteBool(byte value);
    private static (Func<bool> Read, Action<bool> Write) ColorWriteAccess(Type gl)
    {
        if (gl.GetProperty("sRGBWrite") is { CanRead: true, CanWrite: true } property)
            return (() => Convert.ToBoolean(property.GetValue(null)), value => property.SetValue(null, value));
        if (gl is INativeInternalCalls native)
        {
            var get = native.ResolveInternalCall("UnityEngine.GL::get_sRGBWrite()");
            var set = native.ResolveInternalCall("UnityEngine.GL::set_sRGBWrite(System.Boolean)");
            if (get != IntPtr.Zero && set != IntPtr.Zero)
            {
                var read = Marshal.GetDelegateForFunctionPointer<ReadBool>(get);
                var write = Marshal.GetDelegateForFunctionPointer<WriteBool>(set);
                return (() => read() != 0, value => write(value ? (byte)1 : (byte)0));
            }
        }
        throw new MissingMethodException("原图读回缺少 GL.sRGBWrite 托管及原生接口。");
    }

    public TextureSnapshot(IPluginContext context, object texture)
    {
        original = texture;
        var type = Interop.TypeOf(texture);
        width = Convert.ToInt32(Interop.Get(texture, "width")); height = Convert.ToInt32(Interop.Get(texture, "height"));
        format = Interop.Get(texture, "format")!;
        mipmaps = Convert.ToInt32(Interop.Get(texture, "mipmapCount")) > 1;
        readable = Convert.ToBoolean(Interop.Get(texture, "isReadable"));
        Description = $"{Interop.Get(texture, "name")} ({width}x{height}, {format}, readable={readable})";
        reinitialize = type.GetMethods().Single(m => m.Name == "Reinitialize" && m.GetParameters() is
            [_, _, { ParameterType: var f }, { ParameterType: var b }] && f == Interop.TypeOf(format) && b == typeof(bool));
        apply = type.GetMethod("Apply", new[] { typeof(bool), typeof(bool) })!;
        loadImage = context.RequireType("UnityEngine.ImageConversionModule", "UnityEngine.ImageConversion").GetMethods(GameMethods.Declared)
            .Single(m => m.Name == "LoadImage" && m.GetParameters() is [_, { ParameterType: var p }, { ParameterType: var b }] && Interop.IsByteArray(p) && b == typeof(bool));
        foreach (var key in new[] { "filterMode", "wrapMode", "wrapModeU", "wrapModeV", "anisoLevel", "mipMapBias", "hideFlags" })
            if (type.GetProperty(key) is { CanRead: true, CanWrite: true } property) sampling[property] = property.GetValue(texture);
        // LoadImage(false) does NOT turn an imported non-readable texture into a readable one.
        // Save the image before changing it; the same LoadImage path can restore it in place.
        png = ReadPng(context, texture, width, height);
        if (readable && !format.ToString()!.Contains("Crunched", StringComparison.OrdinalIgnoreCase))
        {
            var get = type.GetMethods().Single(m => m.Name == "GetRawTextureData" && !m.IsGenericMethod && m.GetParameters().Length == 0);
            raw = Interop.ReadBytes(get.Invoke(texture, null));
            loadRaw = type.GetMethods().Single(m => m.Name == "LoadRawTextureData" && m.GetParameters() is [{ ParameterType: var p }] && Interop.IsByteArray(p));
        }
    }

    private static byte[] ReadPng(IPluginContext context, object texture, int width, int height)
    {
        const string core = "UnityEngine.CoreModule", rt = "UnityEngine.RenderTexture";
        var rtType = context.RequireType(core, rt);
        var active = rtType.GetProperty("active") is { CanRead: true, CanWrite: true } target ? target
            : throw new MissingMemberException("原图读回缺少 RenderTexture.active 访问器。");
        var colorWrite = ColorWriteAccess(context.RequireType(core, "UnityEngine.GL"));
        var sourceType = Interop.TypeOf(texture);
        var srgb = sourceType.GetProperty("isDataSRGB") is { CanRead: true } color
            ? Convert.ToBoolean(color.GetValue(texture))
            : sourceType.GetProperty("graphicsFormat") is { CanRead: true } graphics
                ? graphics.GetValue(texture)!.ToString()!.EndsWith("_SRGB", StringComparison.OrdinalIgnoreCase)
                : throw new MissingMemberException("原图读回缺少贴图颜色格式信息。");
        var temporary = context.Require(core, rt, "GetTemporary", "System.Int32", "System.Int32", "System.Int32", "UnityEngine.RenderTextureFormat", "UnityEngine.RenderTextureReadWrite");
        var release = context.Require(core, rt, "ReleaseTemporary", rt);
        var blit = context.Require(core, "UnityEngine.Graphics", "Blit", "UnityEngine.Texture", rt);
        var image = context.RequireType(core, "UnityEngine.Texture2D");
        var rect = context.RequireType(core, "UnityEngine.Rect");
        var encode = context.Require("UnityEngine.ImageConversionModule", "UnityEngine.ImageConversion", "EncodeToPNG", "UnityEngine.Texture2D");
        var destroy = context.Require(core, "UnityEngine.Object", "Destroy", "UnityEngine.Object");
        var previous = active.GetValue(null); var previousSrgb = colorWrite.Read();
        object? buffer = null, cpu = null;
        try
        {
            buffer = temporary.Invoke(null, new object[] { width, height, 0,
                Enum.Parse(temporary.GetParameters()[3].ParameterType, "ARGB32"),
                Enum.Parse(temporary.GetParameters()[4].ParameterType, srgb ? "sRGB" : "Linear") });
            colorWrite.Write(srgb);
            blit.Invoke(null, new[] { texture, buffer });
            active.SetValue(null, buffer);
            var format = context.RequireType(core, "UnityEngine.TextureFormat");
            cpu = Interop.New(image, width, height, Enum.Parse(format, "RGBA32"), false, !srgb);
            Interop.Call(cpu!, "ReadPixels", Interop.New(rect, 0f, 0f, (float)width, (float)height), 0, 0, false);
            Interop.Call(cpu!, "Apply", false, false);
            var bytes = Interop.ReadBytes(encode.Invoke(null, new[] { cpu }));
            if (bytes.Length == 0) throw new InvalidDataException("原图备份返回空 PNG。");
            return bytes;
        }
        finally
        {
            active.SetValue(null, previous); colorWrite.Write(previousSrgb);
            if (buffer != null) release.Invoke(null, new[] { buffer });
            if (cpu != null) destroy.Invoke(null, new[] { cpu });
        }
    }

    public void Restore()
    {
        var restored = false;
        Exception? rawError = null;
        if (raw != null && Convert.ToBoolean(Interop.Get(original, "isReadable")))
            try
            {
                if ((bool)reinitialize.Invoke(original, new[] { (object)width, height, format, mipmaps })!)
                {
                    loadRaw!.Invoke(original, new[] { Interop.WrapBytes(loadRaw.GetParameters()[0].ParameterType, raw) });
                    apply.Invoke(original, new object[] { false, false });
                    restored = true;
                }
            }
            catch (Exception error) { rawError = error; }
        if (!restored)
            try
            {
                if (!(bool)loadImage.Invoke(null, new[] { original, Interop.WrapBytes(loadImage.GetParameters()[1].ParameterType, png), !readable })!)
                    throw new IOException("原图 PNG 恢复失败: " + Description);
            }
            catch (Exception error)
            {
                if (rawError != null) throw new AggregateException("原图的原始数据和 PNG 均未恢复: " + Description, rawError, error);
                throw;
            }
        foreach (var (property, value) in sampling) property.SetValue(original, value);
    }
    public void Dispose() { png = Array.Empty<byte>(); raw = null; }
}
