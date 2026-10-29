using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionDevice;
using DCompPoint = OrielWeb.Platform.Windows.WebView2.Interop.WV2POINT;
using DCompTarget = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionTarget;
using DCompVisual = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 单个窗口的 Win32 + WebView2 宿主：窗口创建、WndProc、WebView2 装配、
/// 事件回抛、IPC 回执线程切换与对话框。
/// </summary>
/// <remarks>
/// WebView2 互操作走 <c>WebView2Aot</c> 的源生成绑定：<c>[GeneratedComInterface]</c>（RCW）
/// 与包内事件处理器提供的 <c>[GeneratedComClass]</c>（CCW）。
/// 因此本文件不含任何手工 vtable、IID 表、RefCount 或 QueryInterface 代码；
/// 版本化接口成员（如 <c>SetVirtualHostNameToFolderMapping</c>）在包装层直达。
/// </remarks>
internal partial class Win32WindowHost : IWindowBackend
{
    private const string WindowClassName = "OrielWeb_Window";
    internal const string WebView2UserDataFolderName = "WebView2";

    private static int s_windowClassRegistered;
    private static readonly object s_windowClassGate = new();
    private static readonly nint s_windowClassNamePtr = Marshal.StringToHGlobalUni(WindowClassName);

    private static int s_loaderInitialized;

    internal readonly WebviewWindow _window;
    internal readonly OrielWindowOptions _options;
    internal readonly OrielApp _app;
    internal readonly WindowsPlatformBackend _backend;
    internal readonly EmbeddedAssetStore? _assets;
    internal readonly string _assetHost;

    /// <summary>oriel:// 的请求拦截器（见 <see cref="Win32AssetScheme.Attach"/>）。必须持有引用。</summary>
    private CoreWebView2WebResourceRequestedEventHandler? _assetSchemeHandler;

    private GCHandle _selfHandle;

    /// <summary>窗口是否已交付给应用（<see cref="Create"/> 成功返回时才置位）。</summary>
    /// <remarks>
    /// 创建期失败时 <see cref="Create"/> 也会 <c>DestroyWindow</c> 收尾，那条路径不能触发
    /// 面向应用的副作用：后端的 <c>_aliveWindows</c> 是在 <c>Create</c> **成功之后**才自增的
    /// （见 WindowsPlatformBackend.CreateWindow），这里若照常自减会把计数打成负数，进而误发
    /// <c>PostQuitMessage</c>；`Closed` 也不该为"应用从没见过的窗口"触发。见评审 2026-10-08 发现 1。
    /// </remarks>
    private bool _handedOff;

    /// <summary>
    /// "隐藏启动 + 最大化"时挂起的最大化意图，由 <see cref="Show"/> 兑现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不在创建期直接建立最大化状态：<c>ShowWindow(SW_SHOWMAXIMIZED)</c> 会先把窗口
    /// map 到屏幕上，<c>WithHidden()</c> 的契约是"从没露过面"；换成
    /// <c>SetWindowPlacement(showCmd = SW_SHOWMAXIMIZED)</c> 也一样——实测调用返回后
    /// <c>IsWindowVisible</c> 与 <c>IsZoomed</c> 立刻都是 true，不是只写一条"下次显示时用"的记录。
    /// </para>
    /// <para>
    /// 所以这里只记意图，等 <see cref="Show"/> 用 <c>SW_SHOWMAXIMIZED</c> 一次到位：
    /// 窗口从"从未显示"直接以最大化出现，既没有 map→unmap 的可见帧，也没有"先还原再撑满"的跳变。
    /// </para>
    /// </remarks>
    private bool _maximizeOnShow;

    internal nint _hwnd;

    private IComObject<ICoreWebView2Environment>? _environment;
    private IComObject<ICoreWebView2CompositionController>? _compositionController;

    /// <summary>同一组合控制器的 <c>ICoreWebView2Controller</c> 视图（bounds / visible / focus 用）。</summary>
    private ICoreWebView2Controller? _controller;

    /// <summary>运行期设过的窗口底色（<c>setBackgroundColor</c>）；<c>null</c> 表示没设过，用于回落创建期选项。</summary>
    private uint? _runtimeBackgroundArgb;
    private IComObject<ICoreWebView2>? _webView;
    private CoreWebView2Events? _webViewEvents;
    private CoreWebView2CompositionControllerEvents? _compositionEvents;

    /// <summary>
    /// OLE 拖放目标（提供 enter / over / leave / drop）。
    /// </summary>
    /// <remarks>
    /// 为 null 表示注册失败（窗口上已被别人占用、或 OLE 没初始化）——那时退回
    /// <c>WM_DROPFILES</c>，只有 <c>drop</c> 但至少能用。
    /// </remarks>
    private Win32DropTarget? _dropTarget;

    // DirectComposition 合成树。窗口用 WS_EX_NOREDIRECTIONBITMAP 创建，没有系统绘制的表面，
    // 内容完全由这棵树提供；WebView2 作为其中一个视觉（RootVisualTarget），因此它不再是子窗口。
    // 用 IComObject 持有而不是裸接口：设备/目标/视觉的生命周期与窗口严格绑定，
    // 裸接口会被 GC 的时机决定释放点，DComp 上表现为随机的"提交后不可见"。
    private IComObject<DCompDevice>? _dcompDevice;
    private IComObject<DCompTarget>? _dcompTarget;
    private IComObject<DCompVisual>? _dcompVisual;

    private bool _trackingMouseLeave;

    /// <summary>最近一次转发给 WebView 的客户区落点；<see cref="ForwardMouseLeave"/> 用它补发 LEAVE。</summary>
    private DCompPoint _lastMousePoint;
    private bool _hasLastMousePoint;

    private volatile bool _loadedRaised;

    private int _minWidth;
    private int _minHeight;

    /// <summary>最大尺寸（逻辑像素，0 = 不限制）。与 <c>_minWidth</c> / <c>_minHeight</c> 配对。</summary>
    private int _maxWidth;
    private int _maxHeight;

    private bool _isFullscreen;
    private bool _isOnTop;

    /// <summary>
    /// 窗口的**可见性意图**：最后一次 <see cref="Show"/> / <see cref="Hide"/>（或构造时的
    /// <see cref="OrielWindowOptions.Hidden"/>）要求的状态。
    /// </summary>
    /// <remarks>
    /// 单独记一份意图、而不是每次回读窗口样式，是因为 WebView2 的组合控制器**晚于**窗口创建：
    /// <c>WithHidden()</c> 起手时窗口在 <see cref="PostCreate"/> 就 <c>SW_HIDE</c> 了，而控制器要到
    /// <see cref="InitializeWebView2Async"/> 才存在——那次装配必须按意图恢复可见性，否则会把
    /// 一个隐藏启动的窗口硬置成可见（见那里的说明）。见 <see cref="Hide"/> 的跨平台不变量。
    /// </remarks>
    private bool _isVisible;

    private WINDOWPLACEMENT _savedPlacement;
    private nint _savedStyle;
    private string _title;

    /// <summary>
    /// 是否由 API（<see cref="SetTitle"/>）显式设过标题。
    /// </summary>
    /// <remarks>
    /// 置位后不再让页面 <c>&lt;title&gt;</c> 覆盖窗口标题——显式设置优先于自动跟随。
    /// 三平台同一语义（否则"页面标题何时能覆盖"会变成平台相关的隐式规则）。
    /// </remarks>
    private bool _titleSetExplicitly;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;
    private event Action<bool>? MaximizedChanged;
    private event Action<OrielSizeChangedEventArgs>? Resized;
    private event Action<OrielPositionChangedEventArgs>? Moved;
    private event Action<bool>? FocusChanged;
    private event Action<double>? ScaleFactorChanged;
    private event Action<string>? NavigationStarting;
    private event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted;
    private event Action<OrielConsoleMessageEventArgs>? ConsoleMessage;
    private event Action<OrielMessageReceivedEventArgs>? MessageReceived;

    // 上一次已知的最大化状态，用于在 WM_SIZE 里识别变化（用户在原生路径下最大化/还原时，
    // 页面无从得知，必须由宿主推送）
    private bool _wasMaximized;

    // 上一次已上报的客户区尺寸 / 窗口位置（逻辑像素）。
    // WM_SIZE 与 WM_MOVE 在拖动时**每个像素都会来一次**，不去重就是把每秒几百次原生回调
    // 变成同样数量的托管事件 + 页面 IPC——那是自伤。null 表示"还没报过"。
    private (double Width, double Height)? _lastReportedSize;
    private (double X, double Y)? _lastReportedPosition;

    // 上一次已上报的焦点状态；null 表示"还没报过"（首个 WM_SETFOCUS 必须报，哪怕值相同）
    private bool? _lastReportedFocus;

    // 上一次已上报的缩放比；null 表示"还没报过"。
    // 浮点比较用容差而不是 ==：DPI 是整数算出来的比值（如 144/96），但中途经过除法，
    // 拿 == 比会在"其实没变"时多报一次事件。
    private double? _lastReportedScaleFactor;

    // 强制销毁中：Destroy() 置它，用来让关闭路径上的判断看出"这次不受拦"。
    private bool _forceDestroy;

    // 已为"通知 WebView2 父窗口位置"失败报过一次诊断。WM_MOVE 在拖动时每帧都来，
    // 不设这个开关会把诊断通道刷满（见 NotifyWebViewParentMoved）。
    private bool _parentMoveNotifyFailed;

    // 最近一次 NavigationStarting 的 URL。WebView2 的 NavigationCompleted 参数里没有 URL
    // （它只有 IsSuccess / WebErrorStatus / NavigationId），而事件参数要带上 URL，故在开始时缓存。
    private string _lastNavigationUri = string.Empty;

    protected Win32WindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, WindowsPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app; _backend = backend;
        _assets = assets;
        // 取自构建器 UseEmbeddedAssets(host)：此前硬编码 "app.oriel" 会使用户自定义 host 失效
        // （scheme 的 authority 与导航 URL 用的不是同一个 host → 白屏/404）
        _assetHost = app.AssetHost;

        // 自定义 scheme 必须随**环境**创建登记，而环境是进程级共享、只建一次：
        // 所以这里（建窗之前、第一个窗口构造时）登记，晚于此处的调用只会命中已存在的选项。
        if (_assets is not null)
        {
            Win32AssetScheme.Prepare(_assetHost);
        }
        // 策略是可写属性（运行时能改），所以把 options 里的初值取出来存进属性，而不是每次回头读 options
        ContextMenuPolicy = options.ContextMenuPolicy;
        CloseInterceptEnabled = options.InterceptClose;
        _title = options.Title;
        // 可见性意图的初值：WithHidden() 起手时窗口不上屏（PostCreate 据此 SW_HIDE），
        // 且 WebView2 装配时也要按这个初值恢复（不能硬置 true，见 InitializeWebView2Async）。
        _isVisible = !options.Hidden;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        // 运行期可改（SetDecorations），但初值来自创建选项——所以记一份而不是每次读 options
        _decorations = !options.Frameless;
    }

    /// <summary>句柄是否还在（窗口销毁后清零）。迟到的后端调用据此直接放弃。</summary>
    private bool WindowAlive => _hwnd != 0;

    public nint NativeWindowHandle => _hwnd;

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }
    event Action<bool>? IWindowBackend.MaximizedChanged { add => MaximizedChanged += value; remove => MaximizedChanged -= value; }
    event Action<OrielSizeChangedEventArgs>? IWindowBackend.Resized { add => Resized += value; remove => Resized -= value; }
    event Action<OrielPositionChangedEventArgs>? IWindowBackend.Moved { add => Moved += value; remove => Moved -= value; }
    event Action<bool>? IWindowBackend.FocusChanged { add => FocusChanged += value; remove => FocusChanged -= value; }

    event Action<double>? IWindowBackend.ScaleFactorChanged
    {
        add => ScaleFactorChanged += value;
        remove => ScaleFactorChanged -= value;
    }
    event Action<string>? IWindowBackend.NavigationStarting { add => NavigationStarting += value; remove => NavigationStarting -= value; }
    event Action<OrielNavigationCompletedEventArgs>? IWindowBackend.NavigationCompleted { add => NavigationCompleted += value; remove => NavigationCompleted -= value; }
    event Action<OrielConsoleMessageEventArgs>? IWindowBackend.ConsoleMessage { add => ConsoleMessage += value; remove => ConsoleMessage -= value; }
    event Action<OrielMessageReceivedEventArgs>? IWindowBackend.MessageReceived { add => MessageReceived += value; remove => MessageReceived -= value; }
    event Action<string>? IWindowBackend.ContextMenuItemClicked { add => ContextMenuItemClicked += value; remove => ContextMenuItemClicked -= value; }
    event Action<OrielFileDropEventArgs>? IWindowBackend.FileDropped { add => FileDropped += value; remove => FileDropped -= value; }
    event Action<OrielDragEventArgs>? IWindowBackend.DragDrop { add => DragDrop += value; remove => DragDrop -= value; }

    public bool IsMaximized => Win32.IsZoomed(_hwnd);

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

    // ------------------------------------------------------------------
    // 上下文菜单
    // ------------------------------------------------------------------

    private event Action<string>? ContextMenuItemClicked;
    private event Action<OrielFileDropEventArgs>? FileDropped;
    private event Action<OrielDragEventArgs>? DragDrop;

    /// <summary>窗口销毁时的清理：先关控制器、释放合成树，再触发 Closed 与窗口计数。</summary>
    internal void OnWindowDestroyedCore()
    {
        // 拖放目标要先撤：它在 hwnd 上注册，窗口一销毁 RevokeDragDrop 就无从谈起，
        // 而 OLE 之后还可能往这个已死的窗口投递回调。
        var dropTarget = _dropTarget;
        _dropTarget = null;
        dropTarget?.Dispose();

        // 菜单栏要先摘再放（顺序见 DetachMenuBar）。放在最前：后面的清理可能抛，
        // 而菜单栏是挂在 hwnd 上的——hwnd 即将销毁，再晚就摘不下来了。
        DetachMenuBar();

        _compositionEvents?.Dispose();
        _compositionEvents = null;

        _webViewEvents?.Dispose();
        _webViewEvents = null;

        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            try
            {
                controller.Close(); // 先通知控制器关闭，再释放其余资源
            }
            catch (Exception ex)
            {
                OrielDiagnostics.Report($"[OrielWeb] 关闭 WebView2 控制器失败：{ex.Message}");
            }
        }

        Interlocked.Exchange(ref _compositionController, null)?.Dispose();
        Interlocked.Exchange(ref _webView, null)?.Dispose();
        // **不能**释放环境：它是全进程共享的（由 WindowsPlatformBackend 持有并释放），
        // 本窗口销毁后，同进程里其它窗口还要用它。
        Interlocked.Exchange(ref _environment, null);

        // DirectComposition 的对象本是窗口的合成树，HWND 销毁后即失去作用，
        // 随进程/窗口生命周期自然回收（本项目一个进程通常只活一个窗口）。
        _dcompVisual = null;
        _dcompTarget = null;
        _dcompDevice = null;

        // 任务栏覆盖图标是本对象持有的 HICON（B22）：窗口没了就没人再认它，必须自己销毁。
        // 任务栏那边拷贝过一份，销毁这里不影响它已经画出来的东西；而窗口都销毁了，
        // 那个按钮也不在了。
        if (_overlayIcon != 0)
        {
            _ = Win32.DestroyIcon(_overlayIcon);
            _overlayIcon = 0;
        }

        // 只有已在 Create 里交付给应用的窗口才触发这两件事：创建期失败的清理路径也走这里
        // （DestroyWindow → WM_DESTROY），但那时后端计数还没自增、应用也没见过这个窗口。
        if (_handedOff)
        {
            Closed?.Invoke();
            _backend.OnWindowDestroyed();
        }

        // GWLP_USERDATA 指向的根引用随下面这句失效：不清零的话，紧随 WM_DESTROY 的
        // WM_NCDESTROY 会让 WindowProc 去 GCHandle.FromIntPtr 一个已释放的句柄、抛异常，
        // 再被它自己的 catch 吞掉——那条 catch 是兜底，不该被当成常规路径（评审 P3）。
        _ = Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWLP_USERDATA, 0);
        FreeSelfHandle();
    }

    /// <summary>
    /// 释放根引用（幂等）。
    /// </summary>
    /// <remarks>
    /// WM_DESTROY 与 <see cref="Create"/> 的失败清理两条路都会走到这里：先到的那个放掉句柄，
    /// 后到的必须安静跳过——<c>GCHandle.Free()</c> 二次调用会抛
    /// <c>InvalidOperationException("Handle is not initialized.")</c>，在失败清理里会把真正的
    /// 失败原因顶掉（评审 2026-10-08 发现 1）。
    /// </remarks>
    private void FreeSelfHandle()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    // ---- 光标（B21）----

    /// <summary>应用显式设置的光标形状；<c>null</c> = 跟着页面 CSS 走。</summary>
    private OrielCursorIcon? _cursorIcon;

    /// <summary>当前的显示意图（与 <c>ShowCursor</c> 的计数器配对，防止重复加减）。</summary>
    private bool _cursorVisible = true;

    /// <summary>当前是否把光标限制在窗口内（<see cref="SetCursorGrab"/>）。</summary>
    private bool _cursorGrabbed;

    /// <summary>当前是否忽略鼠标事件（<see cref="SetIgnoreCursorEvents"/>）。</summary>
    private bool _ignoreCursorEvents;

    // ------------------------------------------------------------------
    // 平台开关 / 特效 / 任务栏（B22）
    // ------------------------------------------------------------------
    //
    // 十条平台开关 + 两条特效 + 任务栏四条。**Windows 是这一批里最全的平台**
    // （任务栏/进度条/覆盖图标/系统背板都只有 Windows 有），所以这里没有一条是空壳。
    // 两条明确不支持的（全工作区可见、标题栏样式）抛 NotSupportedException，
    // 理由写在各方法上，见 IPlatformBackend 的 B22 支持矩阵。

    /// <summary>当前是否显示系统装饰；<c>SetDecorations</c> 会改它。</summary>
    /// <remarks>
    /// 与创建期的 <see cref="OrielWindowOptions.Frameless"/> **分开记**：后者决定
    /// <c>WM_NCCALCSIZE</c> 一开始走哪条分支，而这条会被运行期改。
    /// </remarks>
    private bool _decorations;

    /// <summary>是否已把窗口从任务栏摘掉（见 <see cref="SetSkipTaskbar"/>）。</summary>
    private bool _skipTaskbar;

    /// <summary>任务栏上当前设置的覆盖图标（HICON；本对象持有，换/清时销毁）。</summary>
    private nint _overlayIcon;

    /// <summary>当前是否设置了"永远在底部"（见 <see cref="SetAlwaysOnBottom"/>）。</summary>
    private bool _alwaysOnBottom;

    /// <summary>
    /// 判定一次关闭请求（宿主否决 → 页面接管 → 放行）。
    /// </summary>
    /// <remarks>
    /// "页面可决定"要求两件事同时成立：页面**已开启接管**、且页面**已加载完成**。
    /// 后半条不能省——那条 <c>close-requested</c> 送不到未加载的页面，
    /// 挂起只会让窗口变成点 X 没反应（用户唯一的出路是任务管理器）。
    /// </remarks>
    private OrielCloseDecision DecideClose()
    {
        // 强制销毁途中不再问任何人：Destroy() 之后的任何关闭请求都直接放行，
        // 否则"页面接管"会把强制销毁也拦下来（那与 Destroy 的语义直接冲突）。
        if (_forceDestroy)
        {
            return OrielCloseDecision.Proceed;
        }

        return OrielClosePipeline.Decide(
            Closing,
            pageCanDecide: CloseInterceptEnabled && _loadedRaised,
            emitCloseRequested: () => PushEventOnUi(OrielClosePipeline.CloseRequestedEventName, "null"));
    }

    public void Destroy()
    {
        if (_hwnd == 0)
        {
            return;
        }

        // DestroyWindow **不发 WM_CLOSE**（它直接走销毁流程），所以天然绕过了
        // Closing 与页面接管——这正是 Destroy 要的语义。标记一下是为了让
        // 销毁路径上的其它判断（如果将来有）能看出"这是强制销毁"。
        _forceDestroy = true;
        Win32.DestroyWindow(_hwnd);
    }

    // ------------------------------------------------------------------
    // IPC（WebMessage 接收与回执见 Win32WebView2Ipc.cs，避免 async 与 unsafe 混用）
    // ------------------------------------------------------------------

    public OrielApp App => _app;

    /// <summary>本窗口的 label（见 <see cref="OrielWindowOptions.Label"/>）。</summary>
    public string Label => _options.Label;

    internal WindowsPlatformBackend Backend => _backend;

    /// <remarks>本类为 internal，public 成员不增加对外 API 面；它实现 <see cref="IWindowBackend.IsOnUiThread"/>。</remarks>
    public bool IsOnUiThread() => _backend.IsOnUiThread();

    /// <summary>
    /// 是否让页面接管关闭；初值取自 <see cref="OrielWindowOptions.InterceptClose"/>。
    /// </summary>
    public bool CloseInterceptEnabled { get; set; }

    /// <summary>哪几类宿主事件转发给页面；默认全关（见 <see cref="OrielWindowEventKind"/>）。</summary>
    public OrielWindowEventKind EventForwarding { get; set; }

    // ------------------------------------------------------------------
    // IWindowBackend
    // ------------------------------------------------------------------

    /// <summary>
    /// 窗口当前是否"显示中"（被 <see cref="Hide"/> 藏住后为 false）。
    /// </summary>
    /// <remarks>
    /// 直接问原生窗口样式，而不是回读 <see cref="_isVisible"/> 意图——意图可能领先于实际
    /// （控制器尚未装配、或某次 <c>ShowWindow</c> 失败），属性要对调用方如实。最小化的窗口
    /// 仍算"显示中"（<c>WS_VISIBLE</c> 未清，见 <see cref="Win32.IsWindowVisible"/>）。
    /// </remarks>
    public bool IsVisible => Win32.IsWindowVisible(_hwnd);

    /// <summary>是否有键盘焦点（见 <see cref="IWindowBackend.IsFocused"/>）。</summary>
    public bool IsFocused => _hwnd != 0 && Win32.GetForegroundWindow() == _hwnd;

    /// <summary>请求用户注意（见 <see cref="IWindowBackend.RequestUserAttention"/>）。</summary>
    /// <remarks>
    /// 用 <c>FlashWindowEx</c> 闪任务栏按钮：
    /// <c>Critical</c> 用 <c>FLASHW_TIMERNOFG</c>（一直闪到窗口被激活），
    /// <c>Informational</c> 只闪三下；取消走 <c>FLASHW_STOP</c>。
    /// </remarks>
    public void RequestUserAttention(OrielUserAttention? type)
    {
        if (_hwnd == 0)
        {
            return;
        }

        var info = new Win32.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32.FLASHWINFO>(),
            hwnd = _hwnd,
        };

        switch (type)
        {
            case OrielUserAttention.Critical:
                info.dwFlags = Win32.FLASHW_ALL | Win32.FLASHW_TIMERNOFG;
                info.uCount = 0;   // TIMERNOFG 时 uCount 被忽略
                break;

            case OrielUserAttention.Informational:
                info.dwFlags = Win32.FLASHW_ALL;
                info.uCount = 3;
                break;

            default:
                info.dwFlags = Win32.FLASHW_STOP;
                break;
        }

        _ = Win32.FlashWindowEx(ref info);
    }

    /// <summary>
    /// 显示窗口：**先**让宿主上屏（必要时直接以最大化出现），**再**把 WebView2 置为可见。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Hide"/> 成对，二者共同维护一条**跨平台不变量**（见 <see cref="Hide"/> 的说明）。
    /// 顺序不能反：宿主先可见、渲染面后可见，恢复过程不会出现"有窗口、没内容"的空框帧。
    /// <para>
    /// 上屏命令按创建期挂起的最大化意图二选一，而不是无条件 <c>SW_SHOW</c>：
    /// <c>SW_SHOW</c> 是"以当前大小和位置显示"，而 <c>WithHidden() + WithMaximized()</c> 起手的
    /// 窗口从未建立 <c>WS_MAXIMIZE</c>（见 <see cref="_maximizeOnShow"/>），
    /// 无条件 SW_SHOW 会让它按还原态出现。
    /// </para>
    /// </remarks>
    public void Show()
    {
        Win32.ShowWindow(
            _hwnd,
            _maximizeOnShow ? Win32Constants.SW_SHOWMAXIMIZED : Win32Constants.SW_SHOW);

        // 意图只兑现一次：此后再 Show 应当服从"最后一次显式状态变更"
        // （Maximize/Restore/Minimize 等都会清掉它）。
        _maximizeOnShow = false;

        SetControllerVisible(true);
    }

    /// <summary>
    /// 隐藏窗口：**先**把 WebView2 置为不可见，**再** <c>SW_HIDE</c> 宿主。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>跨平台不变量</b>：<see cref="Show"/> / <see cref="Hide"/> 必须覆盖**全部**承担呈现与
    /// 命中测试的表面——藏好之后不留任何仍在参与合成与命中测试的残余窗口。Linux
    /// （<c>gtk_widget_hide</c>）与 macOS（<c>orderOut:</c>）作用在**含 webview 的顶层窗口**上，
    /// 子部件随之一并消失，本就不存在残余；三端语义因此等价，改动 Windows 时不得让这条等价性退化。
    /// </para>
    /// <para>
    /// Windows 上这条不变量**必须显式维护**，因为本窗口走的是
    /// <c>CoreWebView2CompositionController</c>（见 <see cref="InitializeWebView2Async"/>）：
    /// WebView2 是 DirectComposition 合成树里的一个**视觉**，它的浏览器 HWND
    /// （类名 <c>Chrome_WidgetWin_1</c>）是一个由 <c>msedgewebview2.exe</c> 拥有的**顶层窗口**，
    /// **不是**宿主 HWND 的子窗口。因此对宿主发 <c>SW_HIDE</c> 并不会把它一起带走——它会继续
    /// 浮在桌面上参与命中测试，把整块区域的鼠标点击全部吃掉（现场症状："点桌面没反应"，
    /// 极易被误判成系统故障）。这里用 <c>ICoreWebView2Controller.IsVisible</c> 这个官方开关关掉它，
    /// 顺带停掉 Chromium 合成、省一份全屏 GPU 开销；**不要**退化成直接 <c>ShowWindow</c> 那个
    /// 浏览器 HWND（那会漏掉合成开销，也依赖对引擎内部窗口结构的假设）。
    /// </para>
    /// </remarks>
    public void Hide()
    {
        SetControllerVisible(false);
        Win32.ShowWindow(_hwnd, Win32Constants.SW_HIDE);
    }

    public void Close()
    {
        Win32.PostMessageW(_hwnd, Win32Constants.WM_CLOSE, 0, 0);
    }

    public void Focus()
    {
        Win32.SetForegroundWindow(_hwnd);
        Win32.SetFocus(_hwnd);
    }

    /// <summary>
    /// 显式改变窗口状态：调用方已经表过态，创建期挂起的"待最大化"不再代表最新意图。
    /// </summary>
    /// <remarks>
    /// 清 <see cref="_maximizeOnShow"/> 是必要的：否则"隐藏启动 + 最大化"的窗口在被
    /// <c>Restore()</c> 还原后，下一次 <see cref="Show"/> 又会把它撑满——用户看到的是
    /// "还原按钮点了没用，藏一下再出来又最大化了"。
    /// </remarks>
    public void Maximize()
    {
        _maximizeOnShow = false;
        Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMAXIMIZED);
    }

    /// <inheritdoc cref="Maximize"/>
    public void Minimize()
    {
        _maximizeOnShow = false;
        Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMINIMIZED);
    }

    /// <inheritdoc cref="Maximize"/>
    public void Restore()
    {
        _maximizeOnShow = false;
        Win32.ShowWindow(_hwnd, Win32Constants.SW_RESTORE);
    }

    /// <summary>IPC 回执通道：必须切回 UI 线程（WebView2 的 COM 绑定在 STA）。</summary>
    private sealed class UiThreadReplySink(Win32WindowHost host) : IIpcReplySink
    {
        public void PostJson(string json)
        {
            if (host.IsOnUiThread())
            {
                host.PostWebMessageOnUi(json);
            }
            else
            {
                host._backend.PostToMainThread(() => host.PostWebMessageOnUi(json));
            }
        }
    }
}
