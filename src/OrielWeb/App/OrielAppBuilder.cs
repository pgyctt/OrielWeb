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
    internal Dictionary<Type, Func<object>> TargetFactories { get; } = [];

    /// <summary>启用内嵌前端资源：<paramref name="resourcePrefix"/> 为程序集内嵌资源名前缀（默认 "程序集名.wwwroot."），经 <paramref name="host"/> 虚拟主机提供。</summary>
    public OrielAppBuilder UseEmbeddedAssets(string host = "app.oriel", string? resourcePrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        UseAssets = true;
        AssetHost = host;
        AssetResourcePrefix = resourcePrefix;
        return this;
    }

    /// <summary>注册 STJ 源生成的 JsonSerializerContext：DTO 命令参数/返回值由此实现 AOT 安全序列化。</summary>
    public OrielAppBuilder UseJsonContext(JsonSerializerContext context)
    {
        OrielJson.Use(context);
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

    /// <summary>覆盖 WebView2 用户数据目录（默认 %LOCALAPPDATA%\OrielWeb\WebView2）。</summary>
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
    internal Action<WebviewWindow?>? SingleInstanceActivateHandler { get; private set; }

    /// <summary>
    /// 启用单实例：第二个实例启动时会通知首实例，随后**立即以退出码 0 退出**（不建窗、不进消息循环）。
    /// 首实例收到通知后默认把窗口前置并激活，并调用 <paramref name="onActivate"/>（可选）。
    /// </summary>
    /// <param name="id">实例标识（任意字符串；内部会哈希成管道名，不会当路径用）。</param>
    /// <param name="onActivate">首实例收到"又有一个实例启动了"时的回调；不提供则只做默认的前置激活。</param>
    public OrielAppBuilder SingleInstance(string id, Action<WebviewWindow?>? onActivate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        SingleInstanceId = id;
        SingleInstanceActivateHandler = onActivate;
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

    internal OrielTrayOptions? TrayOptions { get; private set; }

    /// <summary>
    /// 启用系统托盘图标（应用级，最多一个）。运行期经 <see cref="OrielApp.Tray"/> 取得，
    /// 用它设置菜单、订阅点击。平台差异见 <see cref="OrielTray"/> 的说明。
    /// </summary>
    public OrielAppBuilder AddTray(Action<OrielTrayOptions>? configure = null)
    {
        var options = new OrielTrayOptions();
        configure?.Invoke(options);
        TrayOptions = options;
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
    /// 创建构建器。<paramref name="args"/> 目前不参与配置（保留以对齐通用启动模板的签名），
    /// 需要读取命令行参数的应用可自行解析后再调用对应配置方法。
    /// </summary>
    public static OrielAppBuilder CreateBuilder(string[]? args = null)
    {
        _ = args;
        return new();
    }
}
