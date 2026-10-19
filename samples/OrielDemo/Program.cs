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
        ["nav", "ipc", "theme", "single-instance", "shell", "capability", "multiwindow", "scheme", "exit", "exit-timeout", "threading", "tray", "tray-js", "geometry", "geometry-frameless", "windowevents", "event", "close", "window", "windownew", "monitors", "menu", "api"];

    /// <summary>本次请求的自检名字是否就是 <paramref name="name"/>（忽略大小写与首尾空白）。</summary>
    private static bool IsSelfTest(string? requested, string name)
        => requested is not null
            && string.Equals(requested.Trim(), name, StringComparison.OrdinalIgnoreCase);

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
        // theme：主题读取与 theme.changed 事件通道（页面回显确认；深浅两条路径由 CI 造值）
        var themeSelfTest = IsSelfTest(selfTest, "theme");
        // single-instance：双进程协作（脚本起两个实例）
        var singleInstanceSelfTest = IsSelfTest(selfTest, "single-instance");
        // shell：托盘、通知、菜单、快捷键、自启、Shell、拖放
        var shellSelfTest = IsSelfTest(selfTest, "shell");
        // capability：能力模型的放行 / 拒绝两条路径（页面真实 invoke 各一次）
        var capabilitySelfTest = IsSelfTest(selfTest, "capability");
        // multiwindow：运行时新建窗口 → 每个窗口各一套会话 → 关掉一个不退出 → win.close 只关发起者
        var multiWindowSelfTest = IsSelfTest(selfTest, "multiwindow");

        // scheme：打印页面来源与"来源决定的能力"（isSecureContext / crypto.subtle / localStorage /
        // 相对路径 fetch），并断言其中两条（见 SchemeSelfTest 的分界线）
        var schemeSelfTest = IsSelfTest(selfTest, "scheme");
        // exit / exit-timeout：退出语义。前者取证"正常退出两个事件都到、退出码 0"，
        // 后者取证"清理卡住时看门狗强杀、退出码 2"（必须子进程，见 ExitSelfTest 的说明）
        var exitSelfTest = IsSelfTest(selfTest, "exit") || IsSelfTest(selfTest, "exit-timeout");
        ExitSelfTest.TimeoutMode = IsSelfTest(selfTest, "exit-timeout");
        // threading：公开的 IsOnUiThread / InvokeOnUiThread / PostToUiThread 的线程语义
        // （在真实消息循环里取证，见 ThreadingSelfTest 的说明）
        var threadingSelfTest = IsSelfTest(selfTest, "threading");
        // tray / tray-js：托盘 API。前者验后端（多托盘、图标对象、模板标记、菜单、显隐、移除），
        // 后者验前端（页面经 oriel.tray 调内建 tray.* 命令并回显结论）——见 TraySelfTest 的说明
        var traySelfTest = IsSelfTest(selfTest, "tray");
        var trayJsSelfTest = IsSelfTest(selfTest, "tray-js");
        // geometry：窗口尺寸语义。取证的是一个**现存缺陷**（三平台 WithSize 的语义不一致，
        // 见 docs/TAURI2-PLAN.md 的 E-1）——它要求装饰窗口，因为 frameless 下整窗 ≈ 客户区，
        // 偏差测不出来。所以这个自检**不能**调 WithFrameless()。
        // geometry-frameless：反面。修 framed 的换算时极易把 frameless 也套进去，
        // 症状是"无边框窗口比请求的大一圈"——而那正是 demo 的默认形态，必须有人看着。
        var geometrySelfTest = IsSelfTest(selfTest, "geometry");
        var geometryFramelessSelfTest = IsSelfTest(selfTest, "geometry-frameless");
        // windowevents：宿主侧几何/焦点事件（Resized / Moved / FocusChanged）。
        // 三平台的原生来源完全不同，只能逐平台真机取证——"我接上了信号"与"事件真的到了"是两件事。
        var windowEventsSelfTest = IsSelfTest(selfTest, "windowevents");
        // event：event.emit 的广播与定向投递（页面 → 宿主 → 页面）。
        // 它的主窗口要载探针页面，且能力名单必须放行 event.emit——那正是"新命令族默认不放行"的体现。
        var eventSelfTest = IsSelfTest(selfTest, "event");
        // close：关闭接管（可阻止关闭）。四条路径逐条取证，见 CloseSelfTest 的说明。
        // 它自己建探针窗口，主窗口只需正常存在。
        var closeSelfTest = IsSelfTest(selfTest, "close");
        // window：window.* 命令族的按 label 寻址 + 能力门禁。它要建第二个窗口当被操作对象。
        var windowSelfTest = IsSelfTest(selfTest, "window");
        // windownew：页面经 IPC 调 window.new 建一个真窗口——单元测试只能验到门禁与前置检查，
        // "窗口真的建出来了"只能在消息循环里取证（见 WindowNewSelfTest 的说明）。
        var windowNewSelfTest = IsSelfTest(selfTest, "windownew");
        // monitors：显示器模型（枚举 / 主屏 / 按点查 / 窗口所在屏 / 光标）。
        // 三个平台三套原生 API，单测只能验到 JSON 形状，数值必须真机取证。
        var monitorsSelfTest = IsSelfTest(selfTest, "monitors");
        // menu：页面侧菜单模型 + menu.popup 真的弹出了原生菜单。
        // 三平台的弹出路径完全不同（Win32 / GTK / AppKit），只能真跑取证。
        var menuSelfTest = IsSelfTest(selfTest, "menu");
        // menubar：setAsWindowMenu / setAsAppMenu 真的装上了菜单栏，且没弄坏客户区尺寸契约。
        // B6b 是唯一动到窗口结构的一批（Linux 的 GtkBox{menubar,webview}），几何只能真跑取证。
        var menuBarSelfTest = IsSelfTest(selfTest, "menubar");
        // api：前端包在真实页面里能不能用（ESM 加载 / 各类往返）。
        var apiSelfTest = IsSelfTest(selfTest, "api");
        // --manual-check：手动验证操作台（停在那里等人点，不自动退出）。
        // 与 --selftest shell 的分工：那个是给 CI 的无人断言，这个是给人看的——
        // 每项做成按钮，托管侧的回调（托盘菜单项、通知点击、快捷键、拖放）回显到页面。
        // 之所以要回显：demo 在 Windows 上是 WinExe，没有控制台，打印的东西看不见。
        //
        // **它是 demo 的默认页面**：直接运行 exe 看到的就是操作台。
        // 想看 Todo 示例页（"怎么用本库写应用"的示范）加 --todo；跑无人自检时也不用它，
        // 免得两种模式去争同一个托盘。
        bool anySelfTest = navSelfTest || ipcSelfTest || themeSelfTest
            || singleInstanceSelfTest || shellSelfTest || capabilitySelfTest || multiWindowSelfTest
            || schemeSelfTest || exitSelfTest || threadingSelfTest || traySelfTest || trayJsSelfTest
            || geometrySelfTest || geometryFramelessSelfTest || windowEventsSelfTest || eventSelfTest
            || closeSelfTest || windowSelfTest || windowNewSelfTest || monitorsSelfTest
            || menuSelfTest || menuBarSelfTest || apiSelfTest;
        var manualCheck = !anySelfTest && !args.Contains("--todo");

        // 主题自检需要 OrielApp（主题是应用级的），所以这里显式 Build 再 Run；
        // app 变量先声明、后赋值，闭包在 onCreated 里读它（onCreated 发生在 Run 内部，那时已赋值）。
        OrielApp? app = null;

        var builder = Oriel.CreateBuilder(args)
            .UseEmbeddedAssets(AssetHost)
            .UseJsonContext(AppJsonContext.Default)
            // 应用元信息：显式给。不显式给时也会从程序集推（产品名 / 版本 / 程序集名），
            // 但标识符会变成 "orieldemo" 这种从程序集名来的东西——而它将成为应用数据目录的
            // 组成部分（见 docs/API.md 的 app 模块一节），所以正式应用应当显式指定。
            .WithAppInfo(name: "OrielWeb 示例", identifier: "com.orielweb.demo")
            .AddCommands<TodoCommands>()
            .AddCommands<ManualCommands>()
            // 能力模型：声明页面真正会调用的命令（都写在这里的用途是当示范——Release 下
            // 不声明的话一律拒绝，见 README「能力模型」）。win.* 是库保留前缀，始终放行，不必列。
            //
            // **event.emit 必须显式声明**：它刻意不在"始终放行"的名单里（广播是真正的跨窗口能力，
            // 自绘标题栏不依赖它）。这个示范本身也是在说明"新命令族默认不放行"这条原则。
            .UseCapabilities(c =>
            {
                c.Allow("todo.*", "sys.info", "manual.*", CapabilitySelfTest.AllowedCommand);

                if (eventSelfTest)
                {
                    c.Allow("event.emit");
                }

                if (windowNewSelfTest)
                {
                    // 建窗是能力而不是元信息：不声明时页面调不动（Release 语义）。
                    c.Allow("window.new");
                }

                if (windowSelfTest || apiSelfTest)
                {
                    // **刻意逐条声明而不是 Allow("window.*")**：window 自检要同时验
                    // "放行分支"与"拒绝分支"，所以留一条（window.setResizable）不声明，
                    // 用它做拒绝路径的对照。整族放行会把那条对照抹掉。
                    //
                    // 注意这里没有 window.minimize / window.close 之类——它们在白名单里，
                    // 不声明也放行（见 OrielCapabilityRules.IsAlwaysAllowedWindowCommand）。
                    //
                    // api 自检复用这一块：它要逐模块验包能用，而 window 模块的
                    // getAllWindows / scaleFactor 需要声明（title / isVisible 在白名单里）。
                    c.Allow(
                        "window.getAll",
                        // 焦点窗口（对标 Tauri 的 getFocusedWindow）：**刻意不进白名单**——
                        // 它答的是"别的窗口是不是在前台"，属于跨窗口信息。
                        "window.getFocused",
                        "window.isVisible",
                        "window.isMaximized",
                        "window.scaleFactor",
                        "window.show",
                        "window.hide",
                        "window.setTitle",
                        "window.destroy",
                        "window.setCloseIntercept",
                        "window.setEventForwarding",
                        // 启用/禁用窗口的输入（M5-4）。**刻意不放进白名单**——
                        // 禁用窗口不是标题栏要做的事，而它让页面能把窗口变成"点不动"的状态，
                        // 那是能力而不是外观。
                        "window.setEnabled",
                        // 尺寸上限与请求注意（M5-6）。**刻意不放进白名单**——
                        // 两者都不是标题栏要做的事（标题栏不改窗口上限、也不闪任务栏）。
                        "window.setMaxSize",
                        "window.requestUserAttention",
                        // 标题栏按钮的可用性（M5-6 第三组）。同样**不进白名单**——
                        // 标题栏不需要改"能不能最大化"，那是应用自己的事。
                        "window.setMaximizable",
                        "window.setMinimizable",
                        "window.setClosable",
                        "window.setIcon",
                        // 图像模块（@orielweb/api/image）用：把文件读成字节、把字节解码成 RGBA。
                        // 两条都放进白名单：它们只碰调用方指定的那个文件，不涉及跨窗口越权。
                        "window.readImageFile",
                        "window.decodeImage",
                        "window.setBackgroundColor",
                        // 几何查询（E2）：**刻意不放进标题栏白名单**——自绘标题栏不需要它们，
                        // 而它们能按 label 读到**别的**窗口的几何。所以要用必须显式声明。
                        "window.innerSize",
                        "window.outerSize",
                        "window.innerPosition",
                        "window.outerPosition",
                        // api 自检还要做一次"读出来写回去"的不动点断言，所以也要 resize。
                        // （window.setResizable 刻意仍不声明——它做拒绝路径的对照。）
                        "window.resize");
                }

                if (apiSelfTest)
                {
                    // fs 自检（F4b-2）：**两道关都要声明**——
                    // 命令授权（Allow）+ 路径 scope（AllowPath）。
                    //
                    // 这里顺带是这个 API 的示范：scope 只给临时目录，而自检里那两条
                    // "读 C:\Windows\win.ini"与"用 .. 逃逸"的断言，验的就是它真的挡住了。
                    // 若这里改成 Allow("fs.*") 而**不给** scope，那两条会以
                    // path_not_allowed 失败——空 scope 表示不允许任何路径，不是不限制。
                    //
                    // demo 自己的命令（demo.fsRoot）也要授权：它给自检提供拼路径的根。
                    c.Allow("demo.fsRoot");

                    string fsRoot = Path.Combine(Path.GetTempPath(), "oriel-demo-selftest");
                    c.Allow(
                        "fs.*",
                        scope => scope.AllowPath(fsRoot.Replace('\\', '/') + "/**"));

                    // convertFileSrc（F2）走**独立的 scope**（`asset.read`），不共用 fs 的——
                    // 照 Tauri 的设计（那边 assetProtocol.scope 与 fs 的 scope 是两套配置）。
                    // "把图片显示出来"与"以程序的方式读写文件"是两种意图，
                    // 共用一个 scope 会让只想显示一张图的应用顺带获得读任意文本的能力。
                    c.Allow(
                        OrielAssetFileServer.ScopeCommand,
                        scope => scope.AllowPath(fsRoot.Replace('\\', '/') + "/**"));

                    // 平台集成（F5）：三族都**不是**始终放行，所以要显式声明。
                    //
                    // 自检只调只读那几条与参数校验（不真的弹浏览器、不真的写自启项），
                    // 但命令授权是在**进处理函数之前**判的——不声明的话连参数校验都到不了，
                    // 自检会拿到 not_allowed 而不是 invalid_argument。
                    c.Allow("opener.openUrl", "opener.openPath", "opener.revealItemInDir");
                    c.Allow("notification.*");
                    c.Allow("autostart.isEnabled");

                    // clipboard（F6）：读写都是能力（写能覆盖用户已复制的内容、读可能读到
                    // 密码管理器刚放进去的东西），所以要声明。
                    c.Allow("clipboard.*");

                    // dialog（F3）：会弹模态对话框并让用户选路径。三条都授权，
                    // 但自检只走"弹之前就该被拒"的那几条路径（见 ApiSelfTest 的说明）。
                    c.Allow("dialog.open", "dialog.save", "dialog.message");
                }
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
                if (eventSelfTest)
                {
                    // 事件自检要走探针页面（它订阅 probe.event 并回显自己的 label）。
                    // 用 https 别名只是为了与 manualCheck 的写法一致——导航前会归一成 oriel://。
                    w.WithUrl($"https://{AssetHost}/event-probe.html");
                }
                w.WithTitle(manualCheck ? "Oriel Demo — 手动验证"
                          : eventSelfTest ? "Oriel Demo — 事件探针"
                          : "Oriel Demo — Todo")
                    .WithSize((geometrySelfTest || geometryFramelessSelfTest) ? GeometrySelfTest.Width : 1024,
                              (geometrySelfTest || geometryFramelessSelfTest) ? GeometrySelfTest.Height : 720)
                    .WithMinSize(640, 480)
                    .Centered();

                // 装饰窗口是默认形态，但 demo 平时用无边框（页面自绘标题栏）。
                // geometry 自检**必须**用装饰窗口：frameless 下整窗 ≈ 客户区，
                // 三平台尺寸语义的偏差就测不出来——而那正是这个自检要抓的缺陷。
                // geometry-frameless 则相反，专门验"换算没被套到无边框窗口上"。
                if (!geometrySelfTest)
                {
                    w.WithFrameless();
                }
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
                if (capabilitySelfTest)
                {
                    CapabilitySelfTest.Attach(win);
                }
                if (multiWindowSelfTest)
                {
                    MultiWindowSelfTest.Attach(win);
                }
                if (schemeSelfTest)
                {
                    SchemeSelfTest.Attach(win, AssetHost);
                }
                if (exitSelfTest)
                {
                    // 与 theme 自检同理：onCreated 阶段窗口门面还没有后端（win.App 会抛），
                    // 推迟到 Loaded——那时后端已就位，且仍在 Run() 之内、退出发生之前。
                    WebviewWindow created = win;
                    created.Loaded += () =>
                    {
                        ExitSelfTest.Attach(created.App);
                        ExitSelfTest.OnLoaded(created);
                    };
                }
                if (threadingSelfTest)
                {
                    // 同上：需要 app 与已就位的窗口后端
                    WebviewWindow created = win;
                    created.Loaded += () => ThreadingSelfTest.Attach(created.App, created);
                }
                if (geometrySelfTest)
                {
                    // 窗口尺寸语义（三平台一致性）。需要页面已加载才能读 documentElement.clientWidth，
                    // 所以挂在 Loaded 上，而不是 onCreated。
                    WebviewWindow created = win;
                    created.Loaded += () => GeometrySelfTest.Attach(created.App, created);
                }
                if (geometryFramelessSelfTest)
                {
                    // 反面：frameless 窗口不该被套上"客户区 → 整窗"的换算。
                    WebviewWindow created = win;
                    created.Loaded += () => GeometrySelfTest.AttachFrameless(created.App, created);
                }
                if (windowEventsSelfTest)
                {
                    // 宿主侧几何/焦点事件。需要页面已加载（自检里会调 Resize/MoveTo/建窗抢焦点），
                    // 所以挂在 Loaded 上。
                    WebviewWindow created = win;
                    created.Loaded += () => WindowEventsSelfTest.Attach(created.App, created);
                }
                if (eventSelfTest)
                {
                    // **分两步**：订阅必须在 onCreated（早于导航），
                    // 因为探针页面在解析时就发 probe.ready，等 Loaded 再订阅会漏掉它。
                    // 而 App 要到 Loaded 才可用（窗口后端那时才 Attach 上），所以开始跑放在 Loaded。
                    EventSelfTest.AttachEvents(win);
                    win.Loaded += EventSelfTest.Begin;
                }
                if (closeSelfTest)
                {
                    // 关闭自检：它自己建探针窗口，所以等主窗口 Loaded（App 可用）再开始。
                    WebviewWindow created = win;
                    created.Loaded += () => CloseSelfTest.Begin(created.App, created);
                }
                if (windowSelfTest)
                {
                    // window.* 自检：订阅必须在 onCreated（早于导航）——页面在解析时就会
                    // 被宿主用 EvaluateJs 驱动，等 Loaded 再订阅会漏掉最早的回执。
                    // 开始跑放在 Loaded：那时 App 可用（能建第二个窗口）。
                    WindowSelfTest.AttachEvents(win);
                    WebviewWindow created = win;
                    created.Loaded += () => WindowSelfTest.Begin(created.App);
                }
                if (windowNewSelfTest)
                {
                    // 与 window 自检同一时序：订阅在 onCreated（早于导航），开始跑在 Loaded（App 可用）。
                    WindowNewSelfTest.Attach(win);
                    WebviewWindow created = win;
                    created.Loaded += () => WindowNewSelfTest.Begin(created.App, created);
                }
                if (monitorsSelfTest)
                {
                    // Loaded 而不是 onCreated：窗口要已 realize，GDK/AppKit 才答得出"它在哪块屏"。
                    WebviewWindow created = win;
                    created.Loaded += () => MonitorSelfTest.Begin(created.App, created);
                }
                if (menuSelfTest)
                {
                    // 与 windownew 同一时序：订阅在 onCreated（早于导航），开始跑在 Loaded（后端已就位）。
                    MenuSelfTest.Attach(win);
                    WebviewWindow created = win;
                    created.Loaded += () => MenuSelfTest.Begin(created.App, created);
                }
                if (menuBarSelfTest)
                {
                    MenuBarSelfTest.Attach(win);
                    WebviewWindow created = win;
                    created.Loaded += () => MenuBarSelfTest.Begin(created.App, created);
                }
                if (apiSelfTest)
                {
                    // 前端包自检：订阅同样要早于导航——探针脚本在页面解析时就会跑完并回显。
                    ApiSelfTest.AttachEvents(win);
                    WebviewWindow created = win;
                    created.Loaded += ApiSelfTest.Begin;
                }
                if (traySelfTest || trayJsSelfTest)
                {
                    // 托盘是应用级资源，要在 UI 线程上建；Loaded 时后端已就位
                    WebviewWindow created = win;
                    created.Loaded += () =>
                    {
                        if (traySelfTest)
                        {
                            TraySelfTest.RunBackend(created.App, created);
                        }
                        else
                        {
                            TraySelfTest.RunFrontend(created.App, created);
                        }
                    };
                }
            });

        if (singleInstanceSelfTest)
        {
            // 单实例配在 Run() 之前——判定发生在 Build()（装配运行时之前），
            // 于是第二实例可以在装配昂贵资源前就通过 app.IsSecondaryInstance 分流。
            builder.SingleInstance(
                SingleInstanceSelfTest.InstanceId,
                SingleInstanceSelfTest.OnActivatedBySecondInstance);
        }

        if (capabilitySelfTest)
        {
            // 两条探针命令只在自检时注册：平时它们没有用途，列在能力声明里也只是为了让
            // "允许的那条"有个真实的被调用对象（拒绝的那条刻意不在名单里）。
            builder.AddCommands<CapabilityProbeCommands>();
        }

        if (ExitSelfTest.TimeoutMode)
        {
            // 超时自检不该等默认的 10 秒：3 秒足够区分"清理卡住"与"清理很慢"，
            // 又让脚本的等待窗口短到能当回归测试跑。
            builder.UseExitTimeout(TimeSpan.FromSeconds(3));
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

        // 退出自检的收尾：正常模式在这里判定结论；超时模式在这里**故意堵死 UI 线程**，
        // 复现"消息循环已停、宿主同步清理卡住"的现场形态（见 ExitSelfTest 的说明）。
        if (exitSelfTest)
        {
            _ = ExitSelfTest.FinishAfterRun();
        }

        // 自检的结论已在运行期打印，这里只把成败映射到进程退出码（CI 的冒烟脚本据此判定）。
        if ((navSelfTest && NavSelfTest.Failed)
            || (ipcSelfTest && IpcSelfTest.Failed)
            || (themeSelfTest && ThemeSelfTest.Failed)
            || (singleInstanceSelfTest && SingleInstanceSelfTest.Failed)
            || (shellSelfTest && ShellSelfTest.Failed)
            || (capabilitySelfTest && CapabilitySelfTest.Failed)
            || (multiWindowSelfTest && MultiWindowSelfTest.Failed)
            || (schemeSelfTest && SchemeSelfTest.Failed)
            || (exitSelfTest && !ExitSelfTest.TimeoutMode && Environment.ExitCode != 0)
            || (threadingSelfTest && ThreadingSelfTest.Failed)
            || (geometrySelfTest && GeometrySelfTest.Failed)
            || (geometryFramelessSelfTest && GeometrySelfTest.Failed)
            || (windowEventsSelfTest && WindowEventsSelfTest.Failed)
            || (eventSelfTest && EventSelfTest.Failed)
            || (closeSelfTest && CloseSelfTest.Failed)
            || (windowSelfTest && WindowSelfTest.Failed)
            || (windowNewSelfTest && WindowNewSelfTest.Failed)
            || (monitorsSelfTest && MonitorSelfTest.Failed)
            || (menuSelfTest && MenuSelfTest.Failed)
            || (menuBarSelfTest && MenuBarSelfTest.Failed)
            || (apiSelfTest && ApiSelfTest.Failed)
            || ((traySelfTest || trayJsSelfTest) && TraySelfTest.Failed))
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

// 无边框窗口的 win.* 命令（最小化 / 最大化 / 关闭 / 拖动 / 全屏 / 置顶 / 选文件 / 上下文菜单）
// **由库内建**，应用不再注册——见 OrielBuiltInWindowCommands 与 API.md 第 2 节。
// 应用自定义的 win.* 会被内建遮蔽（win. 是保留前缀），所以自己那点菜单内容请用别的名字
// （例如下面 ManualCheck 里的 manual.contextMenu）。

/// <summary>DTO：STJ 源生成上下文（AOT 安全序列化的唯一入口）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoItem[]))]
[JsonSerializable(typeof(SysInfo))]
[JsonSerializable(typeof(ManualLog))]
[JsonSerializable(typeof(ManualState))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
