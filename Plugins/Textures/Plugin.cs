using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Textures;

public sealed class Plugin : ILocalifyPlugin, IResourceReloadable, IPluginDiagnostics
{
    private static readonly Dictionary<string, List<string>> files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, byte[]> loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> invalid = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Warnings => invalid.Count == 0 ? Array.Empty<string>() :
        new[] { $"{invalid.Count} 张已匹配贴图替换失败，已替换 {originals.Count} 张。首个失败：{Path.GetFileName(invalid.First().Key)}：{invalid.First().Value}" };
    private static Type texture = null!;
    private static MethodInfo loadImage = null!, destroy = null!, alive = null!;
    private static IPluginContext? context;
    private sealed record Changed(TextureSnapshot Original, byte[] Applied);
    private static readonly Dictionary<object, Changed> originals = new();
    public void Start(IPluginContext session)
    {
        context = session;
        Index();
        texture = session.RequireType("UnityEngine.CoreModule", "UnityEngine.Texture2D");
        var conversions = session.RequireType("UnityEngine.ImageConversionModule", "UnityEngine.ImageConversion");
        loadImage = conversions.GetMethods(GameMethods.Declared).Single(m => m.Name == "LoadImage" &&
            m.GetParameters() is [{ ParameterType: var t }, { ParameterType: var buffer }, { ParameterType: var b }] &&
            t == texture && Interop.IsByteArray(buffer) && b == typeof(bool));
        destroy = session.Require("UnityEngine.CoreModule", "UnityEngine.Object", "Destroy", "UnityEngine.Object");
        alive = session.Require("UnityEngine.CoreModule", "UnityEngine.Object", "op_Implicit", "UnityEngine.Object");
        var target = session.Require("UnityEngine.UIModule", "UnityEngine.CanvasRenderer", "SetTexture", "UnityEngine.Texture");
        session.Patch(target, GameMethods.Handler<Plugin>(nameof(Replace)).MakeGenericMethod(Interop.CallbackType(target.GetParameters()[0].ParameterType)));
        foreach (var item in LiveGameState.FindObjects(session, texture, assets: true)) ReplaceObject(item);
        session.Log($"已替换 {originals.Count} 张现有贴图；{invalid.Count} 张失败。");
        Gkms.Resources.AutoResourceUpdate.Start(session, new ResourceUpdater(), "useAPITextureAssets", ReloadResources);
    }

    private static void Index()
    {
        var dir = ResourceUpdater.ResourceDirectory;
        var next = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string file)
        {
            if (!next.TryGetValue(key, out var candidates)) next.Add(key, candidates = new());
            candidates.Add(file);
        }
        var paths = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToArray() : Array.Empty<string>();
        foreach (var file in paths)
        {
            var name = Path.GetFileName(file);
            Add("direct/" + Path.GetRelativePath(dir, file).Replace('\\', '/'), file);
            var category = Path.GetRelativePath(dir, file).Split(Path.DirectorySeparatorChar)[0];
            if (category is "image" or "atlas" or "others" && Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                // DMM retains every matching path, in filesystem enumeration order.
                Add("recursive/" + category + "/" + name, file);
                Add("recursive/" + category + "/" + Path.GetFileNameWithoutExtension(file), file);
            }
        }
        files.Clear(); foreach (var pair in next) files.Add(pair.Key, pair.Value);
        context!.Log($"已索引 {files.Values.SelectMany(paths => paths).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 张贴图，资源目录: {dir}");
    }

    public static string? Portable(string name) => TextureNames.Portable(name);
    private static IEnumerable<string> Find(object original)
    {
        var name = Interop.TypeOf(original).GetProperty("name")?.GetValue(original) as string;
        if (string.IsNullOrEmpty(name)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Candidates(name))
            if (files.TryGetValue(key, out var candidates))
                foreach (var file in candidates)
                    if (seen.Add(file)) yield return file;
    }
    private static byte[]? FindBytes(object original, Dictionary<string, byte[]> cache, bool reload, out string file)
    {
        file = "";
        Exception? last = null;
        foreach (var candidate in Find(original))
        {
            file = candidate;
            if (!reload && invalid.ContainsKey(candidate)) continue;
            try
            {
                if (!cache.TryGetValue(candidate, out var bytes)) cache[candidate] = bytes = Load(candidate);
                return bytes;
            }
            catch (Exception error)
            {
                last = error;
                invalid[candidate] = error.GetBaseException().Message;
                context?.Log($"贴图候选解码失败，继续下一候选 {candidate}: {error.GetBaseException().Message}");
            }
        }
        if (reload && last != null) throw new InvalidDataException("所有已匹配贴图候选均解码失败。", last);
        return null;
    }
    internal static IEnumerable<string> Candidates(string name)
    {
        name = name.Replace('\\', '/');
        var safe = name.Replace('|', '_');
        var category = TextureNames.Category(safe);
        IEnumerable<string> Direct(string candidate)
        {
            yield return "direct/" + category + "/" + candidate;
            if (!Path.HasExtension(candidate)) yield return "direct/" + category + "/" + candidate + ".png";
        }
        foreach (var candidate in Direct(name)) yield return candidate;
        if (safe != name) foreach (var candidate in Direct(safe)) yield return candidate;
        yield return "recursive/" + category + "/" + safe;
        if (Portable(safe) is { } portable && portable != safe && portable != name)
        {
            foreach (var candidate in Direct(portable)) yield return candidate;
            yield return "recursive/" + category + "/" + portable;
        }
    }
    private static byte[] Load(string file)
    {
        var created = Interop.New(texture, 2, 2)!;
        try
        {
            var data = File.ReadAllBytes(file);
            LoadInto(created, data);
            return data;
        }
        finally { destroy.Invoke(null, new[] { created }); }
    }
    private static void LoadInto(object target, byte[] data)
    {
        if (!(bool)loadImage.Invoke(null, new[] { target, Interop.WrapBytes(loadImage.GetParameters()[1].ParameterType, data), false })!)
            throw new InvalidDataException("PNG 贴图解码失败。");
        if (Interop.TypeOf(target).GetProperty("hideFlags") is { CanRead: true, CanWrite: true } flags)
        {
            var value = Convert.ToInt32(flags.GetValue(target)) | 32; // DontUnloadUnusedAsset, HookTexture.cpp.
            flags.SetValue(target, flags.PropertyType.IsEnum ? Enum.ToObject(flags.PropertyType, value) : value);
        }
    }

    public static void Replace<T>(object __instance, ref T __0) where T : class
    {
        if (__0 != null) ReplaceObject(__0);
    }
    private static void ReplaceObject(object original)
    {
        if (!texture.IsInstanceOfType(original) || originals.ContainsKey(original)) return;
        var file = "";
        try
        {
            var bytes = FindBytes(original, loaded, false, out file);
            if (bytes == null) return;
            var snapshot = new TextureSnapshot(context!, original);
            // Own the backup before touching Unity: a failed decoder may already have changed the texture.
            originals.Add(original, new(snapshot, bytes));
            try { LoadInto(original, bytes); context?.Log("已替换贴图: " + snapshot.Description); }
            catch
            {
                snapshot.Restore(); // If restoration also fails, keep the backup for disable/reload.
                originals.Remove(original); snapshot.Dispose(); throw;
            }
        }
        catch (Exception e)
        {
            invalid[file] = e.GetBaseException().Message;
            context?.Log($"贴图替换失败 {file}: {e}");
        }
    }
    public void Dispose()
    {
        Restore();
        loaded.Clear(); files.Clear(); invalid.Clear(); context = null;
    }
    private static void Restore()
    {
        var errors = new List<Exception>();
        var count = originals.Count; var index = 0;
        foreach (var (texture, state) in originals.ToArray())
        {
            try
            {
                context?.Log($"恢复原图 {++index}/{count}: {state.Original.Description}");
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                if ((bool)alive.Invoke(null, new[] { texture })!) state.Original.Restore();
                originals.Remove(texture); state.Original.Dispose();
                context?.Log($"原图恢复完成 {index}/{count}，耗时 {elapsed.ElapsedMilliseconds} ms。");
            }
            catch (Exception e) { errors.Add(e); context?.Log("恢复原图失败，保留备份供重试: " + e.GetBaseException().Message); }
        }
        if (errors.Count != 0) throw new AggregateException("部分原图未恢复，请重试停用贴图。", errors);
        context?.Log($"已恢复 {count} 张原图，贴图替换已停用。");
    }
    public void ReloadResources()
    {
        var oldIndex = files.ToArray();
        var prepared = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targets = originals.Keys.Concat(LiveGameState.FindObjects(context!, texture, assets: true)).Distinct()
            .Where(t => (bool)alive.Invoke(null, new[] { t })!).ToArray();
        var changes = new Dictionary<object, (Changed State, byte[]? Next, bool Added)>();
        try
        {
            Index();
            foreach (var original in targets)
            {
                var bytes = FindBytes(original, prepared, true, out _);
                if (originals.TryGetValue(original, out var state)) changes.Add(original, (state, bytes, false));
                else if (bytes != null) changes.Add(original, (new(new TextureSnapshot(context!, original), bytes), bytes, true));
            }
        }
        catch
        {
            foreach (var change in changes.Values.Where(c => c.Added)) change.State.Original.Dispose();
            files.Clear(); foreach (var pair in oldIndex) files.Add(pair.Key, pair.Value);
            throw;
        }
        var committed = new List<object>();
        try
        {
            foreach (var (target, change) in changes)
            {
                committed.Add(target);
                if (change.Next == null) change.State.Original.Restore(); else LoadInto(target, change.Next);
            }
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            var retained = new HashSet<object>();
            foreach (var target in committed.AsEnumerable().Reverse())
            {
                var change = changes[target];
                try
                {
                    if (change.Added) change.State.Original.Restore(); else LoadInto(target, change.State.Applied);
                }
                catch (Exception rollbackError)
                {
                    errors.Add(rollbackError);
                    if (change.Added) { originals[target] = change.State; retained.Add(target); }
                    context?.Log("贴图回滚未完成，保留原图供停用时重试: " + rollbackError.GetBaseException().Message);
                }
            }
            foreach (var (target, change) in changes)
                if (change.Added && !retained.Contains(target)) change.State.Original.Dispose();
            files.Clear(); foreach (var pair in oldIndex) files.Add(pair.Key, pair.Value);
            if (errors.Count > 1) throw new AggregateException("贴图重载失败，部分回滚待重试。", errors);
            throw;
        }
        foreach (var (target, change) in changes)
            if (change.Next == null) { originals.Remove(target); change.State.Original.Dispose(); }
            else originals[target] = change.State with { Applied = change.Next };
        loaded.Clear(); foreach (var pair in prepared) loaded.Add(pair.Key, pair.Value);
        invalid.Clear();
        context?.Log($"贴图资源重载完成，当前已替换 {originals.Count} 张。");
    }
}
