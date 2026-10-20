using System.Text.Json.Serialization;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 应用构建器入口：
/// <code>
/// OrielApp.CreateBuilder(args)
///     .UseEmbeddedAssets()
///     .UseJsonContext(AppJsonContext.Default)
///     .AddCommands&lt;TodoCommands&gt;()
///     .AddWindow(w =&gt; w.WithTitle("Demo").WithSize(1000, 700).Center())
///     .Run();
/// </code>
/// </summary>
public sealed class OrielAppBuilder
{
    internal bool UseAssets { get; private set; }
    internal string AssetHost { get; private set; } = "app.oriel";
    internal string? AssetResourcePrefix { get; private set; }
    internal string? UserDataFolder { get; private set; }

    /// <summary>应用启动参数（<see cref="Oriel.CreateBuilder"/> 的入参）；单实例用它给首实例传话。</summary>
    internal IReadOnlyList<string> StartupArgs { get; set; } = [];
    internal Dictionary<Type, Func<object>> TargetFactories { get; } = [];

    /// <summary>启用内嵌前端资源：<paramref name="resourcePrefix"/> 为程序集内嵌资源名前缀（默认 "程序集名.wwwroot/"，必须以 '/' 结尾），经自定义 scheme <c>oriel://&lt;host&gt;/</c> 提供（三平台一致，不写盘）。</summary>
    /// <remarks>
    /// <paramref name="host"/> 会被拼进页面来源 <c>oriel://&lt;host&gt;/</c>（以及兼容别名
    /// <c>https://&lt;host&gt;/</c>），并且在门禁里逐字节参与可信前缀判定，因此只接受
    /// host 允许的字符：字母、数字、<c>-</c>、<c>.</c>、<c>_</c>。含空格/斜杠/中文等字符时
    /// 过去的症状是**页面白屏、宿主侧无异常**（来源拼出来不是合法 URL，导航直接失败），
    /// 现在在构建期就抛出来并指出是哪个字符（评审 P3）。
    /// </remarks>
    public OrielAppBuilder UseEmbeddedAssets(string host = "app.oriel", string? resourcePrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        foreach (char c in host)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            {
                throw new ArgumentException(
                    $"UseEmbeddedAssets 的 host「{host}」含非法字符「{c}」：它要拼进 oriel://<host>/，只能用字母、数字、'-'、'.'、'_'。",
                    nameof(host));
            }
        }

        UseAssets = true;
        AssetHost = host;
        AssetResourcePrefix = resourcePrefix;
        return this;
    }

    internal JsonSerializerContext? JsonContext { get; private set; }

    /// <summary>
    /// 应用自身的元信息（名称 / 版本 / 标识符；见 <see cref="OrielAppInfo"/>）。
    /// </summary>
    /// <remarks>
    /// 默认从入口程序集推断，所以最小示例不必配。正式应用应当显式给——
    /// 尤其标识符，它将成为数据目录的组成部分。
    /// </remarks>
    internal OrielAppInfo AppInfo { get; private set; } = OrielAppInfo.Detect();

    /// <summary>
    /// 设置应用元信息（对标 Tauri 的 <c>tauri.conf.json</c> 里 <c>productName</c> /
    /// <c>version</c> / <c>identifier</c>）。
    /// </summary>
    /// <param name="name">应用显示名；省略则从入口程序集的产品名推断。</param>
    /// <param name="version">应用版本；省略则从入口程序集的版本信息推断。</param>
    /// <param name="identifier">应用标识符，形如 <c>com.example.app</c>；省略则从程序集名推断。</param>
    /// <remarks>
    /// <para>
    /// 三项**独立**处理：只给其中一项时，另两项仍走自动推断。所以"我只想改个显示名"
    /// 不必把版本号也抄一遍——抄一遍的后果是那个版本号从此不再跟随程序集，静默地过期。
    /// </para>
    /// <para>
    /// 标识符的字符集会在 <c>Build()</c> 时校验（只允许字母、数字、<c>-</c>、<c>_</c>、<c>.</c>），
    /// 因为它是数据目录的组成部分。详见 <see cref="OrielAppInfo"/> 的 remarks。
    /// </para>
    /// </remarks>
    public OrielAppBuilder WithAppInfo(string? name = null, string? version = null, string? identifier = null)
    {
        AppInfo = new OrielAppInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? AppInfo.Name : name,
            Version = string.IsNullOrWhiteSpace(version) ? AppInfo.Version : version,
            Identifier = string.IsNullOrWhiteSpace(identifier) ? AppInfo.Identifier : identifier,
        };
        return this;
    }

    /// <summary>
    /// 注册 STJ 源生成的 <see cref="JsonSerializerContext"/>：DTO 命令参数/返回值由此实现 AOT 安全序列化。
    /// </summary>
    /// <remarks>
    /// 上下文**按应用实例持有**（存在这里，随 <see cref="OrielApp"/> 传给 IPC 分发器），
    /// 不写任何全局静态状态——因此同一进程里创建多个应用时，它们各用各的类型信息。
    /// </remarks>
    public OrielAppBuilder UseJsonContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        JsonContext = context;
        return this;
    }

    /// <summary>注册含 [OrielCommand] 实例命令的类型（无参构造创建单例）。</summary>
    public OrielAppBuilder AddCommands<T>() where T : new() => AddCommands<T>(() => new T());

    /// <summary>注册含 [OrielCommand] 实例命令的类型（工厂创建单例）。</summary>
    public OrielAppBuilder AddCommands<T>(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // factory() 的 T 无约束，可空性未知；此处显式断言非 null（null 工厂返回会在解析目标时即暴露）
        TargetFactories[typeof(T)] = () => factory()!;
        return this;
    }

    /// <summary>添加窗口（链式）。需要订阅窗口事件时用带 <c>onCreated</c> 参数的重载。</summary>
    public OrielAppBuilder AddWindow(Action<OrielWindowOptions>? configure = null)
    {
        AddWindowCore(configure, null);
        return this;
    }

    /// <summary>添加窗口并在 Run() 前回调 <paramref name="onCreated"/>（用于订阅 Loaded/Closing 等事件）。</summary>
    public OrielAppBuilder AddWindow(Action<OrielWindowOptions>? configure, Action<WebviewWindow> onCreated)
    {
        AddWindowCore(configure, onCreated);
        return this;
    }

    private void AddWindowCore(Action<OrielWindowOptions>? configure, Action<WebviewWindow>? onCreated)
    {
        var options = new OrielWindowOptions();
        configure?.Invoke(options);
        var window = new WebviewWindow();
        onCreated?.Invoke(window);
        PendingWindows.Add((window, options));
    }

    internal List<(WebviewWindow Window, OrielWindowOptions Options)> PendingWindows { get; } = [];

    /// <summary>覆盖 WebView2 用户数据目录（默认 <c>%LOCALAPPDATA%\&lt;标识符&gt;\WebView2</c>）。</summary>
    /// <remarks>
    /// <para>
    /// 默认值按**应用标识符**隔离（<see cref="WithAppInfo"/> 的 <c>identifier</c>），
    /// 所以两个基于本库的应用各自有一份 profile，不会互抢目录、也不会共用 cookie 与 localStorage。
    /// </para>
    /// <para>
    /// 需要这个覆盖的场景是真存在同一份 profile 的多个进程/多个产品形态（例如同一产品的
    /// 稳定版与预览版刻意共享登录态）。**不要让不相干的应用指向同一个目录**：WebView2 的
    /// 用户数据目录同一时刻只允许一个浏览器进程持有，撞上时后来者会以
    /// <c>0x800700AA</c>（请求的资源在使用中）装配失败。
    /// </para>
    /// </remarks>
    public OrielAppBuilder UseUserDataFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        UserDataFolder = path;
        return this;
    }

    /// <summary>应用级调试开关（默认取首个窗口的 Debug 配置）。</summary>
    public bool Debug { get; private set; }

    public OrielAppBuilder UseDebug(bool debug = true)
    {
        Debug = debug;
        return this;
    }

    internal Action<OrielWebView2RuntimeMissingEventArgs>? WebView2RuntimeMissingHandler { get; private set; }

    /// <summary>
    /// 注册「WebView2 运行时不可用」的处理回调（仅 Windows 会触发）。
    /// 注册后库不再弹默认错误框，提示与引导方式完全由回调决定；未注册时库弹一个说明
    /// 「缺什么、去哪里装」的错误框。运行时不存在的判定发生在窗口装配期，见
    /// <see cref="OrielWebView2RuntimeMissingEventArgs"/>。
    /// </summary>
    public OrielAppBuilder OnWebView2RuntimeMissing(Action<OrielWebView2RuntimeMissingEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        WebView2RuntimeMissingHandler = handler;
        return this;
    }

    internal string? SingleInstanceId { get; private set; }
    internal Action<WebviewWindow?, IReadOnlyList<string>>? SingleInstanceActivateHandler { get; private set; }

    /// <summary>
    /// 启用单实例：第二个实例启动时会通知首实例，随后**立即以退出码 0 退出**（不建窗、不进消息循环）。
    /// </summary>
    /// <param name="id">实例标识（任意字符串；内部会哈希成管道名，不会当路径用）。</param>
    /// <param name="onActivate">
    /// 首实例收到"又有一个实例启动了"时的回调：<c>(首实例的第一个窗口, 第二实例的启动参数)</c>；
    /// 不提供则只做默认的前置激活。参数来自第二实例的 <see cref="Oriel.CreateBuilder"/> 入参，
    /// 用于"用命令行让已运行的程序打开某个页面"。
    /// </param>
    /// <remarks>
    /// <b>判定发生在 <c>Build()</c></b>（不是 <c>Run()</c>）：所以
    /// <c>builder.Build()</c> 之后、装配自己的运行时之前就能问 <see cref="OrielApp.IsSecondaryInstance"/>，
    /// 第二实例不必先把数据库/设备连上再退出（下游反馈 2026-10-11 的 P0-2）。
    /// 代价是 <c>Build()</c> 不再纯粹：它会去抢一次进程级锁。
    /// </remarks>
    public OrielAppBuilder SingleInstance(string id, Action<WebviewWindow?, IReadOnlyList<string>>? onActivate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        SingleInstanceId = id;
        SingleInstanceActivateHandler = onActivate;
        return this;
    }

    internal OrielShellOptions? ShellOptions { get; private set; }

    /// <summary>
    /// 配置 Shell 集成（<see cref="OrielApp.OpenExternal"/> 的 scheme 白名单）。
    /// 默认只放行 <c>http</c>/<c>https</c>/<c>mailto</c>；需要自定义协议时在这里追加。
    /// </summary>
    public OrielAppBuilder UseShell(Action<OrielShellOptions>? configure = null)
    {
        var options = new OrielShellOptions();
        configure?.Invoke(options);
        ShellOptions = options;
        return this;
    }

    internal OrielCapabilityOptions? Capabilities { get; private set; }

    /// <summary>
    /// 声明页面可以调用哪些 IPC 命令（能力模型）。
    /// </summary>
    /// <remarks>
    /// <b>不调用它时的默认行为</b>：Debug 构建全放行，Release 构建**全部拒绝**（fail-closed）。
    /// 调用之后就以这里写的内容为准，Debug 与 Release 都一样。
    ///
    /// 只影响 <c>oriel.invoke</c>（有回执的命令）。单向的 <c>oriel.postMessage</c> 没有命令名，
    /// 由来源与令牌那一层覆盖。库保留的 <c>win.*</c> 窗口命令始终放行。
    /// </remarks>
    public OrielAppBuilder UseCapabilities(Action<OrielCapabilityOptions>? configure = null)
    {
        var options = new OrielCapabilityOptions();
        configure?.Invoke(options);
        Capabilities = options;
        return this;
    }

    internal string? AutoStartId { get; private set; }

    /// <summary>
    /// 覆盖开机自启的标识（默认取可执行文件名）。可执行文件名不适合当标识（多实例共存、名字带中文等）
    /// 或需要与单实例的 id 对齐时用它。
    /// </summary>
    public OrielAppBuilder UseAutoStartId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        AutoStartId = id;
        return this;
    }

    internal string? NotificationAppId { get; private set; }

    /// <summary>
    /// 覆盖系统通知的"应用标识"——Windows 上是 <b>AUMID</b>（系统"通知"设置里按它给应用分组），
    /// Linux 上是 <c>notify-send --app-name</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认取**入口程序集名**（经 <see cref="OrielNotificationAppId.Sanitize"/> 规范化），
    /// 因此同机安装的多个基于本库的应用在通知设置里是分开的，用户可以逐个静音。
    /// 不覆盖的话以前所有应用都叫 <c>OrielWeb</c>，用户在设置里既分不清也关不掉某一个。
    /// </para>
    /// <para>
    /// 需要覆盖的典型场景：与应用清单/开始菜单快捷方式里已注册的 AUMID 对齐（打包分发时），
    /// 或程序集名带版本号、不适合当用户可见名字。
    /// </para>
    /// </remarks>
    public OrielAppBuilder UseNotificationAppId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        NotificationAppId = appId;
        return this;
    }

    internal List<OrielTrayOptions> TrayOptionsList { get; } = [];

    /// <summary>
    /// 兼容既有单托盘代码：第一个托盘选项（没有则 null）。
    /// </summary>
    /// <remarks>
    /// 早先这是唯一的托盘配置槽，现在托盘可以配多个（<see cref="AddTray"/> 可重复调用）。
    /// 保留这个属性是为了让"只配一个托盘"的既有代码与 <see cref="OrielApp.RestoreTray"/> 不受影响。
    /// </remarks>
    internal OrielTrayOptions? TrayOptions => TrayOptionsList.Count > 0 ? TrayOptionsList[0] : null;

    internal TimeSpan? ExitTimeoutValue { get; private set; }
    internal int? ForceExitCodeValue { get; private set; }

    /// <summary>退出看门狗是否启用（默认启用，见 <see cref="OrielApp.ExitTimeout"/>）。</summary>
    internal bool ExitWatchdogEnabled { get; private set; } = true;

    /// <summary>
    /// 覆盖退出清理的超时时间（从"请求退出"算起，默认 10 秒）。
    /// </summary>
    /// <remarks>
    /// 超时后看门狗会**强制结束进程**（见 <see cref="OrielApp.ExitTimeout"/>）。调大它意味着
    /// "允许清理跑更久"，代价是清理真的卡住时进程驻留更久——驻留期间可执行文件映像仍被占用，
    /// 现场表现就是"程序删不掉/装不上"（下游反馈 2026-10-14 的 P0-1）。
    /// </remarks>
    public OrielAppBuilder UseExitTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "退出超时必须是正值。");
        }

        ExitTimeoutValue = timeout;
        return this;
    }

    /// <summary>
    /// 覆盖被看门狗强制结束时的进程退出码（默认 <c>2</c>）。
    /// </summary>
    /// <remarks>
    /// 与正常退出（<c>0</c>）区分开：脚本与现场排障能据此判断"这次不是正常退出的"，
    /// 而不用去猜为什么进程是没的。
    /// </remarks>
    public OrielAppBuilder UseForceExitCode(int exitCode)
    {
        ForceExitCodeValue = exitCode;
        return this;
    }

    /// <summary>
    /// 关闭退出看门狗（默认启用）。关闭后 <see cref="OrielApp.Quit"/> 只请求退出，
    /// 不再保证进程最终结束——清理卡住时进程会一直驻留。
    /// </summary>
    /// <remarks>
    /// 只有"宿主要自己接管退出流程、并且有充分理由相信清理不会卡"时才该关闭它。
    /// </remarks>
    public OrielAppBuilder UseExitWatchdog(bool enabled = true)
    {
        ExitWatchdogEnabled = enabled;
        return this;
    }

    /// <summary>
    /// 启用系统托盘图标（可多次调用以创建**多个**托盘，各自用 <see cref="OrielTrayOptions.Id"/> 区分）。
    /// 运行期经 <see cref="OrielApp.Tray"/> / <see cref="OrielApp.GetTray"/> / <see cref="OrielApp.Trays"/> 取得，
    /// 用它设置菜单、订阅事件。平台差异见 <see cref="OrielTray"/> 的说明。
    /// </summary>
    /// <remarks>
    /// 不显式给 <c>Id</c> 时：第一次调用用固定 id <c>"main"</c>（让 <see cref="OrielApp.RestoreTray"/> 幂等），
    /// 后续调用各自分配随机 id。多托盘应用应当显式给每个托盘不同的 id。
    /// </remarks>
    public OrielAppBuilder AddTray(Action<OrielTrayOptions>? configure = null)
    {
        var options = new OrielTrayOptions();
        configure?.Invoke(options);
        TrayOptionsList.Add(options);
        return this;
    }

    public OrielApp Build() => new(this);

    /// <summary>构建并阻塞运行，直到所有窗口关闭。</summary>
    public void Run() => Build().Run();
}

/// <summary>应用静态入口。</summary>
public static class Oriel
{
    /// <summary>
    /// 创建构建器。
    /// </summary>
    /// <param name="args">
    /// 应用启动参数。库不解析它，但**单实例**会用到：启用 <see cref="OrielAppBuilder.SingleInstance"/>
    /// 时，第二实例把这份参数随激活请求发给首实例（见其 <c>onActivate</c>），于是"用命令行让已运行的
    /// 程序打开某个页面"这类需求成立。应用也可以自行读取它做别的解析。
    /// </param>
    public static OrielAppBuilder CreateBuilder(string[]? args = null)
        => new() { StartupArgs = args ?? [] };
}
