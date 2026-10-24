using System.Diagnostics;

using System.Text.Json.Serialization;
using OrielWeb;
using OrielConformance;

namespace OrielDemo;

/// <summary>
/// 示例应用入口。
/// </summary>
/// <remarks>
/// <para>
/// 这个文件现在只剩**示例应用自己的事**：应用元信息、资产、Todo 与手动验证命令、
/// 以及默认窗口的外观。契约自检（<c>--selftest</c>）的全部装配在
/// <see cref="ConformanceRun"/> 里——那批代码是跨平台契约测试，不属于示例。
/// </para>
/// <para>
/// 保留 <c>--selftest</c> 转发入口是为了让 CI 的三条 verify 脚本仍然只面对一个二进制：
/// 它们各自 publish 一次，然后用同一份产物跑 selftest / shell / smoke 三个阶段。
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// 内嵌资源的虚拟主机名。必须与 <c>UseEmbeddedAssets(host)</c> 一致：
    /// 操作台页面的 URL 要手写它（库里的 <c>AssetHost</c> 是 internal，示例拿不到）。
    /// </summary>
    private const string AssetHost = "app.oriel";

    /// <summary>主窗口。手动验证命令的回调（托盘菜单、快捷键、拖放）要经它回到页面。</summary>
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

    [STAThread]
    private static void Main(string[] args)
    {
        // 自检的结论行是中文（"失败: …"），而 Windows 控制台的默认代码页是 GBK：
        // 重定向到文件或管道时按 GBK 写出，CI 里用 UTF-8 的 grep 会匹配不到——
        // 症状是"自检明明跑了，脚本却说没有结论行"。显式置 UTF-8 让三平台一致。
        // 只在输出被重定向时才有意义；控制台直连时设了也无害。
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // 没有控制台（WinExe 双击启动）时设置会失败，那不是错误
        }

        // 库的诊断通道默认是**静默**的（没有订阅者就什么都不做），示例把它接到 stderr：
        // 这样"WebView2 装配失败"这类只弹窗的故障在脚本/CI 里也留有痕迹。
        // 顺带一提，弹窗本身在无人值守时会把"失败"变成"卡住"——日志是唯一可靠的那份证据。
        OrielDiagnostics.Reported += e => Console.Error.WriteLine(e.Message);

        // --hidden：以隐藏窗口启动。既用于验证三平台的 Hidden 语义（窗口不上屏，但页面照常
        // 加载、IPC 照常往返），也是"先隐藏预热、准备好再显示"这类用法的示例。
        var hidden = args.Contains("--hidden");
        // --icon <path>：设置窗口图标（Windows/Linux 是窗口图标，macOS 会落到 Dock 图标）。
        // 不给时**在 Linux/macOS 上回落到随产物一起分发的 app.png**——那两个平台没有"从 exe 取图标"
        // 这回事（ELF/Mach-O 都不带图标，macOS 的图标在 .app bundle 里），不显式给个文件就是通用图标。
        // Windows 不需要：库会主动从 exe 取（见 README「应用图标」）。
        // 相对路径按**可执行文件所在目录**解析（不是 cwd），见 OrielWindowOptions.Icon。
        var icon = GetOptionValue(args, "--icon") ?? DefaultIconPath();
        // --require-icon：把"图标文件必须存在"打开（默认是缺失即降级为平台默认图标）。
        // 用来验证"打包漏了图标文件"这类问题能在启动时被立刻发现，而不是静默降级。
        var requireIcon = args.Contains("--require-icon");
        // --selftest <名字>：无人交互的契约自检（CI 用）。可用名字由 ConformanceRun.Names 定义，
        // 装配也全在那个类里；这里只负责参数校验与转发。
        var selfTest = GetOptionValue(args, "--selftest");
        if (args.Contains("--selftest") && string.IsNullOrWhiteSpace(selfTest))
        {
            Console.Error.WriteLine($"--selftest 需要一个名字。可用：{string.Join(" | ", ConformanceRun.Names)}");
            Environment.ExitCode = 2;
            return;
        }

        if (selfTest is not null && !ConformanceRun.Names.Contains(selfTest.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"未知的自检名字「{selfTest}」。可用：{string.Join(" | ", ConformanceRun.Names)}");
            Environment.ExitCode = 2;
            return;
        }

        var run = new ConformanceRun(selfTest);

        // --manual-check：手动验证操作台（停在那里等人点，不自动退出）。
        // 与 --selftest shell 的分工：那个是给 CI 的无人断言，这个是给人看的——
        // 每项做成按钮，托管侧的回调（托盘菜单项、通知点击、快捷键、拖放）回显到页面。
        // 之所以要回显：demo 在 Windows 上是 WinExe，没有控制台，打印的东西看不见。
        //
        // **它是 demo 的默认页面**：直接运行 exe 看到的就是操作台。
        // 想看 Todo 示例页（"怎么用本库写应用"的示范）加 --todo；跑无人自检时也不用它，
        // 免得两种模式去争同一个托盘。
        bool manualCheck = !run.Any && !args.Contains("--todo");

        var builder = Oriel.CreateBuilder(args)
            .UseEmbeddedAssets(AssetHost)
            .UseJsonContext(AppJsonContext.Default)
            // 应用元信息：显式给。不显式给时也会从程序集推（产品名 / 版本 / 程序集名），
            // 但标识符会变成 "orieldemo" 这种从程序集名来的东西——而它将成为应用数据目录的
            // 组成部分（见 docs/API.md 的 app 模块一节），所以正式应用应当显式指定。
            .WithAppInfo(name: "OrielWeb 示例", identifier: "com.orielweb.demo")
            .AddCommands<TodoCommands>()
            .AddCommands<ManualCommands>()
            // 示例应用真正会调用的命令。自检需要的那一大串在 ConformanceRun.AllowCapabilities 里——
            // 那是**测试夹具**，不是这个应用的能力面，混在一起会让"这个应用需要什么权限"读不出来。
            .UseCapabilities(c =>
            {
                c.Allow("todo.*", "sys.info", "manual.*");

                run.AllowCapabilities(c);
            })
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
                    if (requireIcon)
                    {
                        w.WithRequireIcon();
                    }
                    w.WithIcon(icon);
                }

                // 自检窗口的标题、尺寸、初始页面与是否无边框由夹具决定。
                run.ConfigureWindow(w);

                if (manualCheck)
                {
                    // 操作台走独立页面：Todo 页保持它作为"API 用法示例"的干净面貌
                    w.WithUrl($"https://{AssetHost}/manual-check.html");
                }

                if (!run.Any)
                {
                    // 示例应用自己的外观。跑自检时上面那段已经设过，不能覆盖。
                    // 标题必须含 "Oriel Demo"：冒烟脚本按这个子串枚举原生窗口。
                    w.WithTitle(manualCheck ? "Oriel Demo — 手动验证" : "Oriel Demo — Todo")
                        .WithSize(1024, 720)
                        .WithMinSize(640, 480)
                        .Centered()
                        .WithFrameless();
                }
            }, win =>
            {
                Window = win;

                if (manualCheck)
                {
                    // 需要 app（托盘/快捷键/自启都是应用级），所以等 Loaded——
                    // onCreated 阶段 win.App 会抛（后端要等 CreateWindow 返回才 Attach）。
                    win.Loaded += () => ManualCheck.Attach(win.App, win);
                }

                run.Attach(win);
            });

        run.BeforeBuild(builder);

        if (manualCheck || run.NeedsTray)
        {
            // 托盘是应用级能力，必须在 Run() 之前配置：原生资源在 Run 期间创建。
            // 操作台同样要它——"看得见"的东西大多挂在托盘上。
            builder.AddTray(o =>
            {
                if (manualCheck)
                {
                    o.Tooltip = "OrielWeb 手动验证";
                    // 操作台上让左键也能弹菜单：手动验证时"点了没反应"最难判断，
                    // 而 Windows 的默认语义是左键只回调、右键才弹菜单。
                    o.MenuOnClick = true;
                }
                else
                {
                    run.ConfigureTray(o);
                }

                // **图标不是可选的观感细节，是托盘可见性的前提。**
                // macOS 的 NSStatusItem 在没有 image 也没有 title 时宽度为 0：
                // 状态项确实存在、也能收到点击，但**状态栏里那一格是空的**——
                // 用户看到的是"托盘没工作"，排查时却查不到任何错误。
                // Tauri 同样如此（TrayIconBuilder 不给图标就会得到空状态项）。
                if (icon is not null)
                {
                    o.IconPath = icon;
                }
            });
        }

        OrielApp app = builder.Build();

        run.AfterBuild(app);

        app.Run();

        run.AfterRun();

        // 自检的结论已在运行期打印，这里只把成败映射到进程退出码（CI 的脚本据此判定）。
        run.ApplyExitCode();
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

// 无边框窗口的 win.* 命令（最小化 / 最大化 / 关闭 / 拖动 / 全屏 / 置顶 / 选文件 / 上下文菜单）
// **由库内建**，应用不再注册——见 OrielBuiltInWindowCommands 与 API.md 第 2 节。
// 应用自定义的 win.* 会被内建遮蔽（win. 是保留前缀），所以自己那点菜单内容请用别的名字
// （例如 ManualCheck 里的 manual.contextMenu）。

/// <summary>DTO：STJ 源生成上下文（AOT 安全序列化的唯一入口）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoItem[]))]
[JsonSerializable(typeof(SysInfo))]
[JsonSerializable(typeof(ManualLog))]
[JsonSerializable(typeof(ManualState))]
// 契约自检的 conf.list 返回 string[]：它要在**同一个**上下文里注册，
// 因为应用只有一个 JsonSerializerContext（见 UseJsonContext）。
[JsonSerializable(typeof(string[]))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
