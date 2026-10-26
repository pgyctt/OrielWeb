using OrielWeb;
using OrielWeb.Ipc;

namespace TauriSplashscreen;

/// <summary>
/// 入口。
/// </summary>
/// <remarks>
/// <para>
/// 原文的 <c>tauri.conf.json</c> 声明了两个窗口：<c>main</c>（<c>visible: false</c>）与
/// <c>splashscreen</c>（400x200、<c>decorations: false</c>）。本库把这层配置换成两次
/// <see cref="OrielAppBuilder.AddWindow"/>——没有配置文件，也就没有"配置与代码谁优先"的问题。
/// </para>
/// <para>
/// <b>为什么用 <c>Build()</c> 而不是 <c>Run()</c></b>：<c>close_splashscreen</c> 命令要拿到
/// <see cref="OrielApp"/> 才能按 label 取窗口，而命令类在第一次 invoke 时才实例化。
/// 所以先 Build、把 app 交给 switcher、再 Run——这是"命令需要应用级句柄"时的标准写法
/// （Tauri 那边靠给命令注入 <c>AppHandle</c>，本库不注入宿主对象）。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var switcher = new SplashSwitcher();

        OrielApp app = Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            .WithAppInfo(name: "Tauri 示例 · Splashscreen", identifier: "com.orielweb.samples.tauri.splashscreen")
            .AddCommands(() => new SplashCommands(switcher))
            .UseCapabilities(c => c.Allow("close_splashscreen"))
            .AddWindow(w => w
                .WithLabel("main")
                .WithTitle("Tauri 示例 · Splashscreen")
                .WithSize(800, 600)
                .WithMinSize(560, 420)
                // 对应 tauri.conf.json 的 "visible": false：页面照常加载、IPC 照常往返，
                // 只是窗口不上屏——所以页面里那个"2 秒后关启动画面"的定时器照常跑。
                .WithHidden()
                .Centered())
            .AddWindow(w => w
                .WithLabel("splashscreen")
                .WithTitle("启动中")
                .WithSize(400, 200)
                .WithFrameless()
                .WithResizable(false)
                // 内嵌资源里的另一页。host 必须与 UseEmbeddedAssets 的默认值一致。
                .WithUrl("oriel://app.oriel/splashscreen.html")
                .Centered())
            .Build();

        switcher.Attach(app);
        app.Run();
    }
}

/// <summary>
/// 两个窗口之间的切换。对应原文命令里的 <c>app.get_webview_window(...)</c> 那两行。
/// </summary>
/// <remarks>
/// 命令类要拿应用级句柄，只能由外部在 <c>Build()</c> 之后注入——本库的命令参数只从 JSON 取，
/// 不注入 <c>AppHandle</c> / <c>Window</c> 这类宿主对象。这不是缺能力：注入会让
/// "这个参数是数据还是句柄"变成第二套规则，而句柄的获取点（这里）只有一处。
/// </remarks>
internal sealed class SplashSwitcher
{
    private OrielApp? _app;

    internal void Attach(OrielApp app) => _app = app;

    internal void CloseSplash()
    {
        OrielApp app = _app ?? throw new OrielIpcException("应用尚未就绪：close_splashscreen 调用得过早。");

        // 先关启动画面再显示主窗口：反过来会让启动画面在主窗口之上多留一帧。
        app.GetWindow("splashscreen")?.Close();
        app.GetWindow("main")?.Show();
    }
}

/// <summary>命令类。与原文的 <c>#[tauri::command] fn close_splashscreen</c> 同名同义。</summary>
internal sealed partial class SplashCommands(SplashSwitcher switcher)
{
    [OrielCommand("close_splashscreen")]
    public void CloseSplashscreen() => switcher.CloseSplash();
}
