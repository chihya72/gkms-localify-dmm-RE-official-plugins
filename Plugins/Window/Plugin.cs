using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Gkms.Sdk;
using ImGuiNET;

namespace Gkms.Plugins.Window;

public sealed class Plugin : ILocalifyPlugin, IPluginDiagnostics
{
    private static readonly HashSet<string> warnings = new();
    public IReadOnlyList<string> Warnings => warnings.ToArray();
    private static string orientation = "";
    private static bool resize;
    private static IntPtr handle;
    private static int addedStyle;
    private static PropertyInfo? width, height;
    private static MethodInfo? setResolution;
    private static int rememberedWidth, rememberedHeight;
    private static bool fullscreen;
    private static bool subclassAttached, f11Down;
    private const string SubclassWarning = "未接入窗口消息；F11 使用前台按键检测，窗口边缘拖动及最大化消息可能受游戏限制。";
    private static int toggleRequested, savedStyle, savedClientWidth, savedClientHeight;
    private static Rect savedRect, fullRect;
    private static readonly SubclassProcedure procedure = WindowMessage;
    private static readonly UIntPtr subclassId = new(0x474b4d53);
    private static IPluginContext? session;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    private const int Style = -16, ResizeStyle = 0x00040000 | 0x00010000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetStyle(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetStyle(IntPtr h, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    private delegate bool EnumWindow(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    public void Start(IPluginContext context)
    {
        session = context;
        orientation = context.Option("orientation", "game");
        resize = context.Option("resizable", true);
        f11Down = (GetAsyncKeyState(0x7a) & 0x8000) != 0;
        if (orientation is not ("game" or "portrait" or "landscape")) throw new InvalidDataException("orientation 应为 game、portrait 或 landscape。");
        if (orientation != "game")
        {
            var method = context.RequireType("campus-submodule.Runtime", "Campus.Common.ScreenOrientationControllerBase")
                .GetMethods(GameMethods.Declared).Single(m => m.Name == "InternalSetOrientationAsync" && m.GetParameters().Length == 3);
            context.Patch(method, GameMethods.Handler<Plugin>(nameof(Orient)).MakeGenericMethod(Interop.CallbackType(method.GetParameters()[0].ParameterType)));
        }
        context.Patch(context.Require("campus-submodule.Runtime", "Campus.Common.CampusQualityManager", "Update"),
            postfix: GameMethods.Handler<Plugin>(nameof(ApplyWindow)));
        var nudge = context.Find("Assembly-CSharp", "Campus.Common.StandAloneWindow.AspectRatioHandler", "NudgeWindow");
        if (nudge != null) context.Patch(nudge, GameMethods.Handler<Plugin>(nameof(SkipNudge)));
        var manager = context.RequireType("Assembly-CSharp", "Campus.Common.StandAloneWindow.WindowManager");
        var orientWindow = manager.GetMethods(GameMethods.Declared).Single(m => m.Name == "ApplyOrientationSettings" && m.GetParameters().Length == 1);
        var screen = context.RequireType("UnityEngine.CoreModule", "UnityEngine.Screen");
        width = screen.GetProperty("width"); height = screen.GetProperty("height");
        setResolution = context.Require("UnityEngine.CoreModule", "UnityEngine.Screen", "SetResolution", "System.Int32", "System.Int32", "System.Boolean");
        context.Patch(orientWindow, GameMethods.Handler<Plugin>(nameof(KeepWindowSize)));
        var windowStyle = context.RequireType("Assembly-CSharp", "Campus.Common.StandAloneWindow.WindowHandle")
            .GetMethods(GameMethods.Declared).Single(m => m.Name == "SetWindowLong" && m.GetParameters().Length == 2);
        context.Patch(windowStyle, GameMethods.Handler<Plugin>(nameof(KeepWindowProcedure)));
    }
    public static void Orient<T>(ref T __0) where T : struct, Enum =>
        __0 = (T)Enum.ToObject(typeof(T), orientation == "portrait" ? 2 : 3);
    public static bool SkipNudge() => !resize && !fullscreen;
    public static bool KeepWindowProcedure(int __0, ref IntPtr __result)
    { if (!resize || __0 != -4) return true; __result = IntPtr.Zero; return false; }
    public static bool KeepWindowSize()
    {
        if (fullscreen) return false;
        if (!resize) return true;
        var currentWidth = Convert.ToInt32(width!.GetValue(null));
        var currentHeight = Convert.ToInt32(height!.GetValue(null));
        if (rememberedWidth > 0 && rememberedHeight > 0 && (rememberedWidth < rememberedHeight) != (currentWidth < currentHeight))
        {
            setResolution!.Invoke(null, new object[] { rememberedWidth, rememberedHeight, false });
        }
        rememberedWidth = currentWidth; rememberedHeight = currentHeight;
        return false;
    }
    public static void ApplyWindow()
    {
        try
        {
            ApplyWindowCore();
            warnings.RemoveWhere(warning => warning != SubclassWarning);
        }
        catch (Exception error)
        {
            var warning = "窗口功能异常: " + error.GetBaseException().Message;
            if (warnings.Add(warning)) session?.Log(warning);
        }
    }
    private static void ApplyWindowCore()
    {
        if (handle != IntPtr.Zero && IsWindow(handle))
        {
            // Unity/overlays can consume WM_KEYDOWN before our subclass sees it.
            // Share the edge state with the message path so one press toggles only once.
            PollFullscreenKey((GetAsyncKeyState(0x7a) & 0x8000) != 0, GetForegroundWindow() == handle);
            if (Interlocked.Exchange(ref toggleRequested, 0) != 0) ToggleFullscreen();
            if (!fullscreen)
            {
                var current = GetStyle(handle, Style);
                if (resize && (current & unchecked((int)0x80000000)) == 0 && (current & ResizeStyle) != ResizeStyle)
                {
                    addedStyle |= ResizeStyle & ~current;
                    SetStyle(handle, Style, current | ResizeStyle);
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
                }
            }
            else
            {
                // Unity may resize/reapply styles after its asynchronous resolution change.
                var current = GetStyle(handle, Style);
                var desired = (current & ~0x00cf0000) | unchecked((int)0x80000000);
                if (current != desired) SetStyle(handle, Style, desired);
                if (GetWindowRect(handle, out var rect) && (current != desired || !rect.Equals(fullRect)))
                    SetWindowPos(handle, IntPtr.Zero, fullRect.Left, fullRect.Top, fullRect.Right-fullRect.Left, fullRect.Bottom-fullRect.Top, 0x34);
            }
            return;
        }
        handle = IntPtr.Zero;
        fullscreen = false; subclassAttached = false; toggleRequested = 0;
        var window = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var pid);
            if (pid != Environment.ProcessId) return true;
            var name = new StringBuilder(128); GetClassName(candidate, name, name.Capacity);
            if (name.ToString() != "UnityWndClass") return true;
            window = candidate; return false;
        }, IntPtr.Zero);
        if (window == IntPtr.Zero) return;
        var original = GetStyle(window, Style);
        if (original == 0) return;
        addedStyle = resize ? ResizeStyle & ~original : 0;
        if (addedStyle != 0 && SetStyle(window, Style, original | addedStyle) == 0) return;
        handle = window;
        subclassAttached = SetWindowSubclass(window, procedure, subclassId, UIntPtr.Zero);
        if (!subclassAttached)
        {
            if (warnings.Add(SubclassWarning)) session?.Log(PluginLogLevel.Warning, SubclassWarning);
        }
        else warnings.Remove(SubclassWarning);
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37);
    }
    internal static void PollFullscreenKey(bool down, bool foreground)
    {
        if (down && !f11Down && foreground) Interlocked.Exchange(ref toggleRequested, 1);
        f11Down = down;
    }
    private static IntPtr WindowMessage(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if ((message == 0x100 || message == 0x104) && wparam.ToUInt64() == 0x7a)
            {
                if ((lparam.ToInt64() & (1L << 30)) == 0 && !f11Down) Interlocked.Exchange(ref toggleRequested, 1);
                f11Down = true; return IntPtr.Zero;
            }
            if ((message == 0x101 || message == 0x105) && wparam.ToUInt64() == 0x7a)
            { f11Down = false; return IntPtr.Zero; }
            if (message == 0x112 && (wparam.ToUInt64() & 0xfff0) == 0xf030)
            { Interlocked.Exchange(ref toggleRequested, 1); return IntPtr.Zero; }
            if (message == 0x24 && resize && !fullscreen)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(MonitorFromWindow(window, 2), ref info))
                {
                    var limits = Marshal.PtrToStructure<MinMaxInfo>(lparam);
                    limits.MinTrack = new Point { X = 200, Y = 200 };
                    limits.MaxTrack = new Point { X = (info.Monitor.Right-info.Monitor.Left)*3, Y = (info.Monitor.Bottom-info.Monitor.Top)*3 };
                    Marshal.StructureToPtr(limits, lparam, false); return IntPtr.Zero;
                }
            }
            if (message == 0x84 && resize && !fullscreen && GetWindowRect(window, out var bounds))
            {
                var x = (short)(lparam.ToInt64() & 0xffff); var y = (short)((lparam.ToInt64() >> 16) & 0xffff);
                var border = Math.Max(8, (int)GetDpiForWindow(window)*8/96);
                var left = x < bounds.Left+border; var right = x >= bounds.Right-border;
                var top = y < bounds.Top+border; var bottom = y >= bounds.Bottom-border;
                if (left || right || top || bottom)
                    return new IntPtr(top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : 11);
            }
        }
        catch (Exception error) { session?.Log("窗口消息处理失败: " + error.GetBaseException().Message); }
        return DefSubclassProc(window, message, wparam, lparam);
    }
    private static void ToggleFullscreen()
    {
        if (fullscreen) { RestoreWindow(); return; }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out savedRect) || !GetClientRect(handle, out var client))
            throw new InvalidOperationException("无法读取游戏所在显示器或窗口尺寸。");
        savedStyle = GetStyle(handle, Style); savedClientWidth = client.Right; savedClientHeight = client.Bottom;
        fullRect = info.Monitor; fullscreen = true;
        try
        {
            setResolution!.Invoke(null, new object[] { fullRect.Right-fullRect.Left, fullRect.Bottom-fullRect.Top, false });
            SetStyle(handle, Style, (savedStyle & ~0x00cf0000) | unchecked((int)0x80000000));
            SetWindowPos(handle, IntPtr.Zero, fullRect.Left, fullRect.Top, fullRect.Right-fullRect.Left, fullRect.Bottom-fullRect.Top, 0x34);
        }
        catch { RestoreWindow(); throw; }
    }
    private static void RestoreWindow()
    {
        if (!fullscreen || handle == IntPtr.Zero || !IsWindow(handle)) return;
        rememberedWidth = savedClientWidth; rememberedHeight = savedClientHeight;
        try { setResolution?.Invoke(null, new object[] { savedClientWidth, savedClientHeight, false }); }
        finally
        {
            SetStyle(handle, Style, savedStyle);
            SetWindowPos(handle, IntPtr.Zero, savedRect.Left, savedRect.Top, savedRect.Right-savedRect.Left, savedRect.Bottom-savedRect.Top, 0x34);
            fullscreen = false;
        }
    }
    public void Dispose()
    {
        if (handle != IntPtr.Zero && IsWindow(handle))
        {
            if (subclassAttached && !RemoveWindowSubclass(handle, procedure, subclassId)) throw new InvalidOperationException("窗口消息尚未解除，请重试停用。");
            RestoreWindow();
        }
        if (handle != IntPtr.Zero && addedStyle != 0 && IsWindow(handle))
        {
            SetStyle(handle, Style, GetStyle(handle, Style) & ~addedStyle);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }
        handle = IntPtr.Zero;
        rememberedWidth = rememberedHeight = 0; width = height = null; setResolution = null;
        toggleRequested = 0; fullscreen = subclassAttached = f11Down = false; session = null; warnings.Clear();
    }
}

public sealed class WindowSettingsUi : IPluginSettingsUi
{
    private static readonly string[] Values = { "game", "portrait", "landscape" };
    private ISettingsUiContext? context;
    private bool open, resizable = true;
    private int selected;
    private string message = "";
    private string Text(string cn, string tw, string en, string ja) => context!.Language switch
    { "zh-TW" => tw, "en" => en, "ja" => ja, _ => cn };

    public void Initialize(ISettingsUiContext value)
    {
        context = value;
        Read();
        context.UiBuilder.OpenConfigUi += Open;
        context.UiBuilder.Draw += Draw;
    }
    private void Open() => open = true;
    private void Read()
    {
        var settings = context!.Settings;
        var orientation = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("orientation", out var value)
            ? value.GetString() : "game";
        selected = Array.IndexOf(Values, orientation);
        if (selected < 0) throw new InvalidDataException("未知的窗口旋转方式。");
        resizable = settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("resizable", out var resize) || resize.GetBoolean();
    }
    private void Draw()
    {
        if (!open || context == null) return;
        var labels = context.Language switch
        {
            "en" => new[] { "Auto rotate", "Force portrait", "Force landscape" },
            "ja" => new[] { "自動回転", "縦向き固定", "横向き固定" },
            "zh-TW" => new[] { "自動旋轉", "強制豎屏", "強制橫屏" },
            _ => new[] { "自动旋转", "强制竖屏", "强制横屏" }
        };
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X * .52f));
        ImGui.Combo(Text("旋转方式", "旋轉方式", "Orientation", "画面の向き"), ref selected, labels, labels.Length);
        ImGui.Checkbox(Text("自由调整大小", "自由調整大小", "Resizable", "サイズ変更可能"), ref resizable);
        PluginUiText.Wrapped(Text(
            "F11：强制在游戏窗口所在屏幕无边框全屏，不受旋转方式或自由调整大小开关限制；再按一次恢复原位置和尺寸。",
            "F11：強制在遊戲視窗所在螢幕無邊框全螢幕，不受旋轉方式或自由調整大小開關限制；再按一次還原原位置和尺寸。",
            "F11 forces borderless fullscreen on the game window's current monitor, regardless of orientation or Resizable. Press again to restore position and size.",
            "F11：向きやサイズ変更設定に関係なく、ゲームウィンドウがあるモニターでボーダーレス全画面に切り替えます。再度押すと元の位置・サイズに戻ります。"));
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        if (ImGui.Button(Text("保存配置", "儲存設定", "Save settings", "設定を保存")))
        {
            try
            {
                // Display labels are localized; persisted values remain compatible with existing profiles.
                context.SaveSettings(JsonSerializer.SerializeToElement(new { orientation = Values[selected], resizable }));
                message = Text("配置已保存。", "設定已儲存。", "Settings saved.", "設定を保存しました。");
            }
            catch (Exception error) { message = error.GetBaseException().Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button(Text("重新读取", "重新讀取", "Reload settings", "設定を再読込")))
        {
            try { context.ReloadSettings(); Read(); message = ""; }
            catch (Exception error) { message = error.GetBaseException().Message; }
        }
        if (message.Length > 0) PluginUiText.Wrapped(message);
    }
    public void Dispose()
    {
        open = false;
        if (context == null) return;
        context.UiBuilder.OpenConfigUi -= Open;
        context.UiBuilder.Draw -= Draw;
        context = null;
    }
}
