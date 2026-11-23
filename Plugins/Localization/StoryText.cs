using System.Reflection;
using System.Text;
using Gkms.Sdk;

namespace Gkms.Plugins.Localization;

public sealed class StoryText : ILocalifyPlugin, IPluginDiagnostics
{
    private static string? root;
    private static MethodInfo? invoke;
    private static IPluginContext? session;
    private static readonly Dictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Warnings => failures.Values.ToArray();
    public void Start(IPluginContext context)
    {
        session = context;
        root = Path.Combine(ResourceUpdater.ResourceDirectory, "resource");
        var loader = context.RequireType("Octo", "Octo.Loader.OctoResourceLoader");
        var method = loader.GetMethods(GameMethods.Declared).Single(m => m.Name == "LoadFromCacheOrDownload" &&
            m.GetParameters() is [{ ParameterType: var name }, { ParameterType: var callback }, _] && name == typeof(string) &&
            callback.IsGenericType && callback.GetGenericArguments()[0].FullName is "System.String" or "Il2CppSystem.String");
        invoke = method.GetParameters()[1].ParameterType.GetMethod("Invoke") ?? throw new MissingMethodException("Octo callback");
        context.Patch(method, GameMethods.Handler<StoryText>(nameof(Replace)));
    }
    public static bool Replace(string __0, object __1)
    {
        if (root == null || invoke == null || __1 == null) return true;
        var file = Interop.LocalFile(root, __0);
        if (file == null) return true;
        failures.Remove(file);
        // Match DMM GetResourceText: read only the requested file, and fall back
        // to the original loader if it is absent or cannot be read.
        if (!ResourceFiles.TryRead(file, ReadResourceText, message =>
            { failures[file] = message; session?.Log(message); }, out var text)) return true;
        var parameters = invoke.GetParameters();
        var success = parameters[1].ParameterType.IsValueType ? Interop.New(parameters[1].ParameterType) : null;
        var translated = Interop.ConvertString(parameters[0].ParameterType, text);
        HookExecution.MarkReplacement();
        invoke.Invoke(__1, new[] { translated, success });
        return false;
    }
    private static string ReadResourceText(string file)
    {
        // Local.cpp uses Windows std::ifstream text mode, then String::New(char*).
        // Preserve the UTF-8 BOM; do not auto-detect UTF-16. CRT text mode folds
        // CRLF and ends at Ctrl-Z; the subsequent char* conversion ends at NUL.
        var bytes = File.ReadAllBytes(file);
        var count = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            if (value is 0 or 0x1a) break;
            if (value == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n') continue;
            bytes[count++] = value;
        }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    public void Dispose() { root = null; invoke = null; session = null; failures.Clear(); }
}
