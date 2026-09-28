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
    internal List<OrielWindowOptions> WindowOptionsList { get; } = [];
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
        TargetFactories[typeof(T)] = () => (object)factory();
        return this;
    }

    /// <summary>添加窗口（链式）。需要订阅窗口事件时用带 <paramref name="onCreated"/> 的重载。</summary>
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
        WindowOptionsList.Add(options);
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

    public OrielApp Build() => new(this);

    /// <summary>构建并阻塞运行，直到所有窗口关闭。</summary>
    public void Run() => Build().Run();
}

/// <summary>应用静态入口。</summary>
public static class Oriel
{
    public static OrielAppBuilder CreateBuilder(string[]? args = null) => new();
}
