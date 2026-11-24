using System.Reflection;
using Gkms.Sdk;

namespace Gkms.Plugins.PurchaseGuard;

internal sealed class PlatformLogin : IDisposable
{
    private static PlatformLogin? current;
    private readonly Dictionary<object, object?> originalPlatforms = new();
    private IPluginContext context = null!;
    private IPluginRuntime runtime = null!;
    private PropertyInfo apiInstance = null!, apiExists = null!, userInstance = null!, userExists = null!;
    private MethodInfo balanceQuery = null!, updateBalance = null!, forget = null!;
    private MethodInfo apiSuccess = null!;
    private ConstructorInfo authConstructor = null!;
    private Type requestResultType = null!, balanceResponseType = null!;
    private Type cancellationType = null!;
    private object backgroundCallOptions = null!;
    private object? cancellation;
    private object? pendingTask, pendingAwaiter;
    private readonly Dictionary<object, bool> responsePlatforms = new();
    private bool authenticated, refreshWanted, inFlight, loggedPlatform;
    private bool waitingForHomeLogin;
    private bool started, cleanupWanted, cleanupMode;
    private string targetPlatform = "iOS", cleanupPlatform = "", requestToken = "", cleanupToken = "";
    private object? requestApi, requestUser;
    private object? requestBalance;
    private int requestFree, requestPaid, balanceRetries;
    private DateTime deadline;
    private string? warning;
    public IReadOnlyList<string> Warnings => warning == null ? Array.Empty<string>() : new[] { warning };

    public void Start(IPluginContext ctx)
    {
        context = ctx; runtime = (IPluginRuntime)ctx; current = this;
        const string game = "Assembly-CSharp", apiNamespace = "Campus.Common.Proto.Client.Api.";
        var request = ctx.RequireType(game, apiNamespace + "AuthLoginRequest");
        authConstructor = request.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(request.FullName, ".ctor()");
        ctx.PatchAdvanced(authConstructor,
            prefix: GameMethods.Handler<PlatformLogin>(nameof(BeginLogin)));
        ctx.Patch(ctx.RequireMessageReader(), postfix: GameMethods.Handler<PlatformLogin>(nameof(MessageParsed)));
        ctx.Patch(ctx.Require("Firebase.Platform", "Firebase.Platform.PlatformInformation", "get_IsIOS"), postfix: GameMethods.Handler<PlatformLogin>(nameof(IsIos)));
        ctx.Patch(ctx.Require("Firebase.Platform", "Firebase.Platform.PlatformInformation", "get_IsAndroid"), postfix: GameMethods.Handler<PlatformLogin>(nameof(IsAndroid)));
        var api = ctx.RequireType(game, "Campus.Common.Network.Api");
        var apiBase = api.BaseType ?? throw new TypeLoadException("ApiBase");
        MethodInfo Method(string name, int count) => apiBase.GetMethods(Interop.Members).Single(m => m.Name == name && m.GetParameters().Length == count);
        ctx.Patch(Method("GetPlatformString", 0), postfix: GameMethods.Handler<PlatformLogin>(nameof(Platform)));
        ctx.Patch(Method("SetAuthToken", 1), postfix: GameMethods.Handler<PlatformLogin>(nameof(TokenSet)));
        // The original DMM plugin hooks the API constructor, not Initialize.
        // The observed Initialize detour received unrelated calls, including a non-object this.
        ctx.PatchAdvanced(apiBase.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(apiBase.FullName, ".ctor()"),
            postfix: GameMethods.Handler<PlatformLogin>(nameof(ApiInitialized)));
        apiInstance = apiBase.GetProperty("Instance", Interop.Members)!;
        apiExists = apiBase.GetProperty("HasInstance", Interop.Members)!;
        if (apiInstance?.GetMethod == null || apiExists == null) throw new MissingMemberException("ApiBase.Instance/HasInstance");
        ctx.Patch(apiInstance.GetMethod, postfix: GameMethods.Handler<PlatformLogin>(nameof(Instance)));
        // Observe the final per-request headers, not just the cached _platform field.
        apiSuccess = Method("ApiSuccessLog", 6);
        ctx.Patch(apiSuccess, postfix: GameMethods.Handler<PlatformLogin>(nameof(ApiSucceeded)));
        var userApi = ctx.RequireType(game, "Campus.Common.Network.Api+User");
        balanceQuery = userApi.GetMethods(GameMethods.Declared).Single(m => m.Name == "GetBalanceAsync" && m.IsStatic && m.GetParameters().Length == 4);
        // This is an optional background refresh, not a game navigation request.
        // In particular, a DateChanged/title response must not abort the game's login.
        backgroundCallOptions = Interop.New(balanceQuery.GetParameters()[1].ParameterType, true, true, true, true, true)!;
        var resultType = balanceQuery.ReturnType.GetMethod("GetAwaiter", Type.EmptyTypes)?.ReturnType.GetMethod("GetResult", Type.EmptyTypes)?.ReturnType;
        context.Log($"余额接口类型（只读诊断）: task={DescribeType(balanceQuery.ReturnType)}; result={DescribeType(resultType)}");
        requestResultType = resultType ?? throw new MissingMethodException("余额任务缺少 GetAwaiter/GetResult。");
        balanceResponseType = ctx.RequireType(game, apiNamespace + "UserGetBalanceResponse");
        if (!requestResultType.IsGenericType || requestResultType.GetGenericTypeDefinition().FullName != "Qua.Network.IApiRequest`2" ||
            requestResultType.GetGenericArguments().Last() != balanceResponseType)
            throw new NotSupportedException("余额接口返回结构已改变: " + DescribeType(requestResultType));
        var user = ctx.RequireType(game, "Campus.Common.User.UserDataManager");
        userInstance = user.GetProperty("Instance", Interop.Members | BindingFlags.FlattenHierarchy)!;
        userExists = user.GetProperty("HasInstance", Interop.Members | BindingFlags.FlattenHierarchy)!;
        updateBalance = user.GetMethods(Interop.Members).Single(m => m.Name == "UpdateUserBalance" && m.GetParameters().Length == 1);
        if (userInstance == null || userExists == null) throw new MissingMemberException("UserDataManager.Instance/HasInstance");
        cancellationType = ctx.RequireType("mscorlib", "System.Threading.CancellationTokenSource");
        forget = ctx.RequireType("UniTask", "Cysharp.Threading.Tasks.UniTaskExtensions").GetMethods(GameMethods.Declared)
            .Single(m => m.Name == "Forget" && !m.ContainsGenericParameters && m.GetParameters() is [{ ParameterType.FullName: "Cysharp.Threading.Tasks.UniTask" }]);
        ctx.Patch(ctx.Require("campus-submodule.Runtime", "Campus.Common.CampusQualityManager", "Update"),
            postfix: GameMethods.Handler<PlatformLogin>(nameof(Tick)));
        if (ExistingApi() is { } existing)
        {
            authenticated = !string.IsNullOrEmpty(Interop.Get(existing, "_authToken")?.ToString());
            Apply(existing);
        }
        if (authenticated) Schedule("热启用：检测到已有认证，正在请求 iOS 宝石余额。");
        else context.Log("充值拦截已启用；等待 DMM 认证完成，再切换 iOS 请求平台。");
        started = true;
    }

    private object? ExistingApi() => apiExists.GetValue(null) is true ? apiInstance.GetValue(null) : null;
    private static string DescribeType(Type? type, int depth = 0) => type == null ? "null" :
        depth < 4 && type.IsGenericType ? (type.FullName ?? type.Name) + "<" + string.Join(",", type.GetGenericArguments().Select(t => DescribeType(t, depth + 1))) + ">" : type.FullName ?? type.Name;
    private void Schedule(string reason)
    {
        refreshWanted = true; warning = null;
        context.Log(reason);
    }
    public static void BeginLogin()
    {
        if (current is not { } self) return;
        self.authenticated = false; self.refreshWanted = false; self.CancelRefresh(); self.Restore(); self.loggedPlatform = false;
        self.waitingForHomeLogin = false;
        self.balanceRetries = 0;
        self.warning = null;
        self.context.Log("开始 DMM 认证，已恢复原请求平台；充值拦截保持启用。");
    }
    public static void Authenticated(object __instance)
    {
        if (current is not { } self) return;
        if (string.IsNullOrEmpty(Interop.Get(__instance, "GameAuthToken")?.ToString())) { BeginLogin(); return; }
        self.AcceptAuthentication("DMM 认证响应有效；按原版切换平台，等待 Home:Login 完成后刷新余额。");
    }
    public static void MessageParsed(object? __0)
    {
        if (__0 != null && Interop.TypeOf(__0).FullName == "Campus.Common.Proto.Client.Api.AuthLoginResponse") Authenticated(__0);
    }
    public static void TokenSet(string __0)
    {
        if (current is not { } self) return;
        if (string.IsNullOrEmpty(__0)) { BeginLogin(); return; }
        if (!self.authenticated) return; // A token assignment alone is not a successful DMM response.
        self.context.Log("游戏已写入认证令牌，后续请求使用 iOS。");
        if (self.ExistingApi() is { } api) self.Apply(api);
    }
    private void AcceptAuthentication(string reason)
    {
        if (!authenticated) { authenticated = true; waitingForHomeLogin = true; context.Log(reason); }
        foreach (var api in originalPlatforms.Keys.ToArray()) Apply(api);
    }
    public static void IsIos(ref bool __result) { if (current?.authenticated == true) __result = true; }
    public static void IsAndroid(ref bool __result) { if (current?.authenticated == true) __result = false; }
    public static void Instance(object? __result) { if (__result != null) current?.Apply(__result); }
    public static void ApiInitialized(object __instance) => current?.Apply(__instance);
    public static void Platform(object __instance, ref string __result)
    { current?.Apply(__instance); if (current?.authenticated == true) __result = "iOS"; }
    private void Apply(object api)
    {
        var before = Interop.Get(api, "_platform");
        if (!originalPlatforms.ContainsKey(api)) originalPlatforms.Add(api, before);
        else if (string.IsNullOrEmpty(originalPlatforms[api]?.ToString()) && !string.IsNullOrEmpty(before?.ToString()) && before?.ToString() != "iOS")
            originalPlatforms[api] = before;
        if (!authenticated) return;
        Interop.Set(api, "_platform", targetPlatform);
        if (Interop.Get(api, "_platform")?.ToString() != targetPlatform) throw new InvalidOperationException("API 平台字段写入校验失败。");
        if (!loggedPlatform) { loggedPlatform = true; context.Log($"本地 API 平台字段已读回确认: {targetPlatform}；这还不是服务端余额确认。"); }
    }
    public static void Tick()
    {
        if (current is not { } self) return;
        try { self.RefreshIfReady(); }
        catch (Exception error) { self.Fail("余额刷新失败: " + error.GetBaseException().Message); }
    }
    private void RefreshIfReady()
    {
        if (inFlight)
        {
            if (apiExists.GetValue(null) is not true || userExists.GetValue(null) is not true ||
                !Equals(ExistingApi(), requestApi) || !Equals(userInstance.GetValue(null), requestUser) ||
                Interop.Get(requestApi!, "_authToken")?.ToString() != requestToken ||
                Interop.Get(requestApi!, "_platform")?.ToString() != targetPlatform)
            { Fail("登录会话、用户对象或请求平台已改变，取消旧余额刷新。"); return; }
            if (pendingAwaiter != null && Interop.Get(pendingAwaiter, "IsCompleted") is true)
            {
                // Consume only this refresh's task. Responses from an earlier login or another
                // request must never update the balance of the current session.
                var awaiter = pendingAwaiter; pendingAwaiter = null; pendingTask = null;
                var request = Interop.Call(awaiter, "GetResult");
                if (request == null || !requestResultType.IsInstanceOfType(request))
                    throw new InvalidDataException("余额请求对象类型不匹配: " + DescribeType(request == null ? null : Interop.TypeOf(request)));
                if (Interop.Get(request, "Error") is { } apiError)
                {
                    var code = Interop.Get(apiError, "Code")?.ToString();
                    var diagnostic = int.TryParse(code, out var number) ? $"（错误码 {number}）" : "";
                    throw new InvalidDataException($"后台余额查询失败{diagnostic}，保留当前显示，不触发游戏返回标题。");
                }
                if (Interop.Get(request, "IsDone") is not true)
                    throw new InvalidDataException("余额请求未成功完成，保留当前显示。");
                var response = Interop.Get(request, "Response");
                if (response == null || !balanceResponseType.IsInstanceOfType(response))
                    throw new InvalidDataException("余额请求缺少有效 Response，未应用。");
                bool confirmed;
                lock (responsePlatforms) confirmed = responsePlatforms.GetValueOrDefault(response);
                if (!confirmed)
                { Fail($"本次 User/GetBalance 的实际请求平台头未确认为 {targetPlatform}，保留当前余额。"); return; }
                var latest = Interop.Get(requestUser!, "UserBalance");
                if (latest == null ||
                    Convert.ToInt32(Interop.Get(latest, "FreeBalance")) != requestFree || Convert.ToInt32(Interop.Get(latest, "PaidBalance")) != requestPaid)
                {
                    ReleaseCancellation(); inFlight = false;
                    lock (responsePlatforms) responsePlatforms.Clear();
                    if (balanceRetries++ == 0) Schedule("刷新期间游戏已更新余额，丢弃旧结果并重新读取一次。");
                    else Fail("游戏余额持续变化，已停止主动刷新，保留游戏当前数据。");
                    return;
                }
                ApplyBalance(response);
            }
            else if (DateTime.UtcNow >= deadline) Fail($"{targetPlatform} 余额刷新失败：请求超时，保留当前显示。");
            return;
        }
        if (!refreshWanted || !authenticated || runtime.Stopping.IsCancellationRequested) return;
        var api = ExistingApi();
        if (api == null || string.IsNullOrEmpty(Interop.Get(api, "_authToken")?.ToString()) || userExists.GetValue(null) is not true) return;
        var user = userInstance.GetValue(null);
        if (user == null || Interop.Get(user, "UserBalance") == null) return;
        if (cleanupMode && (Interop.Get(api, "_authToken")?.ToString() != cleanupToken || Interop.Get(api, "_platform")?.ToString() != targetPlatform))
        { Fail("停用后登录会话或平台已改变，取消旧余额刷新。"); return; }
        Apply(api);
        requestApi = api; requestUser = user; requestToken = Interop.Get(api, "_authToken")?.ToString() ?? "";
        requestBalance = Interop.Get(user, "UserBalance");
        requestFree = Convert.ToInt32(Interop.Get(requestBalance!, "FreeBalance")); requestPaid = Convert.ToInt32(Interop.Get(requestBalance!, "PaidBalance"));
        cancellation = Interop.New(cancellationType)!;
        var token = Interop.Get(cancellation, "Token")!;
        var parameter = balanceQuery.GetParameters()[0].ParameterType;
        var argument = parameter.IsInstanceOfType(token) ? token : Interop.New(parameter, token);
        refreshWanted = false; inFlight = true; deadline = DateTime.UtcNow.AddSeconds(30);
        context.Log($"发送 User/GetBalance，目标平台 {targetPlatform}，重新读取服务器余额（不复用旧响应缓存）。");
        pendingTask = balanceQuery.Invoke(null, new object?[] { argument, backgroundCallOptions, null, null }) ?? throw new InvalidOperationException("余额查询未返回任务。");
        pendingAwaiter = Interop.Call(pendingTask, "GetAwaiter");
    }
    public static void ApiSucceeded(object __instance, object[] __args)
    {
        if (current is not { authenticated: true } self || __args.Length != 6 || __args[3] == null) return;
        var responseType = Interop.TypeOf(__args[3]).FullName;
        // User:Get populates UserBalance before Home:Login performs the daily reset.
        // The original DMM plugin only changes the platform here; our extra query
        // must wait for successful Home:Login, not merely an allocated user model.
        if (!self.cleanupMode && self.waitingForHomeLogin && responseType == "Campus.Common.Proto.Client.Api.HomeLoginResponse")
        {
            self.waitingForHomeLogin = false;
            self.Schedule("Home:Login 已成功，开始后台刷新 iOS 宝石余额。");
            return;
        }
        if (!self.inFlight || responseType != "Campus.Common.Proto.Client.Api.UserGetBalanceResponse") return;
        try
        {
            var key = Interop.Get(__instance, "PlatformHeaderKey")?.ToString();
            var values = Interop.Items(__args[2]).Where(e => string.Equals(Interop.Get(e, "Key")?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                .Select(e => Interop.Get(e, "Value")?.ToString()).ToArray();
            // This log is evidence only. The task's result identifies which response is ours.
            lock (self.responsePlatforms) self.responsePlatforms[__args[3]] = values.Length == 1 && values[0] == self.targetPlatform;
        }
        catch (Exception error) { self.context.Log("检查余额请求平台头失败: " + error.GetBaseException().Message); }
    }
    private void ApplyBalance(object response)
    {
        try
        {
            if (userExists.GetValue(null) is not true) throw new InvalidOperationException("用户数据已释放，忽略旧余额响应。");
            var free = Convert.ToInt32(Interop.Get(response, "FreeBalance"));
            var paid = Convert.ToInt32(Interop.Get(response, "PaidBalance"));
            var web = Convert.ToInt32(Interop.Get(response, "WebStorePaidBalance"));
            if (free < 0 || paid < 0) throw new InvalidDataException("服务端余额为负值，未应用。");
            var model = Interop.New(updateBalance.GetParameters()[0].ParameterType)!;
            Interop.Set(model, "FreeBalance", free); Interop.Set(model, "PaidBalance", paid);
            var user = userInstance.GetValue(null)!;
            updateBalance.Invoke(user, new[] { model });
            var applied = Interop.Get(user, "UserBalance")!;
            if (Convert.ToInt32(Interop.Get(applied, "FreeBalance")) != free || Convert.ToInt32(Interop.Get(applied, "PaidBalance")) != paid)
                throw new InvalidOperationException("余额模型读回不一致。");
            ReleaseCancellation(); inFlight = false; refreshWanted = false; warning = null;
            requestApi = requestUser = requestBalance = null; requestToken = "";
            lock (responsePlatforms) responsePlatforms.Clear();
            context.Log($"{targetPlatform} 余额确认成功：实际请求平台头={targetPlatform}；服务器返回免费={free}、付费={paid}、WebStore付费={web}；已调用 UpdateUserBalance 余额变更通知入口，并读回确认。");
        }
        catch (Exception error) { Fail("余额返回但应用失败: " + error.GetBaseException().Message); }
    }
    private void Fail(string message)
    { CancelRefresh(); refreshWanted = false; warning = message; context.Log(message); }
    private void ReleaseCancellation()
    {
        if (cancellation == null) return;
        Interop.Call(cancellation, "Dispose"); cancellation = null;
    }
    private void CancelRefresh()
    {
        inFlight = false;
        var task = pendingTask; pendingTask = null; pendingAwaiter = null;
        try
        {
            // Native Forget observes cancellation/errors after the plugin hooks are removed.
            if (task != null) forget.Invoke(null, new[] { Interop.Call(task, "AsUniTask") });
        }
        catch (Exception error) { context.Log("结束旧余额请求: " + error.GetBaseException().Message); }
        finally
        {
            try { if (cancellation != null) Interop.Call(cancellation, "Cancel"); }
            finally
            {
                ReleaseCancellation(); lock (responsePlatforms) responsePlatforms.Clear();
                requestApi = requestUser = requestBalance = null; requestToken = "";
            }
        }
    }
    private void Restore()
    {
        foreach (var (api, original) in originalPlatforms)
            if (Interop.Get(api, "_platform")?.ToString() == "iOS") Interop.Set(api, "_platform", original ?? "dmm");
    }
    public void Dispose()
    {
        var existing = started ? ExistingApi() : null;
        cleanupWanted = started && authenticated && existing != null && userExists.GetValue(null) is true &&
            Interop.Get(userInstance.GetValue(null)!, "UserBalance") != null;
        cleanupToken = existing == null ? "" : Interop.Get(existing, "_authToken")?.ToString() ?? "";
        cleanupWanted &= cleanupToken.Length != 0;
        authenticated = false; refreshWanted = false; waitingForHomeLogin = false;
        CancelRefresh(); Restore(); originalPlatforms.Clear();
        cleanupPlatform = existing == null ? "" : Interop.Get(existing, "_platform")?.ToString() ?? "";
        if (current == this) current = null;
    }

    public IPluginCleanup? CreateStopCleanup()
    {
        if (!cleanupWanted || cleanupPlatform.Length == 0) return null;
        cleanupWanted = false;
        return new StopRefresh(this);
    }
    private sealed class StopRefresh : IPluginCleanup
    {
        private readonly PlatformLogin owner;
        public StopRefresh(PlatformLogin owner) => this.owner = owner;
        public void Start(IPluginContext context)
        {
            owner.context = context; owner.runtime = (IPluginRuntime)context;
            owner.cleanupMode = true; owner.targetPlatform = owner.cleanupPlatform;
            owner.balanceRetries = 0;
            owner.authenticated = true; owner.loggedPlatform = false; current = owner;
            // Only observe response headers and cancel on a new login. All iOS and
            // purchase hooks were removed before this bounded cleanup was created.
            context.Patch(owner.apiSuccess, postfix: GameMethods.Handler<PlatformLogin>(nameof(ApiSucceeded)));
            context.PatchAdvanced(owner.authConstructor, prefix: GameMethods.Handler<PlatformLogin>(nameof(BeginLogin)));
            owner.Schedule($"iOS 功能已关闭，正在重新读取 {owner.targetPlatform} 宝石余额。");
        }
        public bool Update()
        {
            owner.RefreshIfReady();
            if (owner.refreshWanted || owner.inFlight) return false;
            if (owner.warning != null) throw new InvalidOperationException(owner.warning);
            return true;
        }
        public void Dispose()
        {
            owner.authenticated = owner.refreshWanted = false;
            owner.CancelRefresh(); owner.originalPlatforms.Clear();
            owner.cleanupToken = "";
            if (current == owner) current = null;
        }
    }
}
