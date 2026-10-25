using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 一次 <c>--selftest &lt;名字&gt;</c> 运行的全部装配：能力名单、窗口配置、事件挂接、收尾判定。
/// </summary>
/// <remarks>
/// <para>
/// 这些代码原先散在 demo 的 <c>Program.cs</c> 里，占 739 行中的 420 行。搬出来的理由不是"整洁"，
/// 而是<b>归属</b>：这批自检是事实上的跨平台契约测试，却住在示例工程里——任何一次 demo 重构
/// 都可能顺手削弱唯一的语义守卫，而且改动看起来完全正常（"我把示例简化了一下"）。
/// </para>
/// <para>
/// <b>为什么是类库而不是可执行程序</b>：CI 的三条 verify 脚本各自 <c>publish</c> 一次，
/// 用同一个二进制跑完 selftest / shell / smoke。做成第二个 exe 会让每个平台多一次 AOT 发布，
/// 还要多打一个 <c>.app</c> bundle 并各自 codesign——成本翻倍，换来的只是"能单独启动"。
/// 自检代码的归属才是要解决的事，所以这里持有实现，demo 保留一个转发入口。
/// </para>
/// <para>
/// 能力名单里的每一条都带注释说明为什么它在名单里，<b>那些注释是测试意图的一部分</b>：
/// 例如"刻意不声明 window.setResizable，用它做拒绝路径的对照"——删掉那条注释的人，
/// 下一次就会顺手把 setResizable 加进去，而拒绝路径的断言仍在，只是永远不成立。
/// </para>
/// </remarks>
internal sealed class ConformanceRun
{
    /// <summary><c>--selftest</c> 认得的名字。加一个自检要同时改这里与 <see cref="Attach"/>。</summary>
    internal static readonly string[] Names =
        ["nav", "ipc", "theme", "single-instance", "shell", "capability", "multiwindow", "scheme", "exit", "exit-timeout", "threading", "tray", "tray-js", "geometry", "geometry-frameless", "windowstate", "windowevents", "event", "close", "window", "windownew", "monitors", "menu", "menubar", "api", "channel", "visuals"];

    /// <summary>主窗口。单实例自检的看门狗要在超时时关掉它。</summary>
    internal static WebviewWindow? Window;

    private readonly string? _name;

    internal ConformanceRun(string? requested)
    {
        _name = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();

        // 超时模式是静态的：ExitSelfTest 在 FinishAfterRun 里读它，而那时已经没有实例可问。
        ExitSelfTest.TimeoutMode = Is("exit-timeout");
    }

    /// <summary>本次是否在跑某个自检。false 表示这是一次普通的 demo 运行。</summary>
    internal bool Any => _name is not null;

    internal string? Name => _name;

    internal bool Is(string name)
        => _name is not null && string.Equals(_name, name, StringComparison.OrdinalIgnoreCase);

    // ---- 各自检的开关（与名字一一对应，集中在一处而不是散在 Program.cs 的 26 个局部变量里）----
    private bool Nav => Is("nav");
    private bool Ipc => Is("ipc");
    private bool Theme => Is("theme");
    private bool SingleInstance => Is("single-instance");
    private bool Shell => Is("shell");
    private bool Capability => Is("capability");
    private bool MultiWindow => Is("multiwindow");
    private bool Scheme => Is("scheme");
    private bool Exit => Is("exit") || Is("exit-timeout");
    private bool Threading => Is("threading");
    private bool Tray => Is("tray");
    private bool TrayJs => Is("tray-js");
    private bool Geometry => Is("geometry");
    private bool GeometryFrameless => Is("geometry-frameless");
    private bool WindowState => Is("windowstate");
    private bool WindowEvents => Is("windowevents");
    private bool Event => Is("event");
    private bool Close => Is("close");
    private bool WindowTest => Is("window");
    private bool WindowNew => Is("windownew");
    private bool Monitors => Is("monitors");
    private bool Menu => Is("menu");
    private bool MenuBar => Is("menubar");
    private bool Visuals => Is("visuals");
    private bool Api => Is("api");
    private bool Channel => Is("channel");

    /// <summary>托盘自检要应用级托盘：shell 与 visuals 需要（状态栏图标是观感的一部分）。</summary>
    /// <remarks>
    /// tray / tray-js 不在此列：它们用 <c>CreateTray</c> 或页面 API 自己建，不走 <c>AddTray</c>。
    /// </remarks>
    internal bool NeedsTray => Shell || Visuals;

    // ---------------------------------------------------------------------
    // 能力名单
    // ---------------------------------------------------------------------

    /// <summary>把本自检需要的命令授权加进应用的能力名单。</summary>
    internal void AllowCapabilities(OrielCapabilityOptions c)
    {
        // 能力模型的探针命令：允许的那条要有真实对象可调，拒绝的那条刻意不在名单里。
        c.Allow(CapabilitySelfTest.AllowedCommand);

        // 自检自己的命令（conf.list / conf.fsRoot）。它们只服务于自检，不构成对外 API。
        c.Allow(ConformanceCommands.Prefix);

        if (Event)
        {
            // event.emit 刻意不在"始终放行"的名单里（广播是真正的跨窗口能力）。
            c.Allow("event.emit");
        }

        if (WindowNew)
        {
            // 建窗是能力而不是元信息：不声明时页面调不动（Release 语义）。
            c.Allow("window.new");
        }

        if (Channel)
        {
            // 通道自检的命令（channel.stream / channel.streamFail）。
            // 与 menu.setAsAppMenu 同一档：只在对应自检跑的时候放行。
            c.Allow("channel.*");
        }

        if (MenuBar)
        {
            // **应用菜单栏是能力**：menu.setAsAppMenu 是 menu 族里唯一越过调用方窗口的命令
            // （套用到所有没显式设过菜单栏的窗口，含新建的），所以它刻意不在"始终放行"名单里。
            c.Allow("menu.setAsAppMenu");
        }

        if (WindowTest || Api)
        {
            AllowWindowCommands(c);
        }

        if (Api)
        {
            AllowApiCommands(c);
        }
    }

    /// <summary>window 自检与 api 自检共用的 window.* 名单。</summary>
    private static void AllowWindowCommands(OrielCapabilityOptions c)
    {
        // **刻意逐条声明而不是 Allow("window.*")**：window 自检要同时验
        // "放行分支"与"拒绝分支"，所以留一条（window.setResizable）不声明，
        // 用它做拒绝路径的对照。整族放行会把那条对照抹掉。
        //
        // 注意这里没有 window.minimize / window.close 之类——它们在白名单里，
        // 不声明也放行（见 OrielCapabilityRules.IsAlwaysAllowedWindowCommand）。
        c.Allow(
            "window.getAll",
            // 焦点窗口：**刻意不进白名单**——它答的是"别的窗口是不是在前台"，属于跨窗口信息。
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
            // 启用/禁用窗口的输入。**刻意不放进白名单**——禁用窗口不是标题栏要做的事，
            // 而它让页面能把窗口变成"点不动"的状态，那是能力而不是外观。
            "window.setEnabled",
            // 尺寸上限与请求注意。**刻意不放进白名单**——都不是标题栏要做的事。
            "window.setMaxSize",
            "window.requestUserAttention",
            // 标题栏按钮的可用性。同样**不进白名单**——标题栏不需要改"能不能最大化"。
            "window.setMaximizable",
            "window.setMinimizable",
            "window.setClosable",
            "window.setIcon",
            // 光标。同样**不进白名单**：让被注入的页面能锁死光标属于越权。
            "window.setCursorIcon",
            "window.setCursorVisible",
            "window.setCursorPosition",
            "window.setCursorGrab",
            "window.setIgnoreCursorEvents",
            // 平台开关 / 特效 / 任务栏。同样**不进白名单**：没有一条是画标题栏非要不可是的能力。
            "window.setShadow",
            "window.setVisibleOnAllWorkspaces",
            "window.setContentProtected",
            "window.setFocusable",
            "window.setSkipTaskbar",
            "window.setAlwaysOnBottom",
            "window.setTitleBarStyle",
            "window.setEffects",
            "window.clearEffects",
            "window.setProgressBar",
            "window.setOverlayIcon",
            "window.setBadgeCount",
            "window.setSimpleFullscreen",
            "window.setDecorations",
            // 刻意**不声明** window.setFullscreenOnMonitor：自检不调用它
            // （真跑会切显示器并把窗口搬到别处，在 CI 的单屏环境里没有意义），
            // 而"没声明就该被拒"本身也是一条能力面的事实。
            //
            // 图像模块用：把文件读成字节、把字节解码成 RGBA。
            // 两条都放进白名单：它们只碰调用方指定的那个文件，不涉及跨窗口越权。
            "window.readImageFile",
            "window.decodeImage",
            "window.setBackgroundColor",
            // 几何查询：**刻意不放进标题栏白名单**——自绘标题栏不需要它们，
            // 而它们能按 label 读到**别的**窗口的几何。所以要用必须显式声明。
            "window.innerSize",
            "window.outerSize",
            "window.innerPosition",
            "window.outerPosition",
            // api 自检还要做一次"读出来写回去"的不动点断言，所以也要 resize。
            // （window.setResizable 刻意仍不声明——它做拒绝路径的对照。）
            "window.resize");

        // app 级能力：window 自检的第 13 段要验 Dock 显隐与删除数据存储。
        // 这两条是能力（改应用呈现 / 删数据），不是元信息；而同批的三条只读命令
        // （getBundleType / defaultWindowIcon / fetchDataStoreIdentifiers）
        // 在 always-allowed 名单里，不必声明。
        c.Allow("app.setDockVisibility", "app.removeDataStore");
    }

    /// <summary>api 自检专属：fs scope、asset scope、平台集成、clipboard、dialog。</summary>
    private static void AllowApiCommands(OrielCapabilityOptions c)
    {
        // fs：**两道关都要声明**——命令授权（Allow）+ 路径 scope（AllowPath）。
        // 空 scope 表示不允许任何路径，不是不限制。
        string fsRoot = ConformanceCommands.FsRootPath.Replace('\\', '/');
        c.Allow(
            "fs.*",
            scope => scope.AllowPath(fsRoot + "/**"));

        // convertFileSrc 走**独立的 scope**（asset.read），不共用 fs 的——
        // "把图片显示出来"与"以程序的方式读写文件"是两种意图，
        // 共用一个 scope 会让只想显示一张图的应用顺带获得读任意文本的能力。
        c.Allow(
            OrielAssetFileServer.ScopeCommand,
            scope => scope.AllowPath(fsRoot + "/**"));

        // 平台集成：三族都**不是**始终放行，所以要显式声明。
        // 自检只调只读那几条与参数校验（不真的弹浏览器、不真的写自启项），
        // 但命令授权是在**进处理函数之前**判的——不声明的话连参数校验都到不了，
        // 自检会拿到 not_allowed 而不是 invalid_argument。
        c.Allow("opener.openUrl", "opener.openPath", "opener.revealItemInDir");
        c.Allow("notification.*");
        c.Allow("autostart.isEnabled");

        // clipboard：读写都是能力（写能覆盖用户已复制的内容、读可能读到密码管理器刚放进去的东西）。
        c.Allow("clipboard.*");

        // dialog：会弹模态对话框并让用户选路径。三条都授权，
        // 但自检只走"弹之前就该被拒"的那几条路径（见 ApiSelfTest 的说明）。
        c.Allow("dialog.open", "dialog.save", "dialog.message");
    }

    // ---------------------------------------------------------------------
    // 窗口与事件
    // ---------------------------------------------------------------------

    /// <summary>按自检调整主窗口的外观与初始页面。</summary>
    internal void ConfigureWindow(OrielWindowOptions w)
    {
        // 普通 demo 运行（没有 --selftest）时这一整套都不该生效：
        // 那是示例应用自己的窗口配置，标题、初始页面、是否无边框都由它决定。
        if (!Any)
        {
            return;
        }

        if (Ipc)
        {
            // 默认关闭（见选项说明）；自检需要它才能收到页面的 console 输出。
            w.WithConsoleForwarding();
        }

        if (Event)
        {
            // 事件自检要走探针页面（它订阅 probe.event 并回显自己的 label）。
            w.WithUrl("https://app.oriel/event-probe.html");
        }

        if (WindowState)
        {
            // **建窗形态就是被测对象**：隐藏启动 + 最大化。
            // 这正是 API.md「首帧闸门（推荐用法）」的写法加上"最大化启动"这个自然诉求，
            // Windows 后端曾在两者组合时丢掉最大化意图（见 WindowStateSelfTest 的说明）。
            // 断言全在 Loaded 之后由自检发 `Show()` 触发，这里只负责把窗口造成那个形态。
            w.WithHidden().WithMaximized();
        }

        if (Visuals)
        {
            // 观感页：**透明背景**。不透明页面会把窗口材质整个盖住，
            // 于是"材质没生效"与"页面挡住了"看起来一模一样。
            w.WithUrl("https://app.oriel/visuals.html");
        }

        // 标题必须含 "Oriel Demo"：verify-linux.sh / verify-macos.sh 的冒烟阶段
        // 用 WINDOW_TITLE_PATTERN="Oriel Demo" 枚举原生窗口，验"窗口真的建出来了"。
        // 改标题会让那条断言静默失效（枚举不到 → 判失败，但报错指向窗口创建，不指向标题）。
        w.WithTitle(Visuals ? VisualSelfTest.WindowTitle
                  : Event ? "Oriel Demo — 事件探针"
                  : "Oriel Demo — 契约自检")
            .WithSize((Geometry || GeometryFrameless) ? GeometrySelfTest.Width : 1024,
                      (Geometry || GeometryFrameless) ? GeometrySelfTest.Height : 720)
            .WithMinSize(640, 480)
            .Centered();

        // 装饰窗口是默认形态，但应用平时用无边框（页面自绘标题栏）。
        // geometry 自检**必须**用装饰窗口：frameless 下整窗 ≈ 客户区，
        // 三平台尺寸语义的偏差就测不出来——而那正是这个自检要抓的缺陷。
        // geometry-frameless 则相反，专门验"换算没被套到无边框窗口上"。
        // visuals 同理要用装饰窗口：titleBarStyle 的三档只在**有标题栏**的窗口上才有意义。
        // windowstate 也用装饰窗口：最大化的标准形态在带标题栏的窗口上，与无边框相比
        // 少一层"WS_POPUP 也支持最大化吗"的干扰。
        if (!Geometry && !Visuals && !WindowState)
        {
            w.WithFrameless();
        }
    }

    /// <summary>在 <c>onCreated</c> 里调用：订阅事件、挂 Loaded。</summary>
    internal void Attach(WebviewWindow win)
    {
        Window = win;

        // 主题/退出/线程/几何/事件/托盘这些需要 App 或已 realize 的窗口，
        // 统一推迟到 Loaded——onCreated 阶段窗口门面还没有后端（window.Attach 要等
        // CreateWindow 返回），那时 win.App 会抛异常。
        if (Theme)
        {
            win.Loaded += () => ThemeSelfTest.Attach(win.App, win);
        }

        if (Nav)
        {
            NavSelfTest.Attach(win);
        }

        if (Ipc)
        {
            IpcSelfTest.Attach(win);
        }

        if (Capability)
        {
            CapabilitySelfTest.Attach(win);
        }

        if (MultiWindow)
        {
            MultiWindowSelfTest.Attach(win);
        }

        if (Scheme)
        {
            // 主机名与 UseEmbeddedAssets 一致。库里的 AssetHost 是 internal，这里只能重复字面量；
            // SchemeSelfTest 断言的正是"来源决定能力"，主机名写错会让它整条不成立。
            SchemeSelfTest.Attach(win, "app.oriel");
        }

        if (Exit)
        {
            win.Loaded += () =>
            {
                ExitSelfTest.Attach(win.App);
                ExitSelfTest.OnLoaded(win);
            };
        }

        if (Threading)
        {
            win.Loaded += () => ThreadingSelfTest.Attach(win.App, win);
        }

        if (Geometry)
        {
            // 需要页面已加载才能读 documentElement.clientWidth，所以挂在 Loaded 上。
            win.Loaded += () => GeometrySelfTest.Attach(win.App, win);
        }

        if (GeometryFrameless)
        {
            // 反面：frameless 窗口不该被套上"客户区 → 整窗"的换算。
            win.Loaded += () => GeometrySelfTest.AttachFrameless(win.App, win);
        }

        if (WindowState)
        {
            // Loaded 时窗口仍是隐藏态（WithHidden），自检从这里开始驱动 Show/Hide/Show。
            win.Loaded += () => WindowStateSelfTest.Attach(win.App, win);
        }

        if (WindowEvents)
        {
            win.Loaded += () => WindowEventsSelfTest.Attach(win.App, win);
        }

        if (Event)
        {
            // **分两步**：订阅必须在 onCreated（早于导航），
            // 因为探针页面在解析时就发 probe.ready，等 Loaded 再订阅会漏掉它。
            EventSelfTest.AttachEvents(win);
            win.Loaded += EventSelfTest.Begin;
        }

        if (Close)
        {
            // 关闭自检：它自己建探针窗口，所以等主窗口 Loaded（App 可用）再开始。
            win.Loaded += () => CloseSelfTest.Begin(win.App, win);
        }

        if (WindowTest)
        {
            // 订阅必须在 onCreated（早于导航）——页面在解析时就会被宿主用 EvaluateJs 驱动，
            // 等 Loaded 再订阅会漏掉最早的回执。开始跑放在 Loaded：那时 App 可用。
            WindowSelfTest.AttachEvents(win);
            win.Loaded += () => WindowSelfTest.Begin(win.App);
        }

        if (WindowNew)
        {
            WindowNewSelfTest.Attach(win);
            win.Loaded += () => WindowNewSelfTest.Begin(win.App, win);
        }

        if (Monitors)
        {
            // Loaded 而不是 onCreated：窗口要已 realize，GDK/AppKit 才答得出"它在哪块屏"。
            win.Loaded += () => MonitorSelfTest.Begin(win.App, win);
        }

        if (Menu)
        {
            MenuSelfTest.Attach(win);
            win.Loaded += () => MenuSelfTest.Begin(win.App, win);
        }

        if (MenuBar)
        {
            MenuBarSelfTest.Attach(win);
            win.Loaded += () => MenuBarSelfTest.Begin(win.App, win);
        }

        if (Channel)
        {
            ChannelSelfTest.Attach(win);
            win.Loaded += () => ChannelSelfTest.Begin(win.App, win);
        }

        if (Api)
        {
            // 订阅同样要早于导航——探针脚本在页面解析时就会跑完并回显。
            ApiSelfTest.AttachEvents(win);
            win.Loaded += ApiSelfTest.Begin;
        }

        if (Tray || TrayJs)
        {
            // 托盘是应用级资源，要在 UI 线程上建；Loaded 时后端已就位。
            win.Loaded += () =>
            {
                if (Tray)
                {
                    TraySelfTest.RunBackend(win.App, win);
                }
                else
                {
                    TraySelfTest.RunFrontend(win.App, win);
                }
            };
        }

        if (Visuals)
        {
            // 观感要等窗口真的在屏幕上（Loaded），否则 AppKit 还没走完首帧，
            // 读回来的 styleMask 可能还是建窗时的默认值。
            win.Loaded += () => VisualSelfTest.Begin(win.App, win, VisualSelfTest.WindowTitle);
        }
    }

    /// <summary>托盘配置里属于自检的那部分（tooltip 与"左键弹菜单"是手动验证的语义，留在 demo）。</summary>
    internal void ConfigureTray(OrielTrayOptions o)
    {
        o.Tooltip = "OrielWeb self-test";
        // demo 的 app.png 是**彩色**图标（蓝底白窗）。macOS 的模板图语义是
        // "按 alpha 变成单色剪影、由系统反色"，彩色图套上去会丢掉全部配色、只剩一个实心块。
        o.IconAsTemplate = false;
        o.MenuOnClick = false;
    }

    // ---------------------------------------------------------------------
    // 生命周期
    // ---------------------------------------------------------------------

    /// <summary>在 <c>Build()</c> 之前调用：单实例、探针命令、退出超时。</summary>
    internal void BeforeBuild(OrielAppBuilder builder)
    {
        // 自检自己的两条命令（conf.list / conf.fsRoot）。它们是实例命令，要显式登记。
        builder.AddCommands<ConformanceCommands>();

        if (SingleInstance)
        {
            // 单实例配在 Run() 之前——判定发生在 Build()（装配运行时之前），
            // 于是第二实例可以在装配昂贵资源前就通过 app.IsSecondaryInstance 分流。
            builder.SingleInstance(
                SingleInstanceSelfTest.InstanceId,
                SingleInstanceSelfTest.OnActivatedBySecondInstance);
        }

        if (Capability)
        {
            builder.AddCommands<CapabilityProbeCommands>();
        }

        if (Channel)
        {
            builder.AddCommands<ChannelSelfTest>();
        }

        if (ExitSelfTest.TimeoutMode)
        {
            // 超时自检不该等默认的 10 秒：3 秒足够区分"清理卡住"与"清理很慢"，
            // 又让脚本的等待窗口短到能当回归测试跑。
            builder.UseExitTimeout(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>在 <c>Build()</c> 之后、<c>Run()</c> 之前调用。</summary>
    internal void AfterBuild(OrielApp app)
    {
        if (SingleInstance)
        {
            SingleInstanceSelfTest.StartWatchdog();
        }

        if (Shell)
        {
            ShellSelfTest.Start(app);
        }
    }

    /// <summary>在 <c>Run()</c> 返回之后调用。</summary>
    internal void AfterRun()
    {
        if (Exit)
        {
            // 正常模式在这里判定结论；超时模式在这里**故意堵死 UI 线程**，
            // 复现"消息循环已停、宿主同步清理卡住"的现场形态（见 ExitSelfTest 的说明）。
            _ = ExitSelfTest.FinishAfterRun();
        }
    }

    /// <summary>名字 → 失败判定。<see cref="ApplyExitCode"/> 遍历这张表。</summary>
    /// <remarks>
    /// 此前是 24 项布尔或的长链：新增自检忘挂进链条不会报错，只会**静默变绿**
    /// （2026-10-25 评审 P2 点名的结构性风险）。字典化后 <see cref="Names"/> 里的
    /// 每个名字都必须有条目——缺了启动即红，"忘了"从静默漏检变成显式失败。
    /// tray 与 tray-js 共用一张失败标志；exit 的超时模式不在这里判（见其条目）。
    /// </remarks>
    private static readonly Dictionary<string, Func<bool>> ExitFailures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nav"] = () => NavSelfTest.Failed,
        ["ipc"] = () => IpcSelfTest.Failed,
        ["theme"] = () => ThemeSelfTest.Failed,
        ["single-instance"] = () => SingleInstanceSelfTest.Failed,
        ["shell"] = () => ShellSelfTest.Failed,
        ["capability"] = () => CapabilitySelfTest.Failed,
        ["multiwindow"] = () => MultiWindowSelfTest.Failed,
        ["scheme"] = () => SchemeSelfTest.Failed,
        // exit 判的是进程退出码（ExitSelfTest 已把结论写进去）；超时模式故意堵死 UI
        // 线程，它的成败由 verify 脚本按"是否被看门狗强杀"判定，不在这里重复。
        ["exit"] = () => !ExitSelfTest.TimeoutMode && Environment.ExitCode != 0,
        ["exit-timeout"] = static () => false,
        ["threading"] = () => ThreadingSelfTest.Failed,
        ["tray"] = () => TraySelfTest.Failed,
        ["tray-js"] = () => TraySelfTest.Failed,
        ["geometry"] = () => GeometrySelfTest.Failed,
        ["geometry-frameless"] = () => GeometrySelfTest.Failed,
        ["windowstate"] = () => WindowStateSelfTest.Failed,
        ["windowevents"] = () => WindowEventsSelfTest.Failed,
        ["event"] = () => EventSelfTest.Failed,
        ["close"] = () => CloseSelfTest.Failed,
        ["window"] = () => WindowSelfTest.Failed,
        ["windownew"] = () => WindowNewSelfTest.Failed,
        ["monitors"] = () => MonitorSelfTest.Failed,
        ["menu"] = () => MenuSelfTest.Failed,
        ["menubar"] = () => MenuBarSelfTest.Failed,
        ["api"] = () => ApiSelfTest.Failed,
        ["channel"] = () => ChannelSelfTest.Failed,
        ["visuals"] = () => VisualSelfTest.Failed,
    };

    /// <summary>把自检成败映射到进程退出码（CI 的脚本据此判定，结论行由各自检打印）。</summary>
    internal void ApplyExitCode()
    {
        // 完整性断言：Names 的每个名字都必须有失败判定。这是字典化换来的防漏——
        // 长链时代忘挂一条只会静默变绿，现在缺条目在**任何一次**自检启动时就红。
        foreach (string name in Names)
        {
            if (!ExitFailures.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"自检 '{name}' 没有退出码判定（ConformanceRun.ExitFailures 缺条目）——" +
                    "宁可启动失败，也不要静默变绿。");
            }
        }

        if (_name is not null && ExitFailures[_name]())
        {
            Environment.ExitCode = 1;
        }
    }
}
