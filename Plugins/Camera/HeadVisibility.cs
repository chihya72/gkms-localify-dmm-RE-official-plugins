using Gkms.Sdk;
using System.Reflection;

namespace Gkms.Plugins.Camera;

internal sealed class HeadVisibility
{
    private readonly MethodInfo alive;
    private object? model;
    private readonly List<object> hidden = new();
    public HeadVisibility(MethodInfo alive) => this.alive = alive;
    private bool Alive(object? value) => value != null && (bool)alive.Invoke(null, new[] { value })!;

    public void Track(object? next)
    {
        if (Alive(next) && Equals(next, model)) return;
        Restore();
        if (!Alive(next)) return;
        model = next;
        var find = Interop.TypeOf(next!).GetMethod("Find", new[] { typeof(string) })!;
        foreach (var path in new[] { "Root_Face/VLSkinningRenderer", "Root_Hair" })
        {
            var node = find.Invoke(next, new object[] { path });
            if (!Alive(node)) continue;
            var obj = Interop.TypeOf(node!).GetProperty("gameObject")!.GetValue(node)!;
            if (!(bool)Interop.TypeOf(obj).GetProperty("activeSelf")!.GetValue(obj)!) continue;
            Interop.TypeOf(obj).GetMethod("SetActive")!.Invoke(obj, new object[] { false });
            hidden.Add(obj);
        }
    }

    public void Restore()
    {
        foreach (var obj in hidden)
            if (Alive(obj)) Interop.TypeOf(obj).GetMethod("SetActive")!.Invoke(obj, new object[] { true });
        hidden.Clear(); model = null;
    }
}
