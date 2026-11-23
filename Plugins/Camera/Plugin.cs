using System.Numerics;
using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.Camera;

public sealed class Plugin : ILocalifyPlugin
{
    private static IPluginContext context = null!;
    private static MethodInfo held = null!, pressed = null!, alive = null!, bone = null!;
    private static Type vector = null!, quaternion = null!, cameraType = null!;
    private static PropertyInfo frame = null!, delta = null!;
    private static object? camera, cameraTransform, targetBone;
    private static Vector3 targetPosition, targetForward, targetLook, freePosition;
    private static Quaternion targetRotation;
    private static Vector3 follow, first;
    private static Vector2 look;
    private static float pitch, yaw, fov, speed, sensitivity, remainder;
    private static int lastFrame, actorIndex, boneIndex, targetFrame;
    private static object[] bones = null!;
    private static string mode = "free";
    private static bool active, restoring, lockRoll, smooth;
    private static HeadVisibility? head;
    private static readonly CameraMath.HeightState heightState = new();
    private static readonly CameraMath.Height followHeight = new(heightState), firstHeight = new(heightState);
    private static readonly Dictionary<object, (float Fov, float Near)> cameraOriginals = new();
    private static readonly Dictionary<object, (object Position, object Rotation)> poses = new();
    public void Start(IPluginContext ctx)
    {
        context = ctx; Reset(); lastFrame = -1;
        speed = ctx.Option("speed", 1f); sensitivity = ctx.Option("sensitivity", 1f);
        if (!float.IsFinite(speed) || !float.IsFinite(sensitivity) || speed <= 0 || sensitivity <= 0) throw new InvalidDataException("移动速度和灵敏度须为正数。");
        mode = ctx.Option("mode", "free");
        if (mode is not ("free" or "follow" or "first-person")) throw new InvalidDataException("镜头模式无效。");
        actorIndex = ctx.Option("actorIndex", 0);
        lockRoll = ctx.Option("lockRoll", false); smooth = ctx.Option("smoothFollow", true);
        follow = Offset("followOffset", new[] { 0f, 0f, 1.5f }); first = Offset("firstPersonOffset", new[] { 0f, .064f, 0f });
        var lookValues = ctx.Option("lookOffset", new[] { 0f, 0f });
        if (lookValues.Length != 2 || lookValues.Any(v => !float.IsFinite(v))) throw new InvalidDataException("注视偏移需要两个有限数值。");
        look = new(lookValues[0], lookValues[1]);
        if (ctx.Settings.ValueKind == System.Text.Json.JsonValueKind.Object && ctx.Settings.TryGetProperty("offset", out _))
        { if (mode == "follow") follow = Offset("offset", new[] { 0f, 0f, 1.5f }); else if (mode == "first-person") first = Offset("offset", new[] { 0f, .064f, 0f }); }
        held = ctx.Require("UnityEngine.InputLegacyModule", "UnityEngine.Input", "GetKey", "System.String");
        pressed = ctx.Require("UnityEngine.InputLegacyModule", "UnityEngine.Input", "GetKeyDown", "System.String");
        var time = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Time");
        frame = time.GetProperty("frameCount")!; delta = time.GetProperty("unscaledDeltaTime")!;
        vector = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Vector3");
        quaternion = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Quaternion");
        cameraType = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Camera");
        alive = ctx.Require("UnityEngine.CoreModule", "UnityEngine.Object", "op_Implicit", "UnityEngine.Object");
        var actor = ctx.RequireType("campus-submodule.Runtime", "Campus.Common.CampusActorController");
        bone = actor.GetMethods(Interop.Members).First(m => m.Name == "GetHumanBodyBoneTransform" && m.GetParameters().Length == 1);
        bones = Enum.GetValues(bone.GetParameters()[0].ParameterType).Cast<object>().ToArray();
        boneIndex = Math.Max(0, Array.FindIndex(bones, v => v.ToString() == "Head"));
        if (ctx.Option("hideHead", true)) head = new HeadVisibility(alive);
        ctx.Patch(ctx.Require("campus-submodule.Runtime", actor.FullName!, "LateUpdate"), GameMethods.Handler<Plugin>(nameof(Track)));
        var brain = ctx.RequireType("Cinemachine", "Cinemachine.CinemachineBrain").GetMethods(GameMethods.Declared).Single(m => m.Name == "PushStateToUnityCamera");
        ctx.Patch(brain, GameMethods.Handler<Plugin>(nameof(Push)).MakeGenericMethod(Interop.CallbackType(brain.GetParameters()[0].ParameterType)));
        ctx.Patch(ctx.Require("Unity.RenderPipelines.Universal.Runtime", "VL.Rendering.VLDOF", "IsActive"), GameMethods.Handler<Plugin>(nameof(DepthOfField)));
        ctx.Patch(cameraType.GetProperty("fieldOfView")!.SetMethod!, GameMethods.Handler<Plugin>(nameof(SetFov)));
        ctx.Patch(cameraType.GetProperty("fieldOfView")!.GetMethod!, postfix: GameMethods.Handler<Plugin>(nameof(GetFov)));
        ctx.Patch(ctx.Require("UnityEngine.CoreModule", "UnityEngine.Camera", "get_main"), postfix: GameMethods.Handler<Plugin>(nameof(Main)));
        var getTransform = cameraType.GetProperty("transform")!.GetMethod!;
        ctx.Patch(getTransform, postfix: GameMethods.Handler<Plugin>(nameof(Transform)));
        ctx.Patch(ctx.Require("UnityEngine.CoreModule", "UnityEngine.Rendering.RenderPipelineManager", "EndCameraRendering", "UnityEngine.Rendering.ScriptableRenderContext", "UnityEngine.Camera"),
            postfix: GameMethods.Handler<Plugin>(nameof(Rendered)));
        var transform = ctx.RequireType("UnityEngine.CoreModule", "UnityEngine.Transform");
        BindInjected("set_position_Injected", nameof(Position), vector);
        BindInjected("set_localPosition_Injected", nameof(Position), vector);
        BindInjected("set_rotation_Injected", nameof(Rotation), quaternion);
        BindInjected("SetPositionAndRotation_Injected", nameof(Combined), vector, quaternion);
        active = true;
        ctx.Log("镜头已启用：原版默认位置及按键；Cinemachine 状态前置修改，渲染回调仅调整 FOV/近裁剪。");
        void BindInjected(string name, string callback, params Type[] args)
        {
            var method = transform.GetMethods(Interop.Members).Single(m => m.Name == name);
            // Unity 6 exports static bindings with a native Unity object pointer first.
            if (!method.IsStatic || method.GetParameters()[0].ParameterType != typeof(IntPtr))
                throw new NotSupportedException(name + " 的 Unity 注入签名与当前适配不符。");
            ctx.Patch(method, GameMethods.Handler<Plugin>(callback).MakeGenericMethod(args.Select(Interop.CallbackType).ToArray()));
        }
    }
    private static Vector3 Offset(string name, float[] fallback)
    {
        var v = context.Option(name, fallback);
        if (v.Length != 3 || v.Any(x => !float.IsFinite(x))) throw new InvalidDataException(name + " 需要三个有限数值。");
        return new(v[0], v[1], v[2]);
    }
    private static bool Alive(object? value) => value != null && alive.Invoke(null, new[] { value }) is true;
    private static Vector3 V(object value) => new(Convert.ToSingle(Interop.Get(value, "x")), Convert.ToSingle(Interop.Get(value, "y")), Convert.ToSingle(Interop.Get(value, "z")));
    private static Quaternion Q(object value) => new(Convert.ToSingle(Interop.Get(value, "x")), Convert.ToSingle(Interop.Get(value, "y")), Convert.ToSingle(Interop.Get(value, "z")), Convert.ToSingle(Interop.Get(value, "w")));
    private static object Box(Vector3 value) => Interop.New(vector, value.X, value.Y, value.Z)!;
    private static object Box(Quaternion value) => Interop.New(quaternion, value.X, value.Y, value.Z, value.W)!;
    public static void Track(object __instance)
    {
        if (!active || mode == "free") { head?.Restore(); return; }
        if (Convert.ToInt32(Interop.Get(__instance, "index")) != actorIndex) return;
        var selected = mode == "first-person" ? bones.First(v => v.ToString() == "Head") : bones[boneIndex];
        targetBone = bone.Invoke(__instance, new[] { selected });
        if (!Alive(targetBone)) { targetBone = null; head?.Restore(); return; }
        targetPosition = V(Interop.Get(targetBone!, "position")!);
        targetFrame = (int)frame.GetValue(null)!;
        targetRotation = Q(Interop.Get(targetBone!, "rotation")!);
        targetForward = V(Interop.Get(targetBone!, "forward")!); targetLook = targetPosition + targetForward * 3;
        // Original hides both head objects in either tracking mode.
        if (head != null) head.Track(Interop.Get(Interop.Get(__instance, "_rootBody")!, "parent"));
    }
    private static void Capture(object? next, bool replace = true)
    {
        if (!active || restoring || !Alive(next) || !replace && Alive(camera)) return;
        if (!Equals(camera, next)) { camera = next; cameraTransform = null; }
    }
    public static void Main(object? __result) => Capture(__result);
    public static void Transform(object __instance, object? __result)
    { if (active && Equals(camera, __instance) && Alive(__result)) cameraTransform = __result; }
    private static void RememberCamera(object value)
    {
        if (cameraOriginals.ContainsKey(value)) return;
        restoring = true;
        try { cameraOriginals[value] = (Convert.ToSingle(Interop.Get(value, "fieldOfView")), Convert.ToSingle(Interop.Get(value, "nearClipPlane"))); }
        finally { restoring = false; }
    }
    public static void SetFov(object __instance, ref float __0)
    {
        if (!active || restoring) return;
        Capture(__instance); RememberCamera(__instance); __0 = fov;
    }
    public static void GetFov(object __instance, ref float __result)
    {
        if (!active || restoring) return;
        Capture(__instance); RememberCamera(__instance);
        restoring = true;
        try
        {
            foreach (var item in Interop.Items(cameraType.GetProperty("allCameras")!.GetValue(null)))
            {
                if (!cameraOriginals.ContainsKey(item)) cameraOriginals[item] = (Convert.ToSingle(Interop.Get(item, "fieldOfView")), Convert.ToSingle(Interop.Get(item, "nearClipPlane")));
                Interop.Set(item, "fieldOfView", fov);
            }
        }
        finally { restoring = false; }
        __result = fov;
    }
    public static bool DepthOfField(ref bool __result)
    { if (!active) return true; __result = false; return false; }
    public static void Rendered(object __1)
    {
        if (!active) return;
        Capture(__1, false);
        if (!Alive(camera)) return;
        RememberCamera(camera!);
        Interop.Set(camera!, "fieldOfView", fov);
        if (mode == "first-person") Interop.Set(camera!, "nearClipPlane", .001f);
        else if (cameraOriginals.TryGetValue(camera!, out var original)) Interop.Set(camera!, "nearClipPlane", original.Near);
    }
    private static bool ControlsTransform(IntPtr pointer)
    {
        if (!active || restoring || !Alive(cameraTransform)) return false;
        if (Interop.Get(cameraTransform!, "m_CachedPtr") is not IntPtr native || native != pointer) return false;
        if (!poses.ContainsKey(cameraTransform!))
            poses[cameraTransform!] = (Interop.Get(cameraTransform!, "position")!, Interop.Get(cameraTransform!, "rotation")!);
        return true;
    }
    private static bool Pose(out Vector3 position, out Quaternion rotation, bool record = false, bool separateRotation = false, bool positionOnly = false)
    {
        rotation = Quaternion.Identity; position = freePosition;
        Vector3 target;
        if (mode == "free")
        {
            var p = pitch * MathF.PI / 180; var y = yaw * MathF.PI / 180;
            target = freePosition + new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p)) * 5;
        }
        else if (mode == "follow")
        {
            if (!Alive(targetBone) || (int)frame.GetValue(null)! - targetFrame > 1) return false;
            target = CameraMath.FollowLook(targetPosition, follow, look, followHeight.Apply(targetPosition.Y, record, smooth));
            position = CameraMath.Orbit(target, follow);
        }
        else
        {
            if (!Alive(targetBone) || (int)frame.GetValue(null)! - targetFrame > 1) return false;
            position = CameraMath.FirstPerson(targetPosition, targetForward, first);
            if (separateRotation && !lockRoll) { rotation = targetRotation; return CameraMath.Finite(position); }
            target = targetLook;
            if (separateRotation) target.Y = firstHeight.Apply(target.Y, true, smooth);
        }
        return CameraMath.Finite(position) && (positionOnly || CameraMath.Look(target - position, out rotation));
    }
    public static void Push<T>(ref T __0)
    {
        if (!active || __0 == null) return;
        Tick();
        if (!Pose(out var pos, out var rot)) return;
        object state = __0;
        Interop.Set(state, "RawPosition", Box(pos)); Interop.Set(state, "RawOrientation", Box(rot));
        Interop.Set(state, "PositionDampingBypass", Box(Vector3.Zero)); Interop.Set(state, "PositionCorrection", Box(Vector3.Zero));
        Interop.Set(state, "OrientationCorrection", Box(Quaternion.Identity));
        var lens = Interop.Get(state, "Lens")!; Interop.Set(lens, "FieldOfView", fov); Interop.Set(state, "Lens", lens);
        __0 = (T)state;
    }
    public static void Position<T>(IntPtr __0, ref T __1)
    { if (ControlsTransform(__0)) { Tick(); if (Pose(out var p, out _, positionOnly: true)) __1 = (T)Box(p); } }
    public static void Rotation<T>(IntPtr __0, ref T __1)
    { if (ControlsTransform(__0)) { Tick(); if (Pose(out _, out var r, true, true)) __1 = (T)Box(r); } }
    public static void Combined<T, U>(IntPtr __0, ref T __1, ref U __2)
    { if (ControlsTransform(__0)) { Tick(); if (Pose(out var p, out var r)) { __1 = (T)Box(p); __2 = (U)Box(r); } } }
    private static bool Press(string key) => pressed.Invoke(null, new object[] { key }) is true;
    private static float Key(string key) => held.Invoke(null, new object[] { key }) is true ? 1 : 0;
    private static void Reset()
    {
        freePosition = new(.5f, 1.1f, 1.3f); pitch = yaw = 0; fov = 60;
        follow = new(0, 0, 1.5f); first = new(0, .064f, 0); look = default; actorIndex = 0;
        targetBone = null; targetPosition = targetForward = targetLook = default; targetRotation = Quaternion.Identity;
    }
    private static void Tick()
    {
        var now = (int)frame.GetValue(null)!;
        if (lastFrame == now) return; lastFrame = now;
        if (LiveGameState.InputCapture?.Invoke() == true) { remainder = 0; return; }
        if (Press("f")) { mode = mode == "free" ? "follow" : mode == "follow" ? "first-person" : "free"; if (mode == "free") head?.Restore(); context.Log("镜头模式: " + mode); }
        if (Press("v")) { if (mode == "follow") smooth = !smooth; else if (mode == "first-person") lockRoll = !lockRoll; }
        if (Press("r")) Reset();
        if (mode != "free")
        {
            if (Press("left")) { actorIndex = Math.Max(0, actorIndex - 1); targetBone = null; head?.Restore(); }
            if (Press("right")) { actorIndex++; targetBone = null; head?.Restore(); }
            if (mode == "follow")
            {
                if (Press("up")) boneIndex = (boneIndex + bones.Length - 1) % bones.Length;
                if (Press("down")) boneIndex = (boneIndex + 1) % bones.Length;
            }
        }
        // Run the original 10 ms movement steps on the Unity thread, so unloading
        // does not leave an immortal worker and Unity APIs never run on that worker.
        remainder += Math.Clamp(Convert.ToSingle(delta.GetValue(null)), 0, .1f);
        while (remainder >= .01f) { remainder -= .01f; Step(); }
    }
    private static void Step()
    {
        var fb = Key("w") - Key("s"); var lr = Key("a") - Key("d");
        var vertical = Key("space") - Math.Max(Key("left ctrl"), Key("right ctrl"));
        if (mode == "free")
        {
            var y = yaw * MathF.PI / 180; var p = pitch * MathF.PI / 180;
            freePosition += .05f * speed * new Vector3((fb * MathF.Sin(y) + lr * MathF.Cos(y)) * MathF.Cos(p), fb * MathF.Sin(p) + vertical, (-fb * MathF.Cos(y) + lr * MathF.Sin(y)) * MathF.Cos(p));
        }
        else if (mode == "follow") { follow.Z -= fb * .008f * speed; look.X += lr * .008f * speed; look.Y += vertical * .008f * speed; }
        else { first.Z += fb * .008f * speed; first.Y += vertical * .008f * speed; }
        // DMM still updates the free-camera angle while a tracking mode is selected.
        pitch = Math.Clamp(pitch + (Key("up") - Key("down")) * sensitivity, -89.99f, 89.99f);
        yaw += (Key("left") - Key("right")) * sensitivity;
        if (yaw >= 360) yaw = -360; else if (yaw <= -360) yaw = 360;
        fov = Math.Clamp(fov + (Key("q") - Key("e")) * .5f, 1, 179);
        if (mode == "follow") { follow.Y += (Key("i") - Key("k")) * .008f; follow.X += (Key("j") - Key("l")) * .8f; }
    }
    public void Dispose()
    {
        active = false; restoring = true;
        try
        {
            head?.Restore();
            foreach (var (item, state) in poses) if (Alive(item)) { Interop.Set(item, "position", state.Position); Interop.Set(item, "rotation", state.Rotation); }
            foreach (var (item, state) in cameraOriginals) if (Alive(item)) { Interop.Set(item, "fieldOfView", state.Fov); Interop.Set(item, "nearClipPlane", state.Near); }
        }
        finally
        {
            poses.Clear(); cameraOriginals.Clear(); camera = cameraTransform = targetBone = null; head = null;
            followHeight.Clear(); firstHeight.Clear(); restoring = false; remainder = 0;
        }
    }
}
