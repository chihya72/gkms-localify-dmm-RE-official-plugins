using System.Reflection;
using System.Runtime.InteropServices;
using Gkms.Sdk;

namespace Gkms.Plugins.Gyro;

public sealed class Plugin : ILocalifyPlugin
{
    private static MethodInfo key = null!, euler = null!, inverse = null!, multiply = null!;
    private static PropertyInfo? cursor;
    private static bool win32, dragging;
    private static Point previous;
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    private static PropertyInfo frame = null!, dt = null!;
    private static float sensitivity, pitch, yaw, roll;
    private static int last = -1, signX, signY;
    private static readonly Dictionary<object, object> injected = new();
    private static readonly Dictionary<object, Dictionary<string, object?>> restored = new();
    private static Type controllerType = null!, gyroType = null!;
    private static IPluginContext? session;
    private static bool reported;
    public void Start(IPluginContext ctx)
    {
        session = ctx; reported = false;
        sensitivity = ctx.Option("sensitivity", 1f);
        if (!float.IsFinite(sensitivity) || sensitivity <= 0) throw new InvalidDataException("sensitivity 须为正数。");
        signX = ctx.Option("invertX", false) ? -1 : 1;
        signY = ctx.Option("invertY", false) ? -1 : 1;
        const string input = "UnityEngine.InputLegacyModule";
        key = ctx.Require(input, "UnityEngine.Input", "GetKey", "System.String");
        win32 = key.DeclaringType is INativeType && OperatingSystem.IsWindows();
        cursor = ctx.RequireType(input, "UnityEngine.Input").GetProperty("mousePosition");
        euler = ctx.Require("UnityEngine.CoreModule", "UnityEngine.Quaternion", "Euler", "System.Single", "System.Single", "System.Single");
        inverse = ctx.Require("UnityEngine.CoreModule", "UnityEngine.Quaternion", "Inverse", "UnityEngine.Quaternion");
        multiply = ctx.Require("UnityEngine.CoreModule", "UnityEngine.Quaternion", "op_Multiply", "UnityEngine.Quaternion", "UnityEngine.Quaternion");
        var time = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Time");
        frame = time.GetProperty("frameCount")!; dt = time.GetProperty("unscaledDeltaTime")!;
        const string campus = "campus-submodule.Runtime", controller = "Campus.Common.CampusGyroController";
        controllerType = ctx.RequireType(campus, controller);
        gyroType = ctx.RequireType("vl-unity.Runtime", "VL.VLGyro");
        InstallInput(ctx);
        ctx.PatchAdvanced(controllerType.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(controller, ".ctor"),
            postfix: GameMethods.Handler<Plugin>(nameof(InitializeController)));
        var manager = ctx.RequireType(campus, "Campus.Live.LivePanoramaCameraManager");
        var controllerSlot = manager.GetProperty("_gyroController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (controllerSlot?.CanWrite == true)
        {
            slot = controllerSlot;
            ctx.Patch(ctx.Require(campus, manager.FullName!, "LateUpdate"), GameMethods.Handler<Plugin>(nameof(EnsureController)));
            var ready = ctx.Find(campus, manager.FullName!, "get_IsGyroReady");
            if (ready != null) ctx.Patch(ready, postfix: GameMethods.Handler<Plugin>(nameof(Supported)));
        }
        var photo = ctx.Find(campus, "Campus.Photography.PhotographyActivityController", "Initialize",
            "UnityEngine.Rendering.VolumeProfile", controller, "System.Single");
        if (photo != null) ctx.Patch(photo, GameMethods.Handler<Plugin>(nameof(ProvideController)).MakeGenericMethod(Interop.CallbackType(controllerType)));
        var support = ctx.Find("Assembly-CSharp", "Campus.Photography.Photography3DPresenter", "get_IsGyroSupported");
        if (support != null) ctx.Patch(support, postfix: GameMethods.Handler<Plugin>(nameof(Supported)));
        var panorama = ctx.Find("Assembly-CSharp", "Campus.Live.LiveScenePanoramaContentPresenter", "Initialize", "UnityEngine.RectTransform", "System.Boolean");
        if (panorama != null) ctx.Patch(panorama, GameMethods.Handler<Plugin>(nameof(Panorama)));
        foreach (var existing in LiveGameState.FindObjects(ctx, controllerType)) InitializeController(existing);
    }
    private static void InstallInput(IPluginContext ctx)
    {
        const string input = "UnityEngine.InputLegacyModule", gyro = "UnityEngine.Gyroscope";
        void Bind(MethodInfo wrapper, string signature, Delegate callback, string managed)
        {
            if (wrapper.DeclaringType is INativeCachedInternalCalls native)
                ctx.Services().Own(native.ReplaceInternalCallCache(wrapper, signature, callback));
            else if (wrapper.DeclaringType is INativeType) throw new NotSupportedException("宿主未提供 icall 缓存替换。");
            else
            {
                var handler = GameMethods.Handler<Plugin>(managed);
                if (handler.IsGenericMethodDefinition) handler = handler.MakeGenericMethod(wrapper.GetParameters()[1].ParameterType.GetElementType()!);
                ctx.Patch(wrapper, handler);
            }
        }
        Bind(ctx.Require("UnityEngine.CoreModule", "UnityEngine.SystemInfo", "IsGyroAvailable"), "UnityEngine.SystemInfo::IsGyroAvailable()", new IcallBoolean(() => 1), nameof(Available));
        Bind(ctx.Require(input, gyro, "getEnabled_Internal", "System.Int32"), "UnityEngine.Gyroscope::getEnabled_Internal(System.Int32)", new IcallIndexedBoolean(_ => 1), nameof(Available));
        Bind(ctx.Require(input, gyro, "setEnabled_Internal", "System.Int32", "System.Boolean"), "UnityEngine.Gyroscope::setEnabled_Internal(System.Int32,System.Boolean)", new IcallSetBoolean((_, _) => { }), nameof(Skip));
        Bind(ctx.Require(input, gyro, "attitude_Internal_Injected", "System.Int32", "UnityEngine.Quaternion&"), "UnityEngine.Gyroscope::attitude_Internal_Injected(System.Int32,UnityEngine.Quaternion&)", new IcallWriteValue((_, ptr) => WriteAttitude(ptr)), nameof(ManagedAttitude));
        Bind(ctx.Require(input, gyro, "gravity_Internal_Injected", "System.Int32", "UnityEngine.Vector3&"), "UnityEngine.Gyroscope::gravity_Internal_Injected(System.Int32,UnityEngine.Vector3&)", new IcallWriteValue((_, ptr) => Marshal.Copy(new[] { 0f, -1f, 0f }, 0, ptr, 3)), nameof(ManagedGravity));
        ctx.Log("已接入原版 5 个陀螺仪输入入口；保留 VLGyro 与 CampusGyroController 的旋转更新。");
    }
    public static bool Available(ref bool __result) { __result = true; return false; }
    public static bool Skip() => false;
    private static float[] SensorAttitude()
    {
        Sample(); var q = CurrentRotation();
        return new[] { -Convert.ToSingle(Interop.Get(q, "x")), -Convert.ToSingle(Interop.Get(q, "y")), Convert.ToSingle(Interop.Get(q, "z")), Convert.ToSingle(Interop.Get(q, "w")) };
    }
    private static void WriteAttitude(IntPtr ptr)
    {
        try { Marshal.Copy(SensorAttitude(), 0, ptr, 4); }
        catch (Exception error)
        {
            Marshal.Copy(new[] { 0f, 0f, 0f, 1f }, 0, ptr, 4);
            if (!reported) { reported = true; session?.Log("陀螺仪采样失败: " + error.GetBaseException().Message); }
        }
    }
    public static bool ManagedAttitude<T>(ref T __1) { __1 = (T)Interop.New(typeof(T), SensorAttitude().Cast<object>().ToArray())!; return false; }
    public static bool ManagedGravity<T>(ref T __1) { __1 = (T)Interop.New(typeof(T), 0f, -1f, 0f)!; return false; }
    private static PropertyInfo? slot;
    private static object CreateController(Type type)
    {
        var instance = Interop.New(type)!;
        InitializeController(instance);
        return instance;
    }
    public static void InitializeController(object __instance)
    {
        var instance = __instance;
        if (Interop.Get(instance, "_initialized") is true) return;
        var identity = euler.Invoke(null, new object[] { 0f, 0f, 0f })!;
        var gyro = Interop.Get(instance, "_gyro");
        if (gyro == null) { gyro = Interop.New(gyroType)!; Set(instance, "_gyro", gyro); }
        Set(gyro, "_updateType", 1); Set(gyro, "<gyroSupported>k__BackingField", true); Set(gyro, "_defaultGyroEnable", true);
        foreach (var field in new[] { "_rotation", "_gestureRotation", "_startRotation", "_hybridInvRotation" }) Set(gyro, field, identity);
        Set(gyro, "_initialized", true); Set(gyro, "_firstUpdate", true);
        Interop.Call(gyro, "ResetRotation");
        Set(instance, "_baseRotation", inverse.Invoke(null, new[] { Interop.Get(gyro, "gyroRotationRaw") }));
        Set(instance, "_lastAccessFrame", int.MinValue); Set(instance, "_rotation", identity); Set(instance, "_initialized", true);
        session?.Log("陀螺仪控制器已按原版初始化 VLGyro。");
    }
    private static void Set(object instance, string field, object? value)
    {
        if (!restored.TryGetValue(instance, out var state)) restored.Add(instance, state = new());
        state.TryAdd(field, Interop.Get(instance, field));
        Interop.Set(instance, field, value);
    }
    public static void EnsureController(object __instance)
    {
        if (slot == null) return;
        var controller = slot.GetValue(__instance);
        if (controller == null)
        {
            controller = CreateController(slot.PropertyType); slot.SetValue(__instance, controller); injected[__instance] = controller;
        }
        else InitializeController(controller);
    }
    public static void ProvideController<T>(ref T __1) where T : class
    { __1 ??= (T)CreateController(controllerType); InitializeController(__1); }
    private static object CurrentRotation() => euler.Invoke(null, new object[] { pitch, yaw, roll })!;
    public static void Supported(ref bool __result) => __result = true;
    public static void Panorama(ref bool __1) => __1 = true;
    private static int Held(string name) => win32
        ? (GetAsyncKeyState(name switch { "mouse 1" => 2, "[7]" => 0x67, "[9]" => 0x69, "[5]" => 0x65, _ => 0 }) & 0x8000) != 0 ? 1 : 0
        : (bool)key.Invoke(null, new object[] { name })! ? 1 : 0;
    private static void Pointer()
    {
        if (Held("mouse 1") == 0) { dragging = false; return; }
        Point point;
        if (win32) { if (!GetCursorPos(out point)) { dragging = false; return; } }
        else
        {
            var position = cursor?.GetValue(null);
            if (position == null) return;
            point = new() { X = Convert.ToInt32(Interop.Get(position, "x")), Y = -Convert.ToInt32(Interop.Get(position, "y")) };
        }
        if (dragging)
        {
            yaw += (point.X - previous.X) * .1f * sensitivity * signX;
            pitch += (point.Y - previous.Y) * .1f * sensitivity * signY;
        }
        previous = point; dragging = true;
    }
    private static void Sample()
    {
        var now = (int)frame.GetValue(null)!;
        if (now != last)
        {
            last = now;
            if (LiveGameState.InputCapture?.Invoke() == true || win32 && !Foreground()) { dragging = false; return; }
            var step = Math.Clamp((float)dt.GetValue(null)!, 0, .1f) * 60 * sensitivity;
            roll += (Held("[7]") - Held("[9]")) * step;
            Pointer();
            if (Held("[5]") != 0) pitch = yaw = roll = 0;
            pitch = Math.Clamp(pitch, -89f, 89f);
        }
    }
    private static bool Foreground()
    { GetWindowThreadProcessId(GetForegroundWindow(), out var process); return process == Environment.ProcessId; }
    public void Dispose()
    {
        foreach (var (manager, controller) in injected)
            if (Equals(slot?.GetValue(manager), controller)) slot?.SetValue(manager, null);
        injected.Clear();
        foreach (var (target, original) in restored.Reverse())
            foreach (var (field, value) in original.Reverse()) Interop.Set(target, field, value);
        restored.Clear(); pitch = yaw = roll = 0; last = -1; slot = null; dragging = false; session = null;
    }
}
