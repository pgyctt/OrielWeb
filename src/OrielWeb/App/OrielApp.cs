using System.IO.Pipes;
using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>OrielWeb 应用实例：持有平台后端、窗口列表与 IPC 分发器。</summary>
public sealed class OrielApp : IDisposable
{
    private readonly OrielAppBuilder _builder;
    private IPlatformBackend? _backend;

    /// <summary>
    /// 应用自身的元信息（名称 / 版本 / 标识符）。
    /// </summary>
    /// <remarks>
    /// 来自 <see cref="OrielAppBuilder.WithAppInfo"/>；没配时从入口程序集推断。
    /// 在 <c>Build()</c> 期间已校验过，所以读到的值一定可用。
    /// </remarks>
    public OrielAppInfo Info { get; }

    internal OrielApp(OrielAppBuilder builder)
    {
        _builder = builder;

        // 元信息在这里校验（= Build() 期间）：标识符要拼进应用数据目录，
        // 而"启动后第一次写配置"才发现它非法，故障点离原因很远。
        builder.AppInfo.Validate();
        Info = builder.AppInfo;

        Guard = new OrielIpcGuard(
            builder.Capabilities,
            OrielBuildConfiguration.IsDebugBuild(System.Reflection.Assembly.GetEntryAssembly()),
            builder.AssetHost);
        Dispatcher = new OrielCommandDispatcher(builder.TargetFactories, builder.JsonContext, Guard);

        // 单实例判定在这里（= Build() 期间）而不是 Run()：宿主往往要在 Run() 之前装配配置、数据库、
        // 设备连接，判定太晚会把第二实例的副作用先跑一遍（下游反馈 2026-10-11 的 P0-2：第二实例
        // 已经连上同一台 PLC 并向同一个库写了记录）。Run() 只剩"是第二实例 → 打印标记并退出"。
        if (builder.SingleInstanceId is { } instanceId)
        {
            switch (SingleInstanceGuard.TryAcquire(
                        instanceId,
                        SingleInstanceGuard.EncodeActivationArgs(builder.StartupArgs),
                        out FileStream? lockFile))
            {
                case SingleInstanceGuard.AcquireResult.AlreadyRunning:
                    // 激活请求（含本次启动参数）已在 TryAcquire 里发出——那个探活连接就是判定手段
                    IsSecondaryInstance = true;
                    break;

                case SingleInstanceGuard.AcquireResult.Unavailable:
                    // 锁文件创建不出来（目录只读、磁盘满、杀软/沙箱拦截……）：无法判定有没有别的实例，
                    // 倾向于**照常启动**——静默不启动（不建窗、退出码 0）比多开一个窗口难排查得多。
                    // 判定细节与理由见 SingleInstanceGuard.TryAcquire。
                    break;

                case SingleInstanceGuard.AcquireResult.Acquired:
                default:
                    // 只占住锁：接收"激活请求"的管道留到 Run() 再建（见那里的说明）
                    _singleInstanceLock = lockFile;
                    break;
            }
        }
    }

    /// <summary>
    /// 本实例是不是"第二个实例"（只在启用了 <see cref="OrielAppBuilder.SingleInstance"/> 时有意义）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Build()</c> 之后就有效，且**激活请求已经发给首实例了**——所以宿主可以据此跳过昂贵装配
    /// （打开数据库、连设备），再调 <see cref="Run"/> 让库负责"通知首实例并静默退出"。
    /// 这正是下游反馈 2026-10-11 P0-2 要的那个分流点。
    /// </para>
    /// <para>
    /// 三态里的第三态（锁不可判定，见 <c>SingleInstanceGuard.AcquireResult.Unavailable</c>）按
    /// **首实例**处理：这里返回 <c>false</c>，应用照常启动。宁可能多开一个窗口，也不要静默不启动。
    /// </para>
    /// <para>
    /// **窗口期提醒**：锁在 <c>Build()</c> 就占住，而接收"激活请求"的管道要到 <see cref="Run"/> 才建。
    /// 这段窗口里到达的第二实例会被正确判定并退出，但它的激活请求到不了首实例——宿主在 <c>Build()</c>
    /// 之后应尽快 <see cref="Run"/>（装配完成即调），不要长期悬在两者之间。
    /// </para>
    /// </remarks>
    public bool IsSecondaryInstance { get; private set; }

    internal OrielCommandDispatcher Dispatcher { get; }

    /// <summary>
    /// 入站 IPC 的门禁（来源 / 令牌 / 命令授权）。每个应用实例一个——令牌也随之每实例一份
    /// （比"每进程一个"更严，见 <see cref="OrielIpcToken"/>）。
    /// </summary>
    internal OrielIpcGuard Guard { get; }

    /// <summary>
    /// 入站 IPC 被门禁拒绝时触发（来源 / 令牌 / 命令授权三层都有）。
    /// </summary>
    /// <remarks>
    /// 用于宿主侧记账与排障：拒绝同时也会写进 <see cref="OrielDiagnostics"/>。事件在收到消息的
    /// 线程上**同步**触发（Windows 是 UI 线程，Linux/macOS 是各自的主线程），回调里不要做重活或阻塞。
    /// </remarks>
    public event Action<OrielIpcRejectedEventArgs>? IpcRejected
    {
        add => Guard.Rejected += value;
        remove => Guard.Rejected -= value;
    }

    /// <summary>
    /// 应用命令执行完成时触发（成功与失败都有；内建 <c>win.*</c> 不计入）。
    /// </summary>
    /// <remarks>
    /// 用来回答"哪条命令慢""哪条在失败"——几十条命令跑在节拍上的应用，这是唯一的宿主侧观测入口
    /// （下游反馈 2026-10-11 的 P0-3/P1-5）。<see cref="OrielCommandInvokedEventArgs.Duration"/> 是命令
    /// 执行耗时（含参数读取），不含门禁与回执投递。事件在完成命令的线程上**同步**触发，回调里别做重活。
    /// </remarks>
    public event Action<OrielCommandInvokedEventArgs>? CommandInvoked
    {
        add => Dispatcher.Invoked += value;
        remove => Dispatcher.Invoked -= value;
    }

    /// <summary>
    /// 应用命令抛异常时触发：异常**原样**带出（页面拿到的仍是净化后的文案）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CommandInvoked"/> 不重复：那个给"耗时/成功率"，这个给**异常对象本身**
    /// ——含内部路径与类型名，只给宿主。两条都会触发（失败的调用同样是一次"完成"）。
    /// </remarks>
    public event Action<OrielCommandFailedEventArgs>? CommandFailed
    {
        add => Dispatcher.Failed += value;
        remove => Dispatcher.Failed -= value;
    }

    /// <summary>内嵌资源使用的 host（页面来源即 <c>oriel://&lt;host&gt;/</c>；取自构建器 <c>UseEmbeddedAssets</c> 的 host 参数）。</summary>
    internal string AssetHost => _builder.AssetHost;

    /// <summary>「WebView2 运行时不可用」的处理回调（可能为 null，表示用库的默认提示）。</summary>
    internal Action<OrielWebView2RuntimeMissingEventArgs>? WebView2RuntimeMissingHandler
        => _builder.WebView2RuntimeMissingHandler;

    /// <summary>
    /// 应用当前**存活**的窗口（关闭的会自动摘掉）。
    /// </summary>
    public IReadOnlyList<WebviewWindow> Windows => _windows;

    private readonly List<WebviewWindow> _windows = [];

    /// <summary>
    /// 应用级菜单栏的内容（<c>Menu.setAsAppMenu</c> 设的那份），null 表示没有。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存**菜单树数据**而不是一个已建好的原生菜单：Win32 的菜单句柄一次只能挂在一个窗口上，
    /// 而应用菜单要同时作用到多个窗口（Tauri 的语义是"没显式设过菜单栏的窗口都用它"）。
    /// 每个窗口各自按这份数据建一份原生菜单，与本库"没有资源表"的整体姿态一致。
    /// </para>
    /// <para>
    /// macOS 是唯一的例外：<c>[NSApp setMainMenu:]</c> 本来就只有一份，
    /// 所以那边每个窗口都会重复设一次同一件事，幂等。
    /// </para>
    /// </remarks>
    private IReadOnlyList<OrielMenuItem>? _appMenuBarItems;

    /// <summary>
    /// 设置应用级菜单栏并套用到所有**没有显式设过菜单栏**的窗口（含此后新建的）。
    /// </summary>
    /// <param name="items">应用菜单的顶层项。</param>
    /// <remarks>
    /// "不覆盖显式设过的窗口"这条语义与 Tauri 对齐（原文 "had one set explicitly"）。
    /// 判断在 <see cref="WebviewWindow.SetAppMenuBar"/> 里做，这里只负责遍历。
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _appMenuBarItems = items;

        foreach (WebviewWindow window in _windows.ToArray())
        {
            window.SetAppMenuBar(items);
        }
    }

    /// <summary>
    /// 显示全部窗口（对标 Tauri 2 的 <c>app.show()</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **语义是"显示所有窗口"**，不是 macOS 的"取消应用隐藏"（<c>NSApp.unhide:</c>）。
    /// 三平台语义一致，且不引入平台分支；macOS 的 NSApp 语义等真有需求再补——
    /// 现在补的话，另外两个平台上它只能退化成"显示所有窗口"，于是同名方法在三个平台上
    /// 做的是两件事，而这正是本库明确避免的那类差异。
    /// </para>
    /// <para>
    /// 窗口显示是 UI 线程相关的，所以内部编组到 UI 线程（已在 UI 线程时同步执行）。
    /// 遍历用快照：<c>Show()</c> 可能触发窗口事件，事件回调里再建/关窗口会让
    /// 直接遍历 <see cref="_windows"/> 抛"集合已修改"。
    /// </para>
    /// </remarks>
    public void ShowAllWindows()
    {
        InvokeOnUiThread(() =>
        {
            foreach (WebviewWindow window in _windows.ToArray())
            {
                window.Show();
            }
        });
    }

    /// <summary>隐藏全部窗口（对标 Tauri 2 的 <c>app.hide()</c>）。理由同 <see cref="ShowAllWindows"/>。</summary>
    public void HideAllWindows()
    {
        InvokeOnUiThread(() =>
        {
            foreach (WebviewWindow window in _windows.ToArray())
            {
                window.Hide();
            }
        });
    }

    /// <summary>
    /// 按 label 取窗口（对标 Tauri 2 的 <c>Window.getByLabel</c>）。
    /// </summary>
    /// <param name="label">窗口 label（大小写不敏感，见 <see cref="OrielWindowOptions.ValidateLabel"/>）。</param>
    /// <returns>找到的窗口；不存在或已关闭时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <para>
    /// <b>返回 null 而不是抛异常</b>：与 Tauri 的 <c>getByLabel</c> 一致，也让调用方能用
    /// <c>if (app.GetWindow("settings") is { } w)</c> 这种形态表达"有就操作、没有就算了"。
    /// </para>
    /// <para>
    /// 已关闭的窗口**不会**被返回：它们在 <c>Closed</c> 时就从列表里摘掉了
    /// （见 <see cref="AttachWindow"/> 的 <c>ClosedBookkeeping</c>）。
    /// 这一点很关键——返回一个死窗口会让调用方拿到一个所有方法都抛异常的对象，
    /// 而"窗口已关闭"这个事实在异常文案里往往看不出来。
    /// </para>
    /// </remarks>
    public WebviewWindow? GetWindow(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string normalized = OrielWindowOptions.ValidateLabel(label);

        foreach (WebviewWindow window in _windows)
        {
            if (string.Equals(window.Label, normalized, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// 当前有焦点的窗口（对标 Tauri 2 的 <c>Window.getFocusedWindow</c>）。
    /// </summary>
    /// <returns>焦点窗口；没有任何窗口有焦点时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <para>
    /// 实现是**遍历存活窗口、取第一个 <see cref="WebviewWindow.IsFocused"/> 为真的**——
    /// 三个平台都能回答"这个窗口有焦点吗"（Windows 比 <c>GetForegroundWindow</c>、
    /// Linux <c>gtk_window_is_active</c>、macOS <c>isKeyWindow</c>），
    /// 而"哪个窗口有焦点"不需要单独的平台能力。
    /// </para>
    /// <para>
    /// <b>为什么遍历而不是让平台直接给答案</b>：三平台都没有"返回焦点窗口"的单次调用，
    /// 且"应用内哪个窗口有焦点"本来就是本库的记账范围——窗口集合只有 <see cref="Windows"/> 一个来源，
    /// 在这里判定不会出现"平台说的焦点窗口不在我的列表里"这种不一致。
    /// </para>
    /// <para>
    /// <b>返回 <c>null</c> 的两种情形要分开理解</b>：① 应用没有任何窗口；② 有窗口但都不在前台
    /// （用户切到了别的程序）。调用方通常不需要区分——"现在没有窗口是活动的"对两者都成立。
    /// 注意**最小化的窗口不会**被判为有焦点（它已不是 key window）。
    /// </para>
    /// <para>
    /// 与 <c>IsFocused</c> 一样按**窗口**判而不是按应用判：多窗口应用里同时只有一个 key window，
    /// 所以循环第一个命中的就是答案，不需要比较先后。
    /// </para>
    /// </remarks>
    public WebviewWindow? GetFocusedWindow()
    {
        foreach (WebviewWindow window in _windows)
        {
            if (window.IsFocused)
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>内嵌资源表（未用内嵌资源时为 null）。运行时新建的窗口也用它。</summary>
    private EmbeddedAssetStore? _assets;

    /// <summary>
    /// 运行时新建一个窗口（多窗口应用用）。
    /// </summary>
    /// <param name="configure">窗口选项，与 <see cref="OrielAppBuilder.AddWindow(Action{OrielWindowOptions}?)"/> 同一套。</param>
    /// <param name="onCreated">
    /// 建窗**之前**的回调，用来订阅 <c>Loaded</c> / <c>Closing</c> 等事件。与 builder 上那个重载一致：
    /// 事件必须在窗口后端创建前订阅好。
    /// </param>
    /// <returns>新窗口的门面对象（内容异步加载，返回时页面还没加载完）。</returns>
    /// <remarks>
    /// <para>
    /// <b>必须在 UI 线程调用</b>——命令处理、窗口事件、托盘菜单回调里都在 UI 线程上；从线程池
    /// （<c>Task.Run</c> 等）调用会抛 <see cref="InvalidOperationException"/>。
    /// 这里刻意**不做**自动 marshal：返回值是同步的窗口对象，"marshal 过去再建"会让调用方拿到一个
    /// 尚未建好的窗口，比一句明确的报错更难用。
    /// </para>
    /// <para>
    /// 新窗口不是新应用：内嵌资源目录、可信来源、IPC 令牌与能力配置都继承应用级的那些。
    /// 页面各有一份，命令与门禁是共用的——包括内建的 <c>win.*</c>：它作用到**发起调用的那个窗口**上
    /// （每个窗口后端各自把自己的窗口交给分发器）。
    /// </para>
    /// <para>
    /// 关掉其中一个窗口不会结束应用；所有窗口都关掉之后消息循环才退出
    /// （Windows 侧由存活窗口计数归零触发 <c>WM_QUIT</c>，macOS 侧由
    /// <c>applicationShouldTerminateAfterLastWindowClosed:</c> 回答，Linux 侧由 GTK 主循环自然结束）。
    /// </para>
    /// </remarks>
    public WebviewWindow CreateWindow(
        Action<OrielWindowOptions>? configure = null, Action<WebviewWindow>? onCreated = null)
    {
        if (_backend is null)
        {
            throw new InvalidOperationException(
                "应用尚未启动：CreateWindow 只能在 Run() 之后（消息循环运行期间）调用。");
        }

        if (!_backend.IsOnUiThread())
        {
            throw new InvalidOperationException(
                "CreateWindow 必须在 UI 线程调用：请在命令处理、窗口事件或托盘菜单回调里发起。");
        }

        var options = new OrielWindowOptions();
        configure?.Invoke(options);

        var window = new WebviewWindow();
        onCreated?.Invoke(window);
        AttachWindow(window, options);
        return window;
    }

    /// <summary>
    /// 建窗并挂到应用上——启动时的窗口与运行时新建的窗口走**同一条路**。
    /// </summary>
    private void AttachWindow(WebviewWindow window, OrielWindowOptions options)
    {
        // label 唯一性在这里判：这是启动期窗口与运行时新建窗口的**唯一汇合点**，
        // 判一次就覆盖两条路径。放在构建器（AddWindow）里不行——那时还没有应用实例，
        // 也就不知道"默认 main"已经被谁占用了。
        EnsureLabelAvailable(options.Label);

        if (_builder.Debug)
        {
            options.Debug = true;
        }

        var backend = _backend!.CreateWindow(window, options, this, _assets);
        window.Attach(backend);
        _windows.Add(window);

        // 每次导航成功都补推一次当前主题：否则新文档要等到用户下次切换才知道现在是深还是浅
        WebviewWindow created = window;
        created.NavigationCompleted += args =>
        {
            if (args.Success)
            {
                PushTheme(created, EffectiveTheme(created));
            }
        };

        // 上下文菜单的自定义项被点击 → 推给**页面**（事件名见 MenuItemClickedEventName）。
        //
        // 为什么在应用层订阅、而不是在 menu.popup 命令里按需订阅：订阅必须只发生一次，
        // 而 popup 每次调用都会经过——按需订阅会随弹出次数累积处理器。放在建窗处，
        // 一个窗口恰好一条，与"主题补推"同一套生命周期。
        //
        // 宿主自己弹的菜单（win.contextMenu / window.contextMenu）也会走到这里，但那些
        // 标准项只有 role、没有 id，后端不会触发 ContextMenuItemClicked——所以页面收到
        // 的一定是它自己那棵菜单的点击。
        created.ContextMenuItemClicked += id => PushMenuItemClick(created, id);

        // 拖放的四个阶段 → 推给页面（事件名见 DragEnterEventName 一组）。
        // 订阅 DragDropReceived 而不是 FileDropped：后者只有 drop，而且 drop 时两者都会触发，
        // 同时订阅会让页面收到两条 drop。
        created.DragDropReceived += args => PushDragDropEvent(created, args);

        // 新建窗口套用应用菜单（Tauri："If a window was not created with an explicit menu
        // or had one set explicitly, this menu will be assigned to it."）。
        // 走 SetAppMenuBar 而不是直接 Backend：那样会绕过"显式设过就别覆盖"的判断。
        if (_appMenuBarItems is { } appMenu)
        {
            window.SetAppMenuBar(appMenu);
        }

        // 关闭后从列表里摘掉：否则 Windows 会越用越长，而"遍历所有窗口"的应用代码
        // （例如把某个设置套用到每个窗口）会作用到已经销毁的窗口上。
        // 走 ClosedBookkeeping 而不是公共 Closed 事件：摘除必须发生在用户的 Closed 处理器**之前**，
        // 否则用户在处理器里读到的 Windows 还包含刚关掉的那个窗口。
        created.ClosedBookkeeping = () => _windows.Remove(created);
    }

    /// <summary>
    /// 判定一个 label 是否已被存活窗口占用；被占用时抛异常。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须抛而不是自动改名</b>：label 是寻址单位。自动改成 <c>main-2</c> 之后，
    /// 页面里写死的 <c>emitTo('main', …)</c> 会发到错误的窗口，而这个错误**没有任何症状**
    /// ——事件就是没到。宁可启动失败，也不要一个悄悄改名的窗口。
    /// </para>
    /// <para>
    /// 已关闭窗口的 label 会被释放（它已从 <see cref="_windows"/> 摘掉），
    /// 因此"关掉 settings 再开一个 settings"是成立的。
    /// </para>
    /// </remarks>
    private void EnsureLabelAvailable(string label)
        => EnsureLabelAvailable(label, _windows.Select(w => w.Label));

    /// <summary>
    /// 唯一性判定的纯函数形态：给定已有 label 集合，判定新 label 是否可用。
    /// </summary>
    /// <remarks>
    /// 抽出来是为了能单测——真正调用它的 <c>AttachWindow</c> 需要真实窗口后端，
    /// 而"重复 label 会不会被发现"这件事恰恰不能只在真机上验。
    /// </remarks>
    /// <param name="label">待建窗口的 label（已由 <see cref="OrielWindowOptions.ValidateLabel"/> 归一）。</param>
    /// <param name="existingLabels">已存活窗口的 label；其中 <c>null</c> 表示该窗口后端尚未就位，跳过。</param>
    /// <exception cref="InvalidOperationException">label 已被占用。</exception>
    internal static void EnsureLabelAvailable(string label, IEnumerable<string?> existingLabels)
    {
        foreach (string? existing in existingLabels)
        {
            if (existing is not null && string.Equals(existing, label, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"窗口 label「{label}」已被占用。label 是寻址单位（GetWindow / emitTo / 能力配置都按它找窗口），" +
                    "不能重复——请给每个窗口不同的 Label（见 OrielWindowOptions.Label）。" +
                    "注意默认值是 \"main\"：多窗口应用必须显式给除第一个之外的窗口设 label。");
            }
        }
    }

    /// <summary>页面侧的主题事件名：<c>oriel.on('theme.changed', theme =&gt; …)</c>，payload 为 <c>"light"</c> / <c>"dark"</c>。</summary>
    internal const string ThemeEventName = "theme.changed";

    /// <summary>当前系统主题（应用未运行、或平台检测不到时按 <see cref="OrielTheme.Light"/> 处理）。</summary>
    public OrielTheme Theme => _backend?.CurrentTheme ?? OrielTheme.Light;

    /// <summary>
    /// 覆写**应用默认**的主题（`null` = 恢复跟随系统），对标 Tauri 2 的 `App.setTheme`。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 作用域是**所有没有自己覆写的窗口**（对标 Tauri 的 `App.setTheme`：那条改的是
    /// "应用默认"，单个窗口仍可用 `Window.setTheme` 自己盖过它）。
    /// 已经经 <see cref="WebviewWindow.ThemeOverride"/> 设过覆写的窗口**不动**——它们已经
    /// 明确表态"我就要这个主题"，不该被应用级的调用搅混。
    /// </para>
    /// <para>
    /// **与 <see cref="Theme"/> 是两件事**：后者回答"系统当前是什么主题"（只读，
    /// 跟着 <c>theme.changed</c> 走），这里写的是"这个应用显示成什么主题"。
    /// 覆写期间 <see cref="Theme"/> 与 <c>theme.changed</c> 仍跟着**系统**走——
    /// 那两个回答的都是"系统现在什么样"，不该被一个应用的覆写搅混。
    /// 页面想知道"我这个窗口显示成什么"，用 `window.theme()`（`window.theme` 命令）。
    /// </para>
    /// <para>
    /// **平台落点不同（不是实现偷懒，是平台事实）**：
    /// <list type="bullet">
    ///   <item>Windows：<c>DwmSetWindowAttribute</c> 的
    ///     <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>（Win10 2004+）+ 标题栏非客户区重绘。</item>
    ///   <item>macOS：<c>NSAppearance</c>（<c>NSAppearanceNameAqua</c> /
    ///     <c>NSAppearanceNameDarkAqua</c>），作用于**应用级**——单窗口的
    ///     <c>NSWindow.appearance</c> 优先，没设时跟应用走。</item>
    ///   <item>Linux：<c>GtkSettings</c> 的 <c>gtk-application-prefer-dark-theme</c>。
    ///     这是**应用级**属性（GTK 没有"按窗口选主题"的入口），所以 Linux 上
    ///     <c>Window.setTheme</c> 与 <c>App.setTheme</c> 落到同一个地方、按调用次序
    ///     后者赢。这不是本库的缺口，是 GTK 的形状。</item>
    /// </list>
    /// </para>
    /// </remarks>
    public void SetTheme(OrielTheme? theme)
    {
        _themeOverride = theme;

        foreach (WebviewWindow window in _windows)
        {
            // 已经有自己的覆写的窗口不动（Tauri 的 `App.setTheme` 只改"默认值"）
            if (window.ThemeOverride is not null)
            {
                continue;
            }

            try
            {
                window.Backend.SetTheme(theme);
                PushTheme(window, EffectiveTheme(window));
            }
            catch (NotSupportedException)
            {
                // 该平台不支持（Linux 的 GTK 无"按窗口选主题"入口）：保持原样，
                // 但仍把当前生效值推给页面——页面至少知道自己现在是什么主题。
                PushTheme(window, EffectiveTheme(window));
            }
        }
    }

    /// <summary>
    /// 应用级主题覆写（<see cref="SetTheme"/>）。<c>null</c> = 跟随系统。
    /// </summary>
    private OrielTheme? _themeOverride;

    /// <summary>
    /// 某个窗口"当前显示成什么主题"：窗口覆写 &gt; 应用覆写 &gt; 系统主题。
    /// </summary>
    /// <remarks>
    /// 供 <c>window.theme</c> 命令按"这个窗口显示成什么"回答；与
    /// <see cref="Theme"/>（系统主题）是两件事，见 <see cref="SetTheme"/>。
    /// </remarks>
    internal OrielTheme EffectiveTheme(WebviewWindow window)
        => window.ThemeOverride ?? _themeOverride ?? Theme;

    /// <summary>
    /// 系统主题变化（用户在系统设置里切换深/浅色时触发）。
    /// 同一变化也会以 <c>theme.changed</c> 事件推给每个窗口的页面（含每次导航完成后的补推）。
    /// </summary>
    /// <remarks>
    /// **只推系统变化，不推"我这窗口现在显示成什么"**：那件事由
    /// <c>window.theme</c> 命令回答（见 <see cref="EffectiveTheme"/>）。
    /// 覆写期间这里仍跟着**系统**走——见 <see cref="SetTheme"/> 的说明。
    /// </remarks>
    public event Action<OrielTheme>? ThemeChanged;

    private void OnThemeChanged(OrielTheme theme)
    {
        ThemeChanged?.Invoke(theme);
        foreach (WebviewWindow window in _windows)
        {
            // 推的是"这窗口现在显示成什么"（含覆写），不是裸的系统值
            PushTheme(window, EffectiveTheme(window));
        }
    }

    /// <summary>把主题推给某个窗口的页面。参数已是 JSON 文本（见 <see cref="WebviewWindow.EmitEvent(string, string)"/>）。</summary>
    private static void PushTheme(WebviewWindow window, OrielTheme theme)
        => window.EmitEvent(ThemeEventName, theme == OrielTheme.Dark ? "\"dark\"" : "\"light\"");

    /// <summary>
    /// 页面侧可见的"菜单项被点击"事件名：<c>oriel.on('menu.itemclicked', ({ id }) =&gt; …)</c>。
    /// </summary>
    /// <remarks>
    /// Tauri 的菜单项用 <c>action</c> 回调直接绑定（项在 Rust 侧是资源，回调挂在它上面）；
    /// 本库没有资源表，菜单树每次 popup 都是**一次性**发给宿主的，所以回调只能靠事件回来。
    /// 事件名是我们自己的——Tauri 没有对应事件，这一点写在 <c>API.md</c> 里。
    /// </remarks>
    internal const string MenuItemClickedEventName = "menu.itemclicked";

    /// <summary>把菜单点击推给窗口的页面。</summary>
    /// <remarks>
    /// 窗口正在销毁时 <see cref="WebviewWindow.EmitEvent"/> 会抛——菜单点击是"用户动作的尾巴"，
    /// 不值得让它把原生回调链炸掉。这种情况静默丢弃，与主题补推同一姿态。
    /// </remarks>
    private static void PushMenuItemClick(WebviewWindow window, string id)
    {
        try
        {
            window.EmitEvent(MenuItemClickedEventName, $"{{\"id\":{JsonText.EncodeString(id)}}}");
        }
        catch (InvalidOperationException)
        {
            // 窗口已销毁：页面已经不在，没人接这条事件。
        }
    }

    /// <summary>
    /// 拖放四个阶段的宿主事件名（<c>drag.enter</c> / <c>drag.over</c> / <c>drag.drop</c> / <c>drag.leave</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 宿主事件名沿用本库自己的命名法（`theme.changed` / `menu.itemclicked` 同一套），
    /// 页面看到的是 Tauri 的名字 <c>tauri://drag-enter</c> 等——由 <c>event.js</c> 的
    /// <c>HOST_EVENT_MAP</c> 翻译。页面侧不必为迁移改名字，宿主侧也不必出现别的库的协议字符串。
    /// </para>
    /// <para>
    /// <b>四个是真的，不是凑齐的。</b>B8 只发 <c>drop</c>，因为那时三平台都只有"松手那一刻"
    /// 的原生回调；B10 把 Windows 换成了 COM 的 <c>IDropTarget</c>、macOS 补了
    /// <c>draggingUpdated:</c> / <c>draggingExited:</c>、GTK 补了 <c>drag-motion</c> /
    /// <c>drag-leave</c>，四个阶段才都有来源。
    /// </para>
    /// </remarks>
    internal const string DragEnterEventName = "drag.enter";

    /// <inheritdoc cref="DragEnterEventName"/>
    internal const string DragOverEventName = "drag.over";

    /// <inheritdoc cref="DragEnterEventName"/>
    internal const string DragDropEventName = "drag.drop";

    /// <inheritdoc cref="DragEnterEventName"/>
    internal const string DragLeaveEventName = "drag.leave";

    /// <summary>把拖放事件推给窗口的页面。</summary>
    /// <remarks>
    /// 与 <see cref="PushMenuItemClick"/> 同一姿态：拖放是用户动作的尾巴，
    /// 窗口正在销毁时 <see cref="WebviewWindow.EmitEvent"/> 会抛，静默丢弃即可。
    /// </remarks>
    private static void PushDragDropEvent(WebviewWindow window, OrielDragEventArgs e)
    {
        string name = e.Type switch
        {
            OrielDragEventType.Enter => DragEnterEventName,
            OrielDragEventType.Over => DragOverEventName,
            OrielDragEventType.Leave => DragLeaveEventName,
            _ => DragDropEventName,
        };

        try
        {
            window.EmitEvent(name, DragDropPayloadJson(e));
        }
        catch (InvalidOperationException)
        {
            // 窗口已销毁：页面已经不在，没人接这条事件。
        }
    }

    /// <summary>
    /// 拖放事件的 payload，形状与 Tauri 一致：<c>{type, paths?, position?}</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>over</c> 不带 <c>paths</c>、<c>leave</c> 既不带 <c>paths</c> 也不带 <c>position</c>
    /// ——Tauri 就是这么发的，页面按 <c>type</c> 收窄类型即可。
    /// </para>
    /// <para>
    /// 手写 JSON 而不是 STJ 序列化：与托盘事件同一套做法，为的是走 Native AOT 时
    /// 不需要为这个内部形状再注册一个 <c>JsonTypeInfo</c>。
    /// </para>
    /// </remarks>
    internal static string DragDropPayloadJson(OrielDragEventArgs e)
    {
        var builder = new System.Text.StringBuilder(64);
        builder.Append("{\"type\":\"");

        switch (e.Type)
        {
            case OrielDragEventType.Enter:
                builder.Append("enter\",\"paths\":[");
                AppendPaths(builder, e.Paths);
                builder.Append(']');
                break;
            case OrielDragEventType.Over:
                builder.Append("over\"");
                break;
            case OrielDragEventType.Leave:
                builder.Append("leave\"");
                // leave 没有坐标：给 0,0 会被当成"拖到了左上角"
                return builder.Append('}').ToString();
            default:
                builder.Append("drop\",\"paths\":[");
                AppendPaths(builder, e.Paths);
                builder.Append(']');
                break;
        }

        builder.Append(",\"position\":{\"x\":").Append(e.X)
               .Append(",\"y\":").Append(e.Y).Append('}');
        return builder.Append('}').ToString();
    }

    private static void AppendPaths(System.Text.StringBuilder builder, IReadOnlyList<string> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(JsonText.EncodeString(paths[i]));
        }
    }

    // ------------------------------------------------------------------
    // 显示器
    //
    // 这一组与窗口几何的单位不同：显示器是**物理像素**，窗口是**逻辑像素**。
    // 理由见 OrielMonitor 的类注释（跨屏比较只有物理像素是统一刻度）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 显示器后端。应用未运行（或测试没有注入假实现）时是空实现——查询返回空列表 /
    /// <c>null</c>，不抛异常。
    /// </summary>
    /// <remarks>
    /// 带 <c>set</c> 是有意的**测试缝**：正常路径由 <see cref="Run"/> 从平台后端取，
    /// 而命令层与宿主层都只经这个属性取数据——测试注入假后端之后，两层看到的是同一份值，
    /// 不会出现"命令测的是 A、宿主测的是 B"。
    /// </remarks>
    internal IMonitorBackend MonitorBackend { get; set; } = EmptyMonitorBackend.Instance;

    /// <summary>当前所有显示器（物理像素；空数组表示平台报不出来或应用未运行）。</summary>
    /// <remarks>
    /// 每次访问都重新枚举，因此热插拔显示器、改分辨率之后立刻可见，不需要失效/刷新逻辑。
    /// 只能在 UI 线程调用（各平台的原生枚举都有线程亲和性）。
    /// </remarks>
    public IReadOnlyList<OrielMonitor> AvailableMonitors => MonitorBackend.GetMonitors();

    /// <summary>主显示器；平台报不出来时 <c>null</c>。</summary>
    public OrielMonitor? PrimaryMonitor => MonitorBackend.GetPrimaryMonitor();

    /// <summary>包含该点（物理像素，相对主屏左上角）的显示器；不在任何屏幕上时 <c>null</c>。</summary>
    /// <param name="x">横坐标（物理像素）。</param>
    /// <param name="y">纵坐标（物理像素，向下为正）。</param>
    public OrielMonitor? GetMonitorFromPoint(double x, double y) => MonitorBackend.MonitorFromPoint(x, y);

    /// <summary>光标位置（物理像素，相对主屏左上角）。</summary>
    public OrielPosition CursorPosition => MonitorBackend.GetCursorPosition();

    /// <summary>
    /// 把动作切回 UI 线程执行（命令完成后的回执、跨线程 UI 更新都用它）。
    /// </summary>
    /// <remarks>
    /// <b>投递失败时抛 <see cref="InvalidOperationException"/></b>（应用未运行、或平台调度通道已失效）。
    /// 若你要的是"失败也不炸"的形态（例如退出途中尽力投一次），用
    /// <see cref="TryPostToMainThread"/>。
    /// </remarks>
    public void PostToMainThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.PostToMainThread(action);
    }

    /// <summary>
    /// <see cref="PostToMainThread"/> 的不抛异常版本：投递失败返回 <c>false</c>，并报一条诊断。
    /// </summary>
    /// <remarks>
    /// 存在的理由：投递失败在旧形态下是**静默丢弃**的——回调不会执行、也没有任何痕迹，
    /// 自动化验证里表现为"设置了但没生效"的假通过（下游反馈 2026-10-14 的 P2-5）。
    /// 退出途中、窗口正在销毁时的"尽力投递"应该走这个重载，而不是让异常把退出流程打断。
    /// </remarks>
    /// <returns>是否已成功投递到 UI 线程队列。</returns>
    public bool TryPostToMainThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_backend is not { } backend)
        {
            OrielDiagnostics.Report("[OrielWeb] TryPostToMainThread 失败：应用尚未运行（未调用 Run()）。");
            return false;
        }

        try
        {
            backend.PostToMainThread(action);
            return true;
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] TryPostToMainThread 投递失败，回调已被丢弃：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 当前线程是不是 UI 线程（GTK / AppKit / Win32 的窗口 API 都只能在它上面调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 应用未运行时返回 <c>false</c>。用于宿主自己的后台线程里判断"要不要编组"，
    /// 从而不必再自己维护一份主线程 ID（下游反馈 2026-10-14 的 P2-6）。
    /// </para>
    /// <para>
    /// <b>只在 UI 线程调用的库 API</b>（如 <see cref="CreateWindow"/>）会自己检查并抛
    /// <see cref="InvalidOperationException"/>；这个属性是给宿主**主动**判断用的。
    /// </para>
    /// </remarks>
    public bool IsOnUiThread => _backend?.IsOnUiThread() ?? false;

    /// <summary>
    /// 在 UI 线程上**同步**执行动作：已在 UI 线程则直接执行，否则投递过去并阻塞等待完成。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="PostToMainThread"/>（异步投递、不等待）的区别就在"等不等"：
    /// 需要拿到 UI 线程操作的结果、或需要"做完再往下走"时用它。
    /// </para>
    /// <para>
    /// <b>不要在 UI 线程上等一个正在等 UI 线程的东西</b>：本方法从 UI 线程调用时是直接执行
    /// （不会自锁），但如果你在 UI 线程上等一个后台线程、而那个后台线程正在这里等 UI 线程，
    /// 就是经典的双向等待死锁——库检测不到这种情况。
    /// </para>
    /// <para>
    /// <b>退出已经开始时立刻失败</b>：消息循环停下之后投递过来的动作永远不会被处理，
    /// 在这里干等就是永久阻塞。与其挂住调用方，不如抛一条说得清的
    /// <see cref="InvalidOperationException"/>（宿主此时应当做与 UI 无关的收尾）。
    /// 退出途中确实需要"投了就算"时用 <see cref="TryPostToMainThread"/>。
    /// </para>
    /// <para>应用未运行时抛 <see cref="InvalidOperationException"/>（与 <see cref="PostToMainThread"/> 一致）。</para>
    /// </remarks>
    public void InvokeOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        if (backend.IsOnUiThread())
        {
            action();
            return;
        }

        if (IsExiting)
        {
            throw new InvalidOperationException(
                "应用正在退出：消息循环已停止，InvokeOnUiThread 投递的动作不会被处理。" +
                "退出途中请只做与 UI 无关的收尾，或用 TryPostToMainThread 做尽力投递。");
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        if (!backend.PostToMainThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            }))
        {
            throw new InvalidOperationException("InvokeOnUiThread 失败：调度通道已失效（回调已被丢弃）。");
        }

        done.Wait();
        if (failure is not null)
        {
            // 原样抛出：编组不该改写调用方异常的类型与栈（用 ExceptionDispatchInfo 保住栈）
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>退出流程是否已经开始（<see cref="Quit"/> 已调用，或消息循环已经结束）。</summary>
    /// <remarks>
    /// 退出一旦开始，UI 线程队列就不再被排空——任何"投过去等它执行"的做法都会永久阻塞。
    /// 库内与宿主都据此避开这条路径。
    /// </remarks>
    public bool IsExiting { get { lock (_exitGate) { return _exitRequested; } } }

    /// <summary>
    /// 请求退出应用：结束消息循环、让 <see cref="Run"/> 返回，并**保证进程最终结束**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 语义：这是"最终退出"，不是"建议退出"。<see cref="Run"/> 返回后该做的清理仍会照常执行
    /// （托盘撤销、后端释放等），但库同时起一个看门狗：清理若超过 <see cref="ExitTimeout"/>
    /// （默认 10 秒）仍未走完，看门狗会强制结束进程并以 <c>2</c> 为退出码
    /// （可用 <see cref="OrielAppBuilder.UseExitTimeout"/> / <see cref="OrielAppBuilder.UseForceExitCode"/>
    /// 调整）。
    /// </para>
    /// <para>
    /// <b>为什么必须有这个兜底</b>：<c>PostQuitMessage</c> 只是往消息队列投一条 <c>WM_QUIT</c>，
    /// 它不保证进程结束。消息循环停下之后，宿主在 <see cref="Run"/> 之后跑的清理全部同步落在
    /// UI 线程上，任何一步卡住（等一个失去响应窗口的同步调用、一个不返回的 join）都会让进程驻留——
    /// 驻留期间可执行文件映像仍被映射，现场表现是"程序删不掉/装不上"，Windows 还会判成
    /// AppHangB1。把兜底做进库里，宿主就不必各写一套看门狗（写错了正好复现这个故障）。
    /// </para>
    /// <para>
    /// <b>只能在 UI 线程调用</b>：<c>PostQuitMessage</c> 是**线程相关**的——在线程池线程上调用，
    /// <c>WM_QUIT</c> 会投给一个没有消息循环的线程，主循环永远收不到，表现为"调了 Quit 却退不掉"。
    /// 从其它线程退出请用 <see cref="InvokeOnUiThread"/> 编组过来。
    /// </para>
    /// <para>
    /// 退出途中可以观测：<see cref="Exiting"/>、<see cref="ExitCompleted"/>、<see cref="ExitTimeout"/>。
    /// </para>
    /// </remarks>
    public void Quit()
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");

        if (!backend.IsOnUiThread())
        {
            // 线程相关性是硬约束，但不要静默投递——投给一个没有消息循环的线程是无效的。
            throw new InvalidOperationException(
                "Quit 必须在 UI 线程调用：PostQuitMessage / GtkMainQuit / NSApplication.terminate 都是" +
                "线程相关的，从线程池调用会投给一个没有消息循环的线程。请用 InvokeOnUiThread 编组过来。");
        }

        BeginExit();
        backend.Quit();
    }

    // ---- 退出生命周期 ----

    private readonly object _exitGate = new();
    private DateTime _exitRequestedUtc;
    private string? _exitStage;
    private bool _exitRequested;
    private bool _exitCompleted;

    /// <summary>从"请求退出"到现在的耗时（未请求退出时为零）。</summary>
    private TimeSpan ExitElapsed => _exitRequestedUtc == default
        ? TimeSpan.Zero
        : DateTime.UtcNow - _exitRequestedUtc;

    /// <summary>库观测到的最后一个退出阶段（供超时回调回答"卡在哪一步"）。</summary>
    internal string? PendingExitStage { get { lock (_exitGate) { return _exitStage; } } }

    /// <summary>退出流程已开始（消息循环即将/已经结束）。</summary>
    public event Action<OrielExitEventArgs>? Exiting;

    /// <summary>
    /// 退出清理已全部走完、<see cref="Run"/> 即将返回。
    /// </summary>
    /// <remarks>
    /// 它与 <see cref="Exiting"/> 的区别是**时序**：<see cref="Exiting"/> 在消息循环刚停下时触发
    /// （宿主的清理通常还在它之后），本事件在库的退出清理（<see cref="Dispose"/>）完成之后触发。
    /// 两个都会触发，且都**不**代表进程已结束——真正结束由 <see cref="Run"/> 返回后发生。
    /// </remarks>
    public event Action<OrielExitEventArgs>? ExitCompleted;

    /// <summary>
    /// 退出清理超过 <see cref="ExitTimeout"/>、进程即将被看门狗强制结束。
    /// </summary>
    /// <remarks>
    /// 这是"这次是被强杀的"的明确信号——没有它，宿主只能看到一个没有解释的进程消失，
    /// 或者 Windows 的 AppHangB1（下游反馈 2026-10-14 的 P1-4）。
    /// 回调在**看门狗线程**上触发，必须尽快返回；详见 <see cref="OrielExitTimeoutEventArgs"/>。
    /// </remarks>
    public event Action<OrielExitTimeoutEventArgs>? ExitTimeout;

    /// <summary>
    /// 退出清理的超时时间（<see cref="OrielAppBuilder.UseExitTimeout"/> 可覆盖）。
    /// </summary>
    /// <remarks>
    /// 名字与 <see cref="ExitTimeout"/> 事件区分开：这是**时长**，那是**超时通知**。
    /// </remarks>
    public TimeSpan ExitTimeoutDuration => _builder.ExitTimeoutValue ?? DefaultExitTimeout;

    /// <summary>默认退出超时：10 秒。够现场所有清理走完，又不会让"删不掉程序"的窗口开太久。</summary>
    internal static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>被看门狗强制结束时的退出码（<see cref="OrielAppBuilder.UseForceExitCode"/> 可覆盖）。</summary>
    public int ForceExitCode => _builder.ForceExitCodeValue ?? 2;

    /// <summary>标记退出已开始并起看门狗（幂等：重复调用只生效第一次）。</summary>
    private void BeginExit()
    {
        lock (_exitGate)
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _exitRequestedUtc = DateTime.UtcNow;
            _exitStage = "退出请求已发出";
        }

        RaiseExit(Exiting, "退出请求已发出");

        if (_builder.ExitWatchdogEnabled)
        {
            StartExitWatchdog();
        }
    }

    /// <summary>
    /// 退出看门狗：<see cref="ExitTimeout"/> 内进程还没结束就强制结束它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它覆盖的是整段退出，包括 <see cref="Run"/> 返回之后宿主的收尾</b>——所以看门狗**不会**
    /// 在库侧清理完成时被放行。报告实测的最坏情形（宿主清理 Join 2s + Wait 2s + Dispose 3s ≈ 7s）
    /// 正发生在那一段：库这时已经不能做任何事（消息循环停了，UI 线程正被宿主的同步调用占着），
    /// 唯一的出路就是超时强杀。进程正常结束时看门狗线程随进程一起消失，不需要显式收尾。
    /// </para>
    /// <para>
    /// 用后台线程：它自己不会阻止进程退出。
    /// </para>
    /// </remarks>
    private void StartExitWatchdog()
    {
        TimeSpan timeout = ExitTimeoutDuration;

        var watchdog = new Thread(() =>
        {
            // 睡满整个超时。醒来时本线程仍在执行 ⇒ 进程仍活着 ⇒ 退出确实卡住了。
            // 不用 Task.Delay/定时器：退出路径上不该再依赖线程池的可用性。
            try
            {
                Thread.Sleep(timeout);
            }
            catch (ThreadInterruptedException)
            {
                return;
            }

            HandleExitTimeout(timeout);
        })
        {
            IsBackground = true,
            Name = "OrielWeb ExitWatchdog",
        };

        watchdog.Start();
    }

    /// <summary>超时处置：先给宿主留痕的机会，再强制结束进程。</summary>
    private void HandleExitTimeout(TimeSpan timeout)
    {
        string? pending = PendingExitStage;

        OrielDiagnostics.Report(
            $"[OrielWeb] 退出清理超过 {timeout.TotalSeconds:F0} 秒仍未完成（卡在「{pending ?? "未知"}」），" +
            $"强制结束进程（退出码 {ForceExitCode}）。");

        try
        {
            ExitTimeout?.Invoke(new OrielExitTimeoutEventArgs(timeout, pending));
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] ExitTimeout 订阅者抛出异常：{ex}");
        }

        ForceExitNow();
    }

    /// <summary>
    /// 强制结束进程。**保留给退出看门狗**——它只该在"正常退出已经不可能"时被调用。
    /// </summary>
    /// <remarks>
    /// 用 <c>Environment.Exit</c> 而不是 <c>Process.Kill</c>：前者在本进程里跑终结器与
    /// <c>ProcessExit</c> 处理器（能带上最后一点清理），后者直接终止原生进程、什么都不跑。
    /// 但要注意它**不等待**前台线程，也不保证 flush 所有缓冲——所以它是兜底，不是常规路径。
    /// </remarks>
    private void ForceExitNow()
    {
        try
        {
            Environment.Exit(ForceExitCode);
        }
        catch (Exception ex)
        {
            // Environment.Exit 理论上不抛；真抛了（权限/运行时异常）也不能让看门狗线程静默死掉
            OrielDiagnostics.Report($"[OrielWeb] 强制结束进程失败：{ex}");
        }
    }

    /// <summary>标记退出清理已完成（幂等）。</summary>
    /// <remarks>
    /// 这里**不**取消看门狗：看门狗保护的是"进程最终结束"，而宿主的收尾发生在 <see cref="Run"/>
    /// 返回之后。进程正常结束时看门狗线程随进程消失，无需显式收尾。
    /// </remarks>
    private void CompleteExit(string stage)
    {
        lock (_exitGate)
        {
            if (_exitCompleted)
            {
                return;
            }

            _exitCompleted = true;
            _exitStage = stage;
        }

        RaiseExit(ExitCompleted, stage);
    }

    /// <summary>把某个退出阶段记进当前阶段（供超时回调定位），不触发事件。</summary>
    internal void RecordExitStage(string stage)
    {
        lock (_exitGate)
        {
            if (!_exitRequested)
            {
                _exitRequested = true;
                _exitRequestedUtc = DateTime.UtcNow;
            }

            _exitStage = stage;
        }
    }

    /// <summary>触发一个退出事件，逐个订阅者兜异常（退出路径不该被订阅者的异常打断）。</summary>
    private void RaiseExit(Action<OrielExitEventArgs>? handler, string stage)
    {
        if (handler is null)
        {
            return;
        }

        var args = new OrielExitEventArgs(stage, ExitElapsed);
        foreach (Action<OrielExitEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(args);
            }
            catch (Exception ex)
            {
                OrielDiagnostics.Report($"[OrielWeb] 退出事件订阅者抛出异常：{ex}");
            }
        }
    }


    // ---- 托盘与通知（应用级；详见 README 平台矩阵）----

    /// <summary>本应用的全部托盘图标（按 id 索引；对标 Tauri 2 的托盘注册表）。</summary>
    private readonly Dictionary<string, OrielTray> _trays = new(StringComparer.Ordinal);

    /// <summary>
    /// 已创建的托盘图标（只读快照）。
    /// </summary>
    /// <remarks>顺序是插入顺序（<see cref="Dictionary{TKey,TValue}"/> 的枚举顺序即此）。</remarks>
    public IReadOnlyCollection<OrielTray> Trays => _trays.Values;

    /// <summary>
    /// 主托盘（对标 Tauri 1 时代的单托盘形态）：构建期 <see cref="OrielAppBuilder.AddTray"/>
    /// 启用的那一个，或第一个创建的托盘。没有任何托盘时为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>多托盘应用请用 <see cref="GetTray"/> / <see cref="Trays"/> 按 id 取</b>——
    /// 这个属性只是为了不破坏既有单托盘代码。
    /// </remarks>
    public OrielTray? Tray => _trays.Count == 0 ? null : _trays.Values.First();

    /// <summary>按 id 取托盘；不存在时返回 null（对标 Tauri 2 的 <c>TrayIcon.getById</c>）。</summary>
    public OrielTray? GetTray(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _trays.TryGetValue(id, out OrielTray? tray) ? tray : null;
    }

    /// <summary>
    /// 运行期新建一个托盘图标（对标 Tauri 2 的 <c>TrayIcon.new</c>）。
    /// </summary>
    /// <param name="configure">创建选项（id / 图标 / 提示 / 标题 / 模板标记 / 左键弹菜单）。</param>
    /// <returns>新托盘的句柄。</returns>
    /// <remarks>
    /// <para>
    /// <b>必须在 UI 线程调用</b>（托盘是原生资源，GTK/AppKit/Win32 都只允许在主线程碰它）。
    /// </para>
    /// <para>
    /// 与构建期的 <see cref="OrielAppBuilder.AddTray"/> 是同一套选项；重复的 id 会抛
    /// （否则 <see cref="GetTray"/> 的结果就取决于创建顺序，那是静默的意外）。
    /// </para>
    /// </remarks>
    public OrielTray CreateTray(Action<OrielTrayOptions>? configure = null)
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");

        if (!backend.IsOnUiThread())
        {
            throw new InvalidOperationException(
                "CreateTray 必须在 UI 线程调用：托盘是原生资源，只能在主线程创建。");
        }

        var options = new OrielTrayOptions();
        configure?.Invoke(options);
        return AttachTray(backend, options);
    }

    /// <summary>建托盘并登记进注册表——构建期与运行期走**同一条路**。</summary>
    private OrielTray AttachTray(IPlatformBackend backend, OrielTrayOptions options)
    {
        string id = string.IsNullOrWhiteSpace(options.Id)
            ? "tray-" + Guid.NewGuid().ToString("N")[..8]
            : options.Id;

        if (_trays.ContainsKey(id))
        {
            throw new InvalidOperationException(
                $"托盘 id「{id}」已存在。多托盘请给各自不同的 Id（见 OrielTrayOptions.Id）。");
        }

        var tray = new OrielTray(backend.CreateTray(options, this, id), options);

        // 托盘事件推给**每个**窗口的页面（对标 Tauri 2 的 TrayIconOptions.action）：
        // 托盘不属于任何窗口，而页面才是订阅方，所以广播。
        // 事件已经在 UI 线程上（各平台的原生回调就在主线程），直接 EmitEvent 即可。
        tray.TrayEvent += e => BroadcastTrayEvent(e);

        // 托盘菜单项点击也走 `menu.itemclicked`（B12）：页面侧 @orielweb/api/tray 的
        // setMenu 给带 action 的项加 token 前缀，回传后按前缀认领——与 menu.js 同一套通路。
        // 不接这根线的症状是"托盘菜单点了没反应"（菜单本身能弹出来）。
        tray.MenuItemClicked += id => BroadcastTrayMenuItemClick(id);

        _trays[id] = tray;
        return tray;
    }

    /// <summary>把一次托盘事件推给所有窗口的页面（事件名 <c>tray.event</c>）。</summary>
    /// <remarks>
    /// 页面侧 <c>oriel.tray.onEvent(handler)</c> 或 <c>oriel.on('tray.event', handler)</c> 接收。
    /// payload 的形状与 <see cref="OrielTrayEvent"/> 对齐，未提供的字段写 <c>null</c>——
    /// 让页面能区分"平台不支持"与"值是 0"。
    /// </remarks>
    private void BroadcastTrayEvent(OrielTrayEvent e)
    {
        string payload = TrayEventJson(e);
        foreach (WebviewWindow window in _windows)
        {
            window.EmitEvent(TrayEventName, payload);
        }
    }

    /// <summary>页面侧接收托盘事件的名称。</summary>
    internal const string TrayEventName = "tray.event";

    /// <summary>
    /// 把托盘菜单项的点击推给所有窗口的页面（事件名与 <see cref="MenuItemClickedEventName"/> 相同）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="BroadcastTrayEvent"/> 同一个"托盘是应用级、页面才是订阅方"的理由。
    /// 页面侧 <c>@orielweb/api/tray</c> 的 <c>setMenu</c> 给带 action 的项加 token 前缀，
    /// 收到后按前缀认领；<c>menu.js</c> 的菜单栏/弹出走的也是这同一条通路。
    /// </remarks>
    private void BroadcastTrayMenuItemClick(string id)
    {
        foreach (WebviewWindow window in _windows)
        {
            PushMenuItemClick(window, id);
        }
    }

    /// <summary>把托盘事件编成 JSON（字段名与 Tauri 的 TrayIconEvent 对齐）。</summary>
    private static string TrayEventJson(OrielTrayEvent e)
    {
        var builder = new System.Text.StringBuilder(128);
        builder.Append("{\"id\":").Append(JsonText.EncodeString(e.TrayId));
        builder.Append(",\"type\":").Append(JsonText.EncodeString(TypeName(e.Type)));

        builder.Append(",\"button\":");
        builder.Append(e.Button is { } b ? JsonText.EncodeString(ButtonName(b)) : "null");

        builder.Append(",\"buttonState\":");
        builder.Append(e.ButtonState is { } s
            ? JsonText.EncodeString(s == OrielTrayButtonState.Down ? "Down" : "Up")
            : "null");

        builder.Append(",\"position\":");
        builder.Append(e.Position is { } p
            ? $"{{\"x\":{Number(p.X)},\"y\":{Number(p.Y)}}}"
            : "null");

        builder.Append(",\"rect\":");
        builder.Append(e.Rect is { } r
            ? $"{{\"position\":{{\"x\":{Number(r.Position.X)},\"y\":{Number(r.Position.Y)}}}," +
              $"\"size\":{{\"width\":{Number(r.Width)},\"height\":{Number(r.Height)}}}}}"
            : "null");

        return builder.Append('}').ToString();
    }

    /// <summary>数值 → JSON 数字字面量（必须 InvariantCulture：逗号小数点的区域会写出非法 JSON）。</summary>
    private static string Number(double value)
        => double.IsFinite(value)
            ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : "0";

    private static string TypeName(OrielTrayEventType type) => type switch
    {
        OrielTrayEventType.Click => "Click",
        OrielTrayEventType.DoubleClick => "DoubleClick",
        OrielTrayEventType.Enter => "Enter",
        OrielTrayEventType.Move => "Move",
        OrielTrayEventType.Leave => "Leave",
        _ => "Click",
    };

    private static string ButtonName(OrielTrayMouseButton button) => button switch
    {
        OrielTrayMouseButton.Left => "Left",
        OrielTrayMouseButton.Right => "Right",
        OrielTrayMouseButton.Middle => "Middle",
        _ => "Left",
    };

    /// <summary>
    /// 移除**全部**托盘图标，应用继续运行（对标 Tauri 2 的 <c>removeById</c> 的批量形态）。
    /// </summary>
    /// <returns>是否确实移除了至少一个托盘（本来就没有时返回 <c>false</c>）。</returns>
    /// <remarks>
    /// 真值在于**原生资源真的被撤销**：Windows 上"图标消失但进程还在"与"进程没了图标还留着"
    /// （幽灵图标）是完全不同的两件事，后者只有靠正确调 <c>NIM_DELETE</c> 才不会发生。
    /// </remarks>
    public bool RemoveTray() => RemoveTrayCore(_trays.Keys.ToList());

    /// <summary>
    /// 按 id 移除一个托盘图标（对标 Tauri 2 的 <c>TrayIcon.removeById</c>）。
    /// </summary>
    /// <returns>是否确实移除了（id 不存在时返回 <c>false</c>）。</returns>
    public bool RemoveTray(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RemoveTrayCore([id]);
    }

    private bool RemoveTrayCore(IReadOnlyList<string> ids)
    {
        bool removed = false;
        foreach (string id in ids)
        {
            if (_trays.Remove(id, out OrielTray? tray))
            {
                tray.Dispose();
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>
    /// 按构建期 <see cref="OrielAppBuilder.AddTray"/> 的选项重建托盘（全部）。
    /// </summary>
    /// <returns>第一个重建出来的托盘；未配置过托盘、应用未运行、或已有同 id 托盘时返回 null。</returns>
    /// <remarks>
    /// <b>重建出来的是新对象</b>：事件订阅与 <c>SetMenu</c> 都**不会**被带过去，
    /// 调用方需要重新挂上（这是不可避免的——事件处理器属于调用方）。
    /// 已有同 id 托盘时跳过它（幂等），不做替换。
    /// </remarks>
    public OrielTray? RestoreTray()
    {
        if (_backend is null)
        {
            return null;
        }

        OrielTray? first = null;
        for (int i = 0; i < _builder.TrayOptionsList.Count; i++)
        {
            OrielTrayOptions options = _builder.TrayOptionsList[i];

            // 第一个构建期托盘用固定 id "main"（既有单托盘代码靠它幂等重建）
            string id = options.Id is { Length: > 0 } explicitId
                ? explicitId
                : i == 0 ? TrayOptionsDefaultId : "tray-" + Guid.NewGuid().ToString("N")[..8];

            if (_trays.TryGetValue(id, out OrielTray? existing))
            {
                first ??= existing;
                continue;
            }

            // options.Id 为空时 AttachTray 会自己分配随机 id，这里要把决定好的 id 传下去
            options.Id = id;
            first ??= AttachTray(_backend, options);
        }

        return first;
    }

    /// <summary>构建期托盘在不显式给 Id 时使用的固定 id（让 <see cref="RestoreTray"/> 能幂等）。</summary>
    internal const string TrayOptionsDefaultId = "main";

    /// <summary>本平台是否支持系统通知（不支持时 <see cref="ShowNotification(OrielNotificationOptions)"/> 是空操作）。</summary>
    public bool NotificationsSupported => _backend?.NotificationsSupported ?? false;

    // 通知点击上报**已移除**（原 `NotificationClicked` 事件）。
    // 三个平台都拿不到点击：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID 并注册 COM 激活器
    // （打包器的职责），Linux 的 notify-send 与 macOS 的 osascript 则根本没有回调入口。
    // 原先三处都是"显式空实现"，看起来像是如实标注——但**订阅一个永不触发的事件不会编译报错**，
    // 调用方只会在运行时才发现收不到。删掉它，让"不支持"变成编译错误，这才是对的信号。
    // 详见 API.md。

    /// <summary>
    /// 后端是否已就位（即 <c>Run()</c> 已经跑起来）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给内建命令族用的：那些命令在"宿主还没跑起来"时应当**返回失败值**，
    /// 而不是让 <c>_backend ?? throw</c> 抛出一个未分类的 <c>InvalidOperationException</c>
    /// ——后者到了页面就是一句"命令执行失败"（<c>internal</c> 码），
    /// 而调用方看不出"是因为宿主还没起来"。
    /// </para>
    /// <para>
    /// 实践中页面只在 <c>Run()</c> 之后才存在，所以这条分支正常走不到；
    /// 而"正常走不到"的分支正是最需要给出可读错误的那些。
    /// </para>
    /// </remarks>
    internal bool IsRunning => _backend is not null;

    // ---------------------------------------------------------------- 系统剪贴板
    //
    // 只做纯文本。富格式（HTML）与图片的取舍写在 IPlatformBackend 的接口注释里。

    /// <summary>
    /// 读系统剪贴板里的文本；没有文本内容时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>没有后端（未 <c>Run()</c>）时返回 <c>null</c></b>，而不是抛异常——
    /// 与通知那几条一致：调用方要判断的是"有没有文本"，而"宿主没起来"对它来说
    /// 与"剪贴板里没有文本"是同一个结论。
    /// </remarks>
    public string? ReadClipboardText() => _backend?.ReadClipboardText();

    /// <summary>把文本写进系统剪贴板；失败时返回 <c>false</c>。</summary>
    /// <remarks>
    /// 失败不抛异常：剪贴板是**全局独占**资源，别的进程正开着时写不进去是正常状态，
    /// 不是调用方的错误。
    /// </remarks>
    public bool WriteClipboardText(string text) => _backend?.WriteClipboardText(text) ?? false;

    /// <summary>清空系统剪贴板；失败时返回 <c>false</c>。</summary>
    public bool ClearClipboard() => _backend?.ClearClipboard() ?? false;

    /// <summary>
    /// 发送一条系统通知。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示**已成功提交给系统**，不等于"用户看见了"——横幅显示与否还取决于系统的
    /// 通知设置与专注助手（Windows 上可在「设置 → 系统 → 通知」里按应用名关闭；
    /// macOS 上未打包运行的通知归属于 Script Editor）。失败不抛异常，返回 <c>false</c>。
    /// </returns>
    public bool ShowNotification(OrielNotificationOptions notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.ShowNotification(notification, NotificationAppId);
    }

    /// <summary>发送一条系统通知（便捷重载）；返回值语义见 <see cref="ShowNotification(OrielNotificationOptions)"/>。</summary>
    public bool ShowNotification(string title, string? body = null)
        => ShowNotification(new OrielNotificationOptions { Title = title, Body = body });

    /// <summary>
    /// 通知的"应用标识"：Windows 上即 AUMID，Linux 上是 <c>notify-send --app-name</c>。
    /// 可用 <see cref="OrielAppBuilder.UseNotificationAppId"/> 覆盖，默认取入口程序集名。
    /// </summary>
    internal string NotificationAppId => _builder.NotificationAppId ?? DefaultNotificationAppId();

    /// <summary>
    /// 默认通知标识 = 入口程序集名（规范化后）；取不到程序集时退回可执行文件名，再退回 <c>OrielWeb</c>。
    /// </summary>
    /// <remarks>
    /// 用程序集名而不是可执行文件名：<c>dotnet run</c> 下 <see cref="Environment.ProcessPath"/> 是
    /// <c>dotnet</c>，所有开发期的应用会挤进同一个标识里。程序集名在 AOT 单文件下同样可用。
    /// </remarks>
    internal static string DefaultNotificationAppId()
    {
        string? name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.ProcessPath is { } executable
                ? Path.GetFileNameWithoutExtension(executable)
                : null;
        }

        return OrielNotificationAppId.Sanitize(name);
    }

    // ---- 开机自启 ----

    /// <summary>自启标识：默认可执行文件名，可用 <see cref="OrielAppBuilder.UseAutoStartId"/> 覆盖。</summary>
    private string AutoStartId => _builder.AutoStartId ?? DefaultAutoStartId();

    /// <summary>默认自启标识 = 可执行文件名（不含扩展名）；取不到路径时退回 "OrielWeb"。</summary>
    internal static string DefaultAutoStartId()
    {
        string? executable = Environment.ProcessPath;
        return string.IsNullOrEmpty(executable)
            ? "OrielWeb"
            : Path.GetFileNameWithoutExtension(executable);
    }

    /// <summary>
    /// 本应用当前是否已设为开机自启。
    /// </summary>
    /// <remarks>
    /// 读的是**平台里实际存在的配置**，不是内存标记：用户可能自己在系统设置里关掉它
    /// （或手工删了 .desktop 文件），那时这里应当如实反映 false。
    /// </remarks>
    public bool IsAutoStartEnabled
    {
        get
        {
            var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
            return backend.IsAutoStartEnabled(AutoStartId);
        }
    }

    /// <summary>
    /// 启用开机自启（把配置写进平台的自启位置）。
    /// </summary>
    /// <param name="arguments">
    /// 随自启一起传入的命令行参数。典型用途是"开机静默启动到托盘"：<c>app.EnableAutoStart(["--minimized"])</c>。
    /// </param>
    /// <returns>是否写入成功（例如沙箱/权限受限时会失败）。</returns>
    public bool EnableAutoStart(IReadOnlyList<string>? arguments = null)
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.EnableAutoStart(AutoStartId, arguments);
    }

    /// <summary>关闭开机自启。返回是否执行成功（本来就没启用也算成功）。</summary>
    public bool DisableAutoStart()
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.DisableAutoStart(AutoStartId);
    }

    // ---- Shell（交给系统默认程序）----

    private static readonly OrielShellOptions s_defaultShellOptions = new();

    private OrielShellOptions ShellOptions => _builder.ShellOptions ?? s_defaultShellOptions;

    /// <summary>
    /// 用系统默认程序打开一个外部链接。
    /// </summary>
    /// <remarks>
    /// **只放行白名单内的 scheme**（默认 <c>http</c>/<c>https</c>/<c>mailto</c>，
    /// 见 <see cref="OrielShellOptions.AllowedSchemes"/>）：<c>file:</c>、<c>javascript:</c>、
    /// 裸路径与含控制字符的目标一律拒绝并返回 <c>false</c>。
    /// 这类 API 的风险不在"自己执行了什么"，而在"它决定让别的程序去打开什么"。
    /// </remarks>
    public bool OpenExternal(string url)
    {
        if (!OrielShellPolicy.IsAllowedUrl(url, ShellOptions.AllowedSchemes))
        {
            OrielDiagnostics.Report($"[OrielWeb] OpenExternal 拒绝了不在白名单内的目标：{url}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.OpenUrl, url);
    }

    /// <summary>
    /// 在系统文件管理器里显示一个文件：Windows 选中它、macOS 用 Finder 显示、
    /// Linux 打开它所在的目录（<c>xdg-open</c> 没有"选中"这个入口）。
    /// 路径不存在或不是绝对路径时返回 <c>false</c>，并且**不做任何调用**。
    /// </summary>
    public bool RevealInFileManager(string path)
    {
        if (!OrielShellPolicy.IsUsablePath(path))
        {
            OrielDiagnostics.Report($"[OrielWeb] RevealInFileManager 拒绝了不可用路径：{path}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.RevealPath, path);
    }

    /// <summary>用系统默认程序打开一个文件（路径必须存在且为绝对路径）。</summary>
    public bool OpenWithDefaultApp(string path)
    {
        if (!OrielShellPolicy.IsUsablePath(path))
        {
            OrielDiagnostics.Report($"[OrielWeb] OpenWithDefaultApp 拒绝了不可用路径：{path}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.OpenPath, path);
    }

    /// <summary>创建窗口、进入消息循环；阻塞直到所有窗口关闭。</summary>
    public void Run()
    {
        EnsureApartment();

        // 单实例判定已在构造期（Build()）做完——见 IsSecondaryInstance 的说明。这里只剩分流：
        // 第二实例不建窗、不进消息循环，打印契约标记后返回（退出码保持 0）。
        // 下面这行是 tools/verify-macos.sh 与 demo 自检 grep 的**契约标记**，不要改措辞。
        if (IsSecondaryInstance)
        {
            Console.WriteLine("SINGLE-INSTANCE-SECONDARY: 已有实例，已通知并退出");
            Console.Out.Flush();
            return;
        }

        // 标识符一起传下去：没显式配目录时，默认目录由它拼（%LOCALAPPDATA%\<标识符>\WebView2）。
        // 不传的话所有基于本库的应用会共用一个目录，同时打开就有一个起不来。
        _backend = PlatformBackendFactory.Create(_builder.UserDataFolder, _builder.AppInfo.Identifier);
        _backend.ThemeChanged += OnThemeChanged;

        // 显示器后端跟着平台后端就位。放在 ThemeChanged 之后、建窗之前：
        // 建窗过程里有自检/启动逻辑可能问"我在哪块屏上"，那时它必须已经可用。
        MonitorBackend = _backend.Monitors;

        // 托盘先于窗口创建：托盘是应用的外壳，先就绪才能让"启动即最小化到托盘"这类形态成立。
        // 与 RestoreTray 同一条路径，免得"启动时建"和"重建"两处各写一遍。
        _ = RestoreTray();

        // 首实例的接收管道在这里（Run）才建：Build 到 Run 之间宿主在装配自己的东西，那段窗口里没有
        // 监听者——此时到达的第二实例仍会被**正确判定**（锁在 Build 时就占住了），只是激活请求送不进来。
        // 管道建好立刻开始监听（实测过：先建管道、过一会儿再 WaitForConnection，会让第二实例的
        // 连接请求一直等在那里，直到超时——所以两者必须紧挨着）。
        if (_singleInstanceLock is not null && _builder.SingleInstanceId is { } primaryId)
        {
            _singleInstanceServer = SingleInstanceGuard.CreateListener(primaryId);
            StartSingleInstanceListener();
        }

        // 只建表，不动磁盘：三平台都由 scheme 处理器按需从程序集取流应答（见 EmbeddedAssetStore）。
        // 这一步同时保留了"一个资源都没有就报配置错误"的行为（不是静默白屏）。
        // 可信来源前缀不在这里补：oriel://<host>/ 由 OrielIpcGuard 的构造统一登记，
        // 与 host 同源，少一处手拼路径的机会。
        _assets = _builder.UseAssets
            ? EmbeddedAssetStore.Create(_builder.AssetResourcePrefix)
            : null;

        _windows.EnsureCapacity(_builder.PendingWindows.Count);
        foreach (var (window, options) in _builder.PendingWindows)
        {
            AttachWindow(window, options);
        }

        _backend.RunMessageLoop();

        // 消息循环结束 = 应用正在退出。若退出不是经 Quit 发起的（例如所有窗口自然关闭、
        // 或平台侧别的路径落出循环），这里补记起点并起看门狗——否则退出事件漏报，
        // 宿主在 Run() 之后的收尾也失去"卡住就强杀"的保护。
        BeginExitIfNotStarted();

        // 消息循环结束 = 应用正在退出：这里主动释放一次。
        // 托盘图标尤其关键——它是**系统级**资源，只属于本进程却由 explorer 持有：
        // 不调 NIM_DELETE 撤销的话，进程没了图标还留在通知区（"幽灵图标"），
        // 用户只能把鼠标划过去等它自己消失。以前的 Run() 直接返回，于是每次退出都留一个。
        // Dispose 幂等，调用方之后再 Dispose 一次也无妨。
        try
        {
            RecordExitStage("正在释放托盘与后端");
            Dispose();
            RecordExitStage("库侧清理完成");
        }
        catch (Exception ex)
        {
            // 退出路径不该因为清理失败而崩：报出来，让进程照常结束
            OrielDiagnostics.Report($"[OrielWeb] 退出清理时抛出异常：{ex}");
        }

        // 库侧清理走完：通知宿主。注意这**不等于进程已结束**——宿主在 Run() 返回后还有自己的
        // 收尾，那段时间仍受看门狗保护（超时一样会强杀），这正是"Quit 保证退出"的兜底所在。
        CompleteExit("库侧退出清理完成，Run() 即将返回");
    }

    /// <summary>退出尚未开始时补一次 <see cref="BeginExit"/>（自然退出路径：所有窗口关闭、平台落出循环）。</summary>
    private void BeginExitIfNotStarted()
    {
        lock (_exitGate)
        {
            if (_exitRequested)
            {
                // 已经走过 Quit：只更新阶段，不重复触发事件、不重复起看门狗
                _exitStage = "消息循环已结束";
                return;
            }
        }

        RecordExitStage("消息循环已结束");
        RaiseExit(Exiting, "消息循环已结束");

        if (_builder.ExitWatchdogEnabled)
        {
            StartExitWatchdog();
        }
    }

    /// <summary>
    /// Windows 要求主线程为 STA（WebView2 的 COM 初始化与 UI 消息循环依赖它）。
    /// Unix 平台的 <see cref="Thread.GetApartmentState"/> 恒返回 <see cref="ApartmentState.Unknown"/>，
    /// <c>[STAThread]</c> 特性在 Linux/macOS 上也被忽略，因此在非 Windows 平台必须跳过该检查。
    /// </summary>
    internal static void EnsureApartment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "OrielWeb 在 Windows 上要求主线程为 STA 线程：请在 Main 方法上标注 [STAThread]。" +
                "（WebView2 的 COM 初始化与 UI 消息循环依赖 STA）");
        }
    }

    // ---- 单实例 ----

    private NamedPipeServerStream? _singleInstanceServer;
    private FileStream? _singleInstanceLock;

    /// <summary>
    /// 首实例侧：后台等后续实例的连接，收到后在 UI 线程激活窗口，并把第二实例的启动参数交给回调。
    /// </summary>
    private void StartSingleInstanceListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SingleInstanceGuard
                    .ListenAsync(
                        _singleInstanceServer!,
                        payload => PostToMainThread(
                            () => ActivateAllWindows(SingleInstanceGuard.DecodeActivationArgs(payload))))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 监听结束（例如首实例正在退出）不算错误，记一笔即可
                OrielDiagnostics.Report($"[OrielWeb] 单实例监听结束：{ex.Message}");
            }
        });
    }

    /// <summary>把窗口前置并激活，随后把"第二实例的启动参数"交给应用的回调（若有）。</summary>
    private void ActivateAllWindows(IReadOnlyList<string> secondaryArgs)
    {
        foreach (WebviewWindow window in _windows)
        {
            window.Show();
            window.Focus();
        }

        _builder.SingleInstanceActivateHandler?.Invoke(
            _windows.Count > 0 ? _windows[0] : null, secondaryArgs);
    }

    public void Dispose()
    {
        _singleInstanceServer?.Dispose();
        _singleInstanceLock?.Dispose();
        // 托盘先于后端释放：它的原生资源要靠后端所在的消息循环/主线程清理。
        // 全部托盘都要撤销——Windows 上漏掉一个就是通知区里一个幽灵图标。
        foreach (OrielTray tray in _trays.Values)
        {
            tray.Dispose();
        }

        _trays.Clear();
        _backend?.Dispose();
    }
}
