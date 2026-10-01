using System.Diagnostics;
using System.Text.Json.Serialization;
using OrielWeb;
using OrielDemo;
using OrielWeb.Ipc;

namespace OrielDemo;

internal static class Program
{
    /// <summary>
    /// 内嵌资源的虚拟主机名。必须与 <c>UseEmbeddedAssets(host)</c> 一致：
    /// 操作台页面的 URL 要手写它（库里的 <c>AssetHost</c> 是 internal，示例拿不到）。
    /// </summary>
    private const string AssetHost = "app.oriel";

    internal static WebviewWindow? Window;

    /// <summary>取 <c>--name value</c> 形式的参数值；未提供时返回 null。</summary>
    private static string? GetOptionValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Linux/macOS 上的默认窗口图标：产物目录旁的 <c>app.png</c>；没有就用 null（窗口不带图标）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 <see cref="AppContext.BaseDirectory"/> 拼**绝对路径**：图标接口按当前工作目录解析相对路径，
    /// 而 macOS 从 <c>.app</c> 启动时 cwd 是 <c>/</c>——相对路径在那里必然失效。
    /// </para>
    /// <para>
    /// Windows 直接返回 null：那边库会主动从 exe 取图标（csproj 里的 <c>ApplicationIcon</c>），
    /// 不需要应用给文件。
    /// </para>
    /// </remarks>
    private static string? DefaultIconPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "app.png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// <c>--selftest</c> 认得的名字。加一个自检只需在这里加名字 + 在 Main 里加一行判定。
    /// </summary>
    private static readonly string[] SelfTestNames =
        ["nav", "ipc", "clipboard", "theme", "single-instance", "shell"];

    /// <summary>本次请求的自检名字是否就是 <paramref name="name"/>（忽略大小写与首尾空白）。</summary>
    private static bool IsSelfTest(string? requested, string name)
        => requested is not null
            && string.Equals(requested.Trim(), name, StringComparison.OrdinalIgnoreCase);

    [STAThread]
    private static void Main(string[] args)
    {
        // --hidden：以隐藏窗口启动。既用于验证三平台的 Hidden 语义（窗口不上屏，但页面照常
        // 加载、IPC 照常往返），也是"先隐藏预热、准备好再显示"这类用法的示例。
        var hidden = args.Contains("--hidden");
        // --icon <path>：设置窗口图标（Windows/Linux 是窗口图标，macOS 会落到 Dock 图标）。
        // 不给时**在 Linux/macOS 上回落到随产物一起分发的 app.png**——那两个平台没有"从 exe 取图标"
        // 这回事（ELF/Mach-O 都不带图标，macOS 的图标在 .app bundle 里），不显式给个文件就是通用图标。
        // Windows 不需要：库会主动从 exe 取（见 README「应用图标」）。
        var icon = GetOptionValue(args, "--icon") ?? DefaultIconPath();
        // --selftest <名字>：无人交互的自检（CI 用），跑完打印结论并以退出码表达成败。
        // 可用名字见 SelfTestNames。合并成一个开关是为了让 demo 的入口干净——
        // 以前是六个独立开关（--nav-selftest 之类），能力没少，只是不再往命令行上摆一排。
        // 自检的**输出结论格式没变**（NAV-SELFTEST: PASS 等），脚本仍按原来的行去 grep。
        var selfTest = GetOptionValue(args, "--selftest");
        if (args.Contains("--selftest") && string.IsNullOrWhiteSpace(selfTest))
        {
            Console.Error.WriteLine($"--selftest 需要一个名字。可用：{string.Join(" | ", SelfTestNames)}");
            Environment.ExitCode = 2;
            return;
        }

        if (selfTest is not null && !SelfTestNames.Contains(selfTest.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"未知的自检名字「{selfTest}」。可用：{string.Join(" | ", SelfTestNames)}");
            Environment.ExitCode = 2;
            return;
        }

        // nav：跳转 → 后退 → 前进 → 刷新 → 失败导航，打印 NAV-SELFTEST 结论
        var navSelfTest = IsSelfTest(selfTest, "nav");
        // ipc：页面 console → 宿主、页面 postMessage → 宿主、宿主 EmitEvent → 页面 → 回显宿主
        //（它会顺带打开 ConsoleForwarding）
        var ipcSelfTest = IsSelfTest(selfTest, "ipc");
        // clipboard：剪贴板文本与 HTML 的写→读回（会覆盖系统剪贴板内容）
        var clipboardSelfTest = IsSelfTest(selfTest, "clipboard");
        // theme：主题读取与 theme.changed 事件通道（页面回显确认；深浅两条路径由 CI 造值）
        var themeSelfTest = IsSelfTest(selfTest, "theme");
        // single-instance：双进程协作（脚本起两个实例）
        var singleInstanceSelfTest = IsSelfTest(selfTest, "single-instance");
        // shell：托盘、通知、菜单、快捷键、自启、Shell、拖放
        var shellSelfTest = IsSelfTest(selfTest, "shell");
        // --manual-check：手动验证操作台（停在那里等人点，不自动退出）。
        // 与 --selftest shell 的分工：那个是给 CI 的无人断言，这个是给人看的——
        // 每项做成按钮，托管侧的回调（托盘菜单项、通知点击、快捷键、拖放）回显到页面。
        // 之所以要回显：demo 在 Windows 上是 WinExe，没有控制台，打印的东西看不见。
        //
        // **它是 demo 的默认页面**：直接运行 exe 看到的就是操作台。
        // 想看 Todo 示例页（"怎么用本库写应用"的示范）加 --todo；跑无人自检时也不用它，
        // 免得两种模式去争同一个托盘。
        bool anySelfTest = navSelfTest || ipcSelfTest || clipboardSelfTest || themeSelfTest
            || singleInstanceSelfTest || shellSelfTest;
        var manualCheck = !anySelfTest && !args.Contains("--todo");

        // 主题自检需要 OrielApp（主题是应用级的），所以这里显式 Build 再 Run；
        // app 变量先声明、后赋值，闭包在 onCreated 里读它（onCreated 发生在 Run 内部，那时已赋值）。
        OrielApp? app = null;

        var builder = Oriel.CreateBuilder(args)
            .UseEmbeddedAssets(AssetHost)
            .UseJsonContext(AppJsonContext.Default)
            .AddCommands<TodoCommands>()
            .AddCommands<WindowCommands>()
            .AddCommands<ManualCommands>()
            .UseDebug()
            .OnWebView2RuntimeMissing(HandleWebView2RuntimeMissing)
            .AddWindow(w =>
            {
                if (hidden)
                {
                    w.WithHidden();
                }
                if (icon is not null)
                {
                    w.WithIcon(icon);
                }
                if (ipcSelfTest)
                {
                    // 默认关闭（见选项说明）；自检需要它才能收到页面的 console 输出。
                    w.WithConsoleForwarding();
                }
                if (manualCheck)
                {
                    // 操作台走独立页面：Todo 页保持它作为"API 用法示例"的干净面貌
                    w.WithUrl($"https://{AssetHost}/manual-check.html");
                }
                w.WithTitle(manualCheck ? "Oriel Demo — 手动验证" : "Oriel Demo — Todo")
                    .WithSize(1024, 720)
                    .WithMinSize(640, 480)
                    .WithFrameless()
                    .Centered();
            }, win =>
            {
                Window = win;
                if (themeSelfTest)
                {
                    // onCreated 阶段窗口门面还没有后端（window.Attach 要等 CreateWindow 返回），
                    // 那时 win.App 会抛异常。推迟到 Loaded——那时后端已就位。
                    WebviewWindow created = win;
                    created.Loaded += () => ThemeSelfTest.Attach(created.App, created);
                }
                if (manualCheck)
                {
                    // 同上：需要 app（托盘/快捷键/自启都是应用级），所以等 Loaded
                    WebviewWindow created = win;
                    created.Loaded += () => ManualCheck.Attach(created.App, created);
                }
                if (navSelfTest)
                {
                    NavSelfTest.Attach(win);
                }
                if (ipcSelfTest)
                {
                    IpcSelfTest.Attach(win);
                }
                if (clipboardSelfTest)
                {
                    ClipboardSelfTest.Attach(win);
                }
            });

        if (singleInstanceSelfTest)
        {
            // 单实例必须配在 Run() 之前：判定发生在建窗之前（否则第二个实例会先闪个窗口）
            builder.SingleInstance(
                SingleInstanceSelfTest.InstanceId,
                SingleInstanceSelfTest.OnActivatedBySecondInstance);
        }

        if (shellSelfTest || manualCheck)
        {
            // 托盘是应用级能力，必须在 Run() 之前配置：原生资源在 Run 期间创建。
            // 操作台同样要它——这一批里"看得见"的东西大多挂在托盘上。
            builder.AddTray(o =>
            {
                o.Tooltip = shellSelfTest ? "OrielWeb self-test" : "OrielWeb 手动验证";
                // 操作台上让左键也能弹菜单：手动验证时"点了没反应"最难判断，
                // 而 Windows 的默认语义是左键只回调、右键才弹菜单。
                o.MenuOnClick = manualCheck;
            });
        }

        app = builder.Build();

        if (singleInstanceSelfTest)
        {
            SingleInstanceSelfTest.StartWatchdog();
        }

        if (shellSelfTest)
        {
            ShellSelfTest.Start(app);
        }

        app.Run();

        // 自检的结论已在运行期打印，这里只把成败映射到进程退出码（CI 的冒烟脚本据此判定）。
        if ((navSelfTest && NavSelfTest.Failed)
            || (ipcSelfTest && IpcSelfTest.Failed)
            || (clipboardSelfTest && ClipboardSelfTest.Failed)
            || (themeSelfTest && ThemeSelfTest.Failed)
            || (singleInstanceSelfTest && SingleInstanceSelfTest.Failed)
            || (shellSelfTest && ShellSelfTest.Failed))
        {
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// WebView2 运行时缺失时的引导：讲清缺什么、打开官方下载页，随后应用退出（装完再启动）。
    /// 不注册此回调时，库只弹一个含下载地址的错误框。
    /// </summary>
    private static void HandleWebView2RuntimeMissing(OrielWebView2RuntimeMissingEventArgs e)
    {
        e.Window.ShowMessage(
            "未检测到 Microsoft Edge WebView2 运行时。本应用的界面完全由网页渲染，缺少它无法显示。\r\n\r\n" +
            "点「确定」后将打开微软的下载页面；安装完成后请重新启动本应用。\r\n\r\n" +
            OrielWebView2RuntimeMissingEventArgs.DownloadUrl,
            "缺少 WebView2 运行时",
            OrielMessageBoxIcon.Warning);

        // 用户已确认：打开下载页。本窗口随后由库销毁，进程随之退出。
        Process.Start(new ProcessStartInfo
        {
            FileName = OrielWebView2RuntimeMissingEventArgs.DownloadUrl,
            UseShellExecute = true,
        });
    }
}

/// <summary>无边框窗口控制命令：由页面自绘标题栏调用（按平台分支拖动模式）。</summary>
public sealed partial class WindowCommands
{
    [OrielCommand("win.minimize")]
    public static void Minimize() => Program.Window?.Minimize();

    /// <summary>最大化/还原切换，返回切换后是否最大化（页面据此更新按钮图标）。</summary>
    [OrielCommand("win.toggleMaximize")]
    public static bool ToggleMaximize() => Program.Window?.ToggleMaximize() ?? false;

    [OrielCommand("win.close")]
    public static void Close() => Program.Window?.Close();

    /// <summary>Windows：进入原生模态拖动（参数忽略）；macOS：无操作（用 dragStart/dragTo）。</summary>
    [OrielCommand("win.drag")]
    public static void Drag() => Program.Window?.BeginDrag();

    /// <summary>macOS 流式拖动起点。px/py = 指针屏幕坐标（CSS 点）；wx/wy/ww/wh = 窗口几何；sh = 屏高。</summary>
    [OrielCommand("win.dragStart")]
    public static void DragStart(double px, double py, double wx, double wy, double ww, double wh, double sh)
        => Program.Window?.BeginDragStreaming(px, py, wx, wy, ww, wh, sh);

    /// <summary>macOS 流式拖动增量（CSS 点，y 向下为正）；Windows 上为 no-op。</summary>
    [OrielCommand("win.dragTo")]
    public static void DragTo(double dx, double dy) => Program.Window?.DragTo(dx, dy);

    [OrielCommand("win.dragEnd")]
    public static void DragEnd() => Program.Window?.EndDrag();

    [OrielCommand("win.toggleFullscreen")]
    public static bool ToggleFullscreen() => Program.Window?.ToggleFullscreen() ?? false;

    [OrielCommand("win.toggleOnTop")]
    public static bool ToggleOnTop() => Program.Window?.ToggleOnTop() ?? false;

    [OrielCommand("win.pickFile")]
    public static string? PickFile() => Program.Window?.ShowOpenFileDialog("选择文件", "所有文件|*.*");

    /// <summary>
    /// 弹出一个覆盖各形态的上下文菜单——真机验证用的入口（页面里用
    /// <c>oriel.invoke('win.contextMenu')</c> 或 devtools 控制台触发）。
    /// 无人自检**不**调它：上下文菜单要等用户选择，Windows 上还会阻塞。
    /// </summary>
    [OrielCommand("win.contextMenu")]
    public static void ShowContextMenu() => Program.Window?.ShowContextMenu(
    [
        OrielMenuItem.Item("ctx-hello", "Hello from context menu"),
        OrielMenuItem.Separator(),
        new OrielMenuItem { Id = "ctx-pin", Label = "置顶（勾选示例）", Checked = true },
        new OrielMenuItem { Id = "ctx-disabled", Label = "禁用项", Enabled = false },
        new OrielMenuItem
        {
            Label = "子菜单",
            Items = [OrielMenuItem.Item("ctx-sub", "子项")],
        },
        OrielMenuItem.Separator(),
        OrielMenuItem.RoleItem(OrielMenuRole.Copy),
        OrielMenuItem.RoleItem(OrielMenuRole.Close),
    ]);
}

/// <summary>DTO：STJ 源生成上下文（AOT 安全序列化的唯一入口）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoItem[]))]
[JsonSerializable(typeof(SysInfo))]
[JsonSerializable(typeof(ManualLog))]
[JsonSerializable(typeof(ManualState))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
