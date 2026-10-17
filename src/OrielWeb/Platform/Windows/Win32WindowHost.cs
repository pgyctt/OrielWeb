using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = DirectN.IDCompositionDevice;
using DCompFunctions = DirectN.Functions;
using DCompHWND = DirectN.HWND;
using DCompPoint = DirectN.POINT;
using DCompTarget = DirectN.IDCompositionTarget;
using DCompVisual = DirectN.IDCompositionVisual;

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

    internal nint _hwnd;

    private IComObject<ICoreWebView2Environment>? _environment;
    private IComObject<ICoreWebView2CompositionController>? _compositionController;

    /// <summary>同一组合控制器的 <c>ICoreWebView2Controller</c> 视图（bounds / visible / focus 用）。</summary>
    private ICoreWebView2Controller? _controller;
    private IComObject<ICoreWebView2>? _webView;
    private CoreWebView2Events? _webViewEvents;
    private CoreWebView2CompositionControllerEvents? _compositionEvents;

    // DirectComposition 合成树。窗口用 WS_EX_NOREDIRECTIONBITMAP 创建，没有系统绘制的表面，
    // 内容完全由这棵树提供；WebView2 作为其中一个视觉（RootVisualTarget），因此它不再是子窗口。
    private DCompDevice? _dcompDevice;
    private DCompTarget? _dcompTarget;
    private DCompVisual? _dcompVisual;

    private bool _trackingMouseLeave;

    /// <summary>最近一次转发给 WebView 的客户区落点；<see cref="ForwardMouseLeave"/> 用它补发 LEAVE。</summary>
    private DCompPoint _lastMousePoint;
    private bool _hasLastMousePoint;

    private volatile bool _loadedRaised;

    private int _minWidth;
    private int _minHeight;
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

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;
    private event Action<bool>? MaximizedChanged;
    private event Action<OrielSizeChangedEventArgs>? Resized;
    private event Action<OrielPositionChangedEventArgs>? Moved;
    private event Action<bool>? FocusChanged;
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

    // 强制销毁中：Destroy() 置它，用来让关闭路径上的判断看出"这次不受拦"。
    private bool _forceDestroy;

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
    }

    public nint NativeWindowHandle => _hwnd;

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }
    event Action<bool>? IWindowBackend.MaximizedChanged { add => MaximizedChanged += value; remove => MaximizedChanged -= value; }
    event Action<OrielSizeChangedEventArgs>? IWindowBackend.Resized { add => Resized += value; remove => Resized -= value; }
    event Action<OrielPositionChangedEventArgs>? IWindowBackend.Moved { add => Moved += value; remove => Moved -= value; }
    event Action<bool>? IWindowBackend.FocusChanged { add => FocusChanged += value; remove => FocusChanged -= value; }
    event Action<string>? IWindowBackend.NavigationStarting { add => NavigationStarting += value; remove => NavigationStarting -= value; }
    event Action<OrielNavigationCompletedEventArgs>? IWindowBackend.NavigationCompleted { add => NavigationCompleted += value; remove => NavigationCompleted -= value; }
    event Action<OrielConsoleMessageEventArgs>? IWindowBackend.ConsoleMessage { add => ConsoleMessage += value; remove => ConsoleMessage -= value; }
    event Action<OrielMessageReceivedEventArgs>? IWindowBackend.MessageReceived { add => MessageReceived += value; remove => MessageReceived -= value; }
    event Action<string>? IWindowBackend.ContextMenuItemClicked { add => ContextMenuItemClicked += value; remove => ContextMenuItemClicked -= value; }
    event Action<OrielFileDropEventArgs>? IWindowBackend.FileDropped { add => FileDropped += value; remove => FileDropped -= value; }

    public bool IsMaximized => Win32.IsZoomed(_hwnd);

    // ---- 导航操作（WebView2 内建历史；无历史时调用是空操作）----

    public bool CanGoBack => _webView?.CanGoBack ?? false;
    public bool CanGoForward => _webView?.CanGoForward ?? false;
    public void GoBack() => _webView?.GoBack();
    public void GoForward() => _webView?.GoForward();
    public void Reload() => _webView?.Reload();

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    // ------------------------------------------------------------------
    // 上下文菜单
    // ------------------------------------------------------------------

    private event Action<string>? ContextMenuItemClicked;
    private event Action<OrielFileDropEventArgs>? FileDropped;

    /// <summary>渲染引擎内建右键菜单的策略；见 <see cref="OrielContextMenuPolicy"/>。</summary>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>
    /// WebView2 内建右键菜单弹出前的回调（<c>ContextMenuRequested</c>）。
    /// </summary>
    /// <remarks>
    /// 这是<b>唯一</b>能"改内建菜单"而不是"另起一个菜单"的地方：过滤掉不想要的项之后，
    /// 菜单仍由 WebView2 自己弹，留下来的剪切/复制/粘贴因此保持着引擎实现的行为
    /// （能直接作用在页面选区上）。自己用 <see cref="ShowContextMenu"/> 造一个做不到这点——
    /// 粘贴需要把系统剪贴板送进页面，页面自己无此权限。
    /// <para>
    /// 整个回调包在 <c>try</c> 里：过滤菜单失败是小事，让进程倒在这里是大事。
    /// </para>
    /// </remarks>
    private void OnContextMenuRequested(object? sender, ICoreWebView2ContextMenuRequestedEventArgs args)
    {
        try
        {
            switch (ContextMenuPolicy)
            {
                case OrielContextMenuPolicy.Native:
                    return; // 平台原样

                case OrielContextMenuPolicy.Disabled:
                    args.Handled = true; // 抑制：右键不弹任何东西
                    return;

                default:
                    FilterEditingItems(args);
                    return;
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 过滤内建右键菜单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 只留下剪切 / 复制 / 粘贴。
    /// </summary>
    /// <remarks>
    /// <b>必须从后往前删</b>：集合按下标操作，正序删会让后面的项整体前移一格，
    /// 于是紧接着那一项被跳过——表现为"删了但不干净"，且只在中段有要删的项时出现。
    /// <para>
    /// 判断依据是 <c>Name</c>（<b>未本地化</b>的英文标识，如 <c>"copy"</c>），<b>不是</b>
    /// <c>Label</c>（那是给用户看的本地化文本，中文环境下是「复制」）——拿 Label 判断会在换语言时
    /// 静默失效。也**不是** <c>Kind</c>：那个枚举说的是控件种类
    /// （Command / CheckBox / Radio / Separator / Submenu），与"是不是复制"不是一回事。
    /// </para>
    /// </remarks>
    private static void FilterEditingItems(ICoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (args.MenuItems is not { } items)
        {
            return;
        }

        using (items)
        {
            for (uint i = items.Count; i > 0; i--)
            {
                using IComObject<ICoreWebView2ContextMenuItem>? item = items.GetValueAtIndex(i - 1);
                if (item is null || !OrielContextMenuSupport.IsEditingWin32Name(item.Name))
                {
                    items.RemoveValueAtIndex(i - 1);
                }
            }

            // 一项都不剩时把菜单整个吃掉：否则会弹一个空框，看着像界面坏了
            if (items.Count == 0)
            {
                args.Handled = true;
            }
        }
    }

    /// <summary>
    /// 处理 <c>WM_DROPFILES</c>：从 HDROP 里逐个取路径，完成后 <c>DragFinish</c> 释放。
    /// </summary>
    /// <remarks>
    /// 取路径要**问两次**：第一次传 0 拿长度，第二次才把字符串读进缓冲区。
    /// 少一次就会得到一个截断的路径（或用固定大小缓冲区去猜）。HDROP 的生命周期由我们负责，
    /// 所以放在 <c>finally</c> 里释放——中途抛异常也不能漏。
    /// </remarks>
    private void HandleFileDrop(nint hDrop)
    {
        if (hDrop == 0)
        {
            return;
        }

        try
        {
            uint count = Win32.DragQueryFileW(hDrop, 0xFFFFFFFF, 0, 0);
            var paths = new List<string>((int)count);

            for (uint i = 0; i < count; i++)
            {
                uint length = Win32.DragQueryFileW(hDrop, i, 0, 0);
                if (length == 0)
                {
                    continue;
                }

                nint buffer = Marshal.AllocHGlobal((int)(length + 1) * sizeof(char));
                try
                {
                    Marshal.WriteInt16(buffer, 0);
                    if (Win32.DragQueryFileW(hDrop, i, buffer, length + 1) > 0
                        && Marshal.PtrToStringUni(buffer) is { Length: > 0 } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            if (paths.Count > 0)
            {
                FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
            }
        }
        finally
        {
            Win32.DragFinish(hDrop);
        }
    }

    /// <summary>
    /// 上下文菜单：构建 → 在鼠标位置弹出（**阻塞**）→ 释放。
    /// </summary>
    /// <remarks>
    /// 每次调用都新建并销毁原生菜单：上下文菜单的内容通常随调用点而变（点在不同对象上），
    /// 缓存它反而要额外判断"内容变了没有"。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
    {
        using Win32Menu? menu = Win32Menu.Build(items);
        if (menu is null)
        {
            return;
        }

        OrielMenuItem? item = menu.Popup(_hwnd);
        if (item is not null)
        {
            ActivateMenuItem(item);
        }
    }

    /// <summary>菜单项 → 行为：role 走共享解释器，自定义项上报到 <see cref="ContextMenuItemClicked"/>。</summary>
    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 Windows 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    /// <summary>
    /// 创建窗口：注册窗口类（幂等）→ CreateWindowExW（宿主经 lpParam 存入 GWLP_USERDATA，
    /// WndProc 据此取回实例）→ 触发 PostCreate（装配 WebView2）。
    /// </summary>
    public static unsafe Win32WindowHost Create(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, WindowsPlatformBackend backend)
    {
        EnsureWindowClass();
        var host = new Win32WindowHost(window, options, app, assets, backend);
        host._selfHandle = GCHandle.Alloc(host);

        uint style = ComputeStyle(options);
        int x = options.X ?? Win32Constants.CW_USEDEFAULT;
        int y = options.Y ?? Win32Constants.CW_USEDEFAULT;

        nint windowNamePtr = Marshal.StringToHGlobalUni(options.Title);
        var hwnd = Win32.CreateWindowExW(
            // 无重定向表面：窗口内容完全由 DirectComposition 提供，WebView2 作为视觉合成进来。
            // 这是 Composition 宿主的前提，也让 WebView2 不再是子窗口——边缘的 WM_NCHITTEST
            // 因此能到达本窗口，系统原生的边缘调整大小随之可用。
            // 接受文件拖放：Composition 宿主下 WebView2 不是子窗口（没有自己的 HWND），
            // 所以拖到窗口任意位置都会落到本窗口的 WM_DROPFILES，不会被 webview 截走。
            Win32Constants.WS_EX_NOREDIRECTIONBITMAP | Win32Constants.WS_EX_ACCEPTFILES,
            s_windowClassNamePtr,
            windowNamePtr,
            style,
            x, y, options.Width, options.Height,
            0, 0,
            Win32.GetModuleHandleW(null),
            (void*)GCHandle.ToIntPtr(host._selfHandle));
        Marshal.FreeHGlobal(windowNamePtr); // CreateWindowExW 已复制字符串

        if (hwnd == 0)
        {
            host._selfHandle.Free();
            throw new InvalidOperationException($"创建窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }
        host._hwnd = hwnd;

        // 尺寸与位置统一为**逻辑像素的客户区**（与 Linux/macOS 的 WithSize/At 同语义）：
        //   * 逻辑像素 → 物理像素：Per-Monitor-V2 下按**窗口所在显示器**的 DPI 折算。
        //     不折算的后果是 150%/200% 屏上窗口只有一半大（评审 2026-10-08 发现 2 修的就是位置漏折算）。
        //   * 客户区 → 整窗：Win32 的 CreateWindowExW / SetWindowPos 吃的是**整窗**尺寸，
        //     而 Linux 的 gtk_window_resize 与 macOS 的 setContentSize: 吃的是**客户区**。
        //     不换算的话同一份 WithSize(800,600) 在 Windows 上得到 784x561 的客户区
        //     （实测，见 --selftest geometry），而另两个平台得到 800x600。
        //     **frameless 窗口不换算**：它的 WM_NCCALCSIZE 把客户区铺满整窗，客户区已等于整窗。
        // 时机在窗口已建、尚未显示（PostCreate 才 ShowWindow）——用户看不到这次修正。
        uint initialDpi = Win32.GetDpiForWindow(hwnd);
        int physicalWidth = LogicalPixels.ToPhysical(options.Width, initialDpi);
        int physicalHeight = LogicalPixels.ToPhysical(options.Height, initialDpi);

        if (!options.Frameless)
        {
            (physicalWidth, physicalHeight) = ClientToWindowSize(
                physicalWidth, physicalHeight, style, initialDpi);
        }

        // 位置只在显式给了 At(x,y) 时才动：没给时窗口保持系统摆位（或稍后由 Center 摆），
        // 用 SWP_NOMOVE 保住它。At 一次给两维，缺一维按"没给位置"处理。
        // 注意位置**不需要**做客户区换算：At 定的是窗口左上角，与客户区大小无关。
        if (initialDpi != 96 || !options.Frameless)
        {
            if (options.X is int atX && options.Y is int atY)
            {
                _ = Win32.SetWindowPos(
                    hwnd, 0,
                    LogicalPixels.ToPhysical(atX, initialDpi), LogicalPixels.ToPhysical(atY, initialDpi),
                    physicalWidth, physicalHeight,
                    Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
            }
            else
            {
                _ = Win32.SetWindowPos(
                    hwnd, 0, 0, 0, physicalWidth, physicalHeight,
                    Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
            }
        }

        // WithIcon：覆盖窗口类带来的（exe）图标
        if (!string.IsNullOrEmpty(options.Icon))
        {
            ApplyExplicitIcon(hwnd, options.Icon);
        }

        try
        {
            host.SetupComposition();
            host.PostCreate();
        }
        catch
        {
            // 窗口已经建出来了：中段失败（如 DComp 设备创建不了）必须收掉 HWND 与根引用，
            // 否则窗口残留、WndProc 还在经 GWLP_USERDATA 把消息路由给这个半成品宿主。
            // DestroyWindow **同步**触发 WM_DESTROY → OnWindowDestroyedCore，那里已经放掉根引用、
            // 并按"未交付"跳过了应用可见的副作用；这里的调用只是幂等兜底——重复 Free 会抛
            // InvalidOperationException，把真正的失败原因顶掉（评审 2026-10-08 发现 1）。
            _ = Win32.DestroyWindow(hwnd);
            host.FreeSelfHandle();
            throw;
        }

        // 交付：此后销毁才触发 Closed 与后端窗口计数（见 _handedOff 的说明）。
        host._handedOff = true;
        return host;
    }

    /// <summary>
    /// 建立 DirectComposition 合成树：设备 → 窗口目标 → 根视觉。WebView2 稍后作为视觉挂到根视觉上
    /// （见 <see cref="InitializeWebView2Async"/>）。
    /// </summary>
    /// <remarks>
    /// 直接用 dcomp.h 的 COM 接口，而不是 WinRT 的 Windows.UI.Composition：后者需要
    /// net10.0-windows TFM，而本库面向跨平台的 net10.0。DirectNAot 正是 Win32 COM 的 AOT 绑定。
    /// </remarks>
    private unsafe void SetupComposition()
    {
        int hr = DCompFunctions.DCompositionCreateDevice(null, typeof(DCompDevice).GUID, out nint devicePtr);
        if (hr < 0 || devicePtr == 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 设备失败（HRESULT 0x{hr:X8}）。");
        }

        _dcompDevice = ComInterfaceMarshaller<DCompDevice>.ConvertToManaged((void*)devicePtr)
            ?? throw new InvalidOperationException("DirectComposition 设备接口转换失败。");

        hr = _dcompDevice.CreateTargetForHwnd(new DCompHWND(_hwnd), topmost: true, out var target);
        if (hr < 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 窗口目标失败（HRESULT 0x{hr:X8}）。");
        }
        _dcompTarget = target;

        hr = _dcompDevice.CreateVisual(out var visual);
        if (hr < 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 视觉失败（HRESULT 0x{hr:X8}）。");
        }
        _dcompVisual = visual;

        if (_dcompTarget.SetRoot(_dcompVisual) < 0 || _dcompDevice.Commit() < 0)
        {
            throw new InvalidOperationException("提交 DirectComposition 合成树失败。");
        }
    }

    private static uint ComputeStyle(OrielWindowOptions options)
    {
        uint style;
        if (options.Frameless)
        {
            style = Win32Constants.WS_POPUP | Win32Constants.WS_SYSMENU;
            if (options.Resizable)
            {
                style |= Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX | Win32Constants.WS_MINIMIZEBOX;
            }
        }
        else
        {
            style = Win32Constants.WS_OVERLAPPEDWINDOW;
            if (!options.Resizable)
            {
                style &= ~(Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX);
            }
        }
        return style;
    }

    private void PostCreate()
    {
        if (_options.Center && _options.X is null && _options.Y is null)
        {
            Center();
        }

        int showCommand = _options.Hidden ? Win32Constants.SW_HIDE
            : _options.Maximized ? Win32Constants.SW_SHOWMAXIMIZED
            : Win32Constants.SW_SHOW;
        Win32.ShowWindow(_hwnd, showCommand);

        if (_options.OnTop)
        {
            SetOnTop(true);
        }
        if (_options.Fullscreen)
        {
            SetFullscreen(true);
        }

        InitializeWebView2();
    }

    /// <summary>用 <see cref="OrielWindowOptions.Icon"/> 指定的文件覆盖本窗口的图标。</summary>
    /// <remarks>
    /// 大/小两个尺寸分别按当前系统度量加载（小图标用于标题栏与 Alt-Tab，大图标用于任务栏）。
    /// 走 <c>WM_SETICON</c> 而不是改窗口类：窗口类是进程级共享的，而图标是每窗口的选项。
    /// 加载到的 HICON 不释放——WM_SETICON 不接管所有权，但窗口销毁时要逐个跟踪释放，
    /// 而句柄数量与窗口数同阶（实际应用通常 1～2 个窗口），这里选择明确不释放而不是引入一套句柄生命周期管理。
    /// </remarks>
    private static void ApplyExplicitIcon(nint hwnd, string path)
    {
        nint large = Win32.LoadImageW(
            0, path, Win32Constants.IMAGE_ICON,
            Win32.GetSystemMetrics(Win32Constants.SM_CXICON),
            Win32.GetSystemMetrics(Win32Constants.SM_CYICON),
            Win32Constants.LR_LOADFROMFILE);
        nint small = Win32.LoadImageW(
            0, path, Win32Constants.IMAGE_ICON,
            Win32.GetSystemMetrics(Win32Constants.SM_CXSMICON),
            Win32.GetSystemMetrics(Win32Constants.SM_CYSMICON),
            Win32Constants.LR_LOADFROMFILE);

        if (large != 0)
        {
            _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_BIG, large);
        }
        if (small != 0)
        {
            _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_SMALL, small);
        }
    }

    private static unsafe void EnsureWindowClass()
    {
        if (Volatile.Read(ref s_windowClassRegistered) == 1)
        {
            return;
        }

        lock (s_windowClassGate)
        {
            if (Volatile.Read(ref s_windowClassRegistered) == 1)
            {
                return;
            }

            // 任务栏 / Alt-Tab 的按钮图标取自**窗口图标**。窗口完全没有图标时，shell 退回的是
            // 通用应用图标——不是 exe 自带的图标（实测确认过），所以这里必须显式把 exe 图标设到
            // 窗口类上，否则即使 csproj 配了 ApplicationIcon，运行期任务栏仍是通用图标。
            (nint hIcon, nint hIconSm) = LoadExecutableIcons();

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = Win32Constants.CS_HREDRAW | Win32Constants.CS_VREDRAW | Win32Constants.CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                hIcon = hIcon,
                hCursor = Win32.LoadCursorW(0, Win32Constants.IDC_ARROW),
                hbrBackground = 0, // WebView2 自绘客户端区，置空避免闪烁
                lpszClassName = s_windowClassNamePtr,
                hIconSm = hIconSm,
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                int error = Marshal.GetLastWin32Error(); // 先取错误码，DestroyIcon 会覆盖它
                _ = Win32.DestroyIcon(hIcon);
                _ = Win32.DestroyIcon(hIconSm);
                throw new InvalidOperationException($"注册窗口类失败（Win32 错误 {error}）。");
            }

            // 注册成功后才置位：此前是先置位后注册，若注册失败抛异常，标志已是 1
            // → 后续调用直接跳过 → 用未注册的类名去 CreateWindowExW，错误信息误导
            Volatile.Write(ref s_windowClassRegistered, 1);
        }
    }

    /// <summary>提取本进程可执行文件的图标（大/小两个尺寸），供窗口类使用。</summary>
    /// <remarks>
    /// 用 <c>ExtractIconEx</c> 而不是 <c>LoadIcon(module, IDI_APPLICATION)</c>：前者走 shell 自身的
    /// 图标解析、与资源管理器显示的一致，因此不依赖图标资源 ID（.NET SDK 用 32512，原生 .rc 可以是别的）。
    /// 应用未提供图标时返回 (0, 0)，窗口即不带图标。
    /// 取到的句柄归窗口类持有到进程结束——窗口类从不注销，故不再单独释放。
    /// </remarks>
    private static (nint Large, nint Small) LoadExecutableIcons()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return (0, 0);
        }

        uint extracted = Win32.ExtractIconExW(path, 0, out nint large, out nint small, 1);
        if (extracted == 0)
        {
            // 无图标或提取失败：此时句柄仍可能被回填，必须释放，避免泄漏。
            // （DestroyIcon(0) 是无害的，返回 false 并置一个无关的错误码。）
            _ = Win32.DestroyIcon(large);
            _ = Win32.DestroyIcon(small);
            return (0, 0);
        }

        return (large, small);
    }

    // ------------------------------------------------------------------
    // WndProc
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static unsafe nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            nint userData = Win32.GetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA);

            if (message == Win32Constants.WM_NCCREATE && userData == 0)
            {
                var createStruct = (CREATESTRUCTW*)lParam;
                if (createStruct->lpCreateParams != 0)
                {
                    Win32.SetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA, createStruct->lpCreateParams);
                    userData = createStruct->lpCreateParams;
                }
            }

            if (userData == 0)
            {
                return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
            }

            var host = (Win32WindowHost)GCHandle.FromIntPtr(userData).Target!;
            return host.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 用户事件处理器（Closing/Loaded/Closed 等）的异常绝不能穿越原生边界
            // （外泄 = 进程 fail-fast，不可捕获）。
            // 降级为默认处理：若异常来自 WM_CLOSE 的 Closing 回调，窗口会按默认语义销毁
            // ——即"取消关闭"的意图失效，这是"异常时无法确定用户意图"的合理降级。
            OrielDiagnostics.Report($"[OrielWeb] WindowProc 处理消息 0x{message:X} 时抛出异常：{ex}");
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private unsafe nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case Win32Constants.WM_NCCALCSIZE:
            {
                // 仅无边框窗口把客户区铺满整个窗口（见下段注释）。framed 窗口（库默认，
                // WS_OVERLAPPEDWINDOW）必须交回 DefWindowProc：不判 Frameless 会把系统标题栏与
                // 边框整个压没，且全客户区下 DefWindowProc 对 WM_NCHITTEST 只回 HTCLIENT，
                // 原生边缘 resize 一并丢失——Linux（gtk_window_set_decorated）与 macOS
                // （StyleTitled）的 framed 模式都是原生装饰，这里对齐。
                if (!_options.Frameless)
                {
                    break;
                }

                // 无边框窗口：客户区等于整个窗口，四周不留任何系统边框。
                // 之所以不再需要"让出边框换热区"（见 API.md 的历史记录）：窗口是
                // WS_EX_NOREDIRECTIONBITMAP + DirectComposition 宿主，WebView2 是合成树里的视觉
                // 而非子窗口，因此这条消息与 WM_NCHITTEST 都能真正到达本窗口，边缘命中由下面的
                // WM_NCHITTEST 分支显式给出。
                if (wParam != 0)
                {
                    // lParam 为 NCCALCSIZE_PARAMS*，其首成员已是窗口矩形；返回 0 即"客户区 = 该矩形"。
                    return 0;
                }

                // lParam 为 RECT*（拟议客户区，屏幕坐标）：显式改写为窗口矩形。
                if (lParam != 0
                    && Win32.GetWindowRect(hwnd, out var windowRect)
                    && windowRect.Right > windowRect.Left
                    && windowRect.Bottom > windowRect.Top)
                {
                    *(RECT*)lParam = windowRect;
                }
                return 0;
            }

            case Win32Constants.WM_NCHITTEST:
            {
                // 组合宿主下 WebView 不再是子窗口，系统终于会把这条消息送到本窗口。
                // 无边框窗口的客户区铺满整个窗口，边缘的调整大小语义必须在此显式补回；
                // framed 窗口的 NC 区与命中测试由 DefWindowProc 原生处理，不进这个分支。
                if (_options.Frameless && _options.Resizable)
                {
                    nint hit = HitTestResizeBorder(lParam);
                    if (hit != 0)
                    {
                        return hit;
                    }
                }
                break; // 其余情况交给 DefWindowProcW
            }

            case Win32Constants.WM_MOUSEMOVE:
            case Win32Constants.WM_LBUTTONDOWN:
            case Win32Constants.WM_LBUTTONUP:
            case Win32Constants.WM_LBUTTONDBLCLK:
            case Win32Constants.WM_RBUTTONDOWN:
            case Win32Constants.WM_RBUTTONUP:
            case Win32Constants.WM_RBUTTONDBLCLK:
            case Win32Constants.WM_MBUTTONDOWN:
            case Win32Constants.WM_MBUTTONUP:
            case Win32Constants.WM_MBUTTONDBLCLK:
            case Win32Constants.WM_MOUSEWHEEL:
            case Win32Constants.WM_MOUSEHWHEEL:
                ForwardMouseMessage(hwnd, message, wParam, lParam);
                break;

            case Win32Constants.WM_MOUSELEAVE:
                // 指针移出窗口：组合托管下 WebView 不是窗口，系统不会把"离开"送给它，必须由宿主
                // 补发 LEAVE——否则页面的 :hover / tooltip 会滞留在最后一次坐标上（评审 P3，
                // 2026-10-11 复核确认代码里缺的正是这一步）。标志复位后由下一次 WM_MOUSEMOVE 重新武装。
                _trackingMouseLeave = false;
                ForwardMouseLeave();
                return 0;

            // 键盘不需要转发：组合托管的 WebView 自己处理键盘输入，宿主只需在获得焦点时
            // 把焦点交给它（见下）。这与 AOTrino 的 Composition 宿主一致——它也只转发鼠标/指针。
            case Win32Constants.WM_SETFOCUS:
                _controller?.MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
                ReportFocus(true);
                return 0;

            // WM_KILLFOCUS **仍然不把焦点塞回组合宿主**（那会让页面的 caret/选区高亮与 IME
            // 状态错乱，见下方原说明）。但"上报失焦"与"转移焦点"是两件事：前者只是通知，
            // 不改变任何焦点归属，因此可以安全地加。Tauri 的 onFocusChanged 也是这个语义。
            case Win32Constants.WM_KILLFOCUS:
                ReportFocus(false);
                return 0;

            case Win32Constants.WM_SIZE:
                UpdateBounds();
                ReportPosition();
                return 0;

            case Win32Constants.WM_MOVE:
                // 注意：此处理论上应调用 controller.NotifyParentWindowPositionChanged()（槽 23），
                // 但实测在窗口最大化等场景该调用会触发 AV（疑似本机运行时 ComWrappers/互操作问题），
                // 且窗口位置变化由 WM_SIZE 的 put_Bounds 兜底，MVP 先跳过（见 API.md）。
                //
                // 但**上报位置**与那个调用无关，可以安全地做：页面拿不到可靠的窗口位置
                // （window.screenX 在多屏与 HiDPI 下三平台行为不一致），只能由宿主上报。
                ReportPosition();
                return 0;

            case Win32Constants.WM_GETMINMAXINFO:
            {
                var info = (MINMAXINFO*)lParam;

                // 最大化限制到显示器工作区（无边框 WS_POPUP 不会自动避开任务栏）
                var monitor = Win32.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (Win32.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    info->ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
                    info->ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
                    info->ptMaxPosition.X = monitorInfo.rcWork.Left;
                    info->ptMaxPosition.Y = monitorInfo.rcWork.Top;
                }

                // 最小尺寸同样按逻辑像素折算（与 Create/Resize 共用 LogicalPixels 这一个入口）。
                // 注意 ptMinTrackSize 是**整窗**尺寸（它约束的是 WM_GETMINMAXINFO 的跟踪尺寸），
                // 所以 framed 窗口还要把"客户区下限"换算成整窗——否则最小尺寸会退化成
                // "客户区能缩到 客户区下限 - 边框"，与另两个平台不一致。
                uint dpi = Win32.GetDpiForWindow(hwnd);
                if (_minWidth > 0)
                {
                    int minWidth = LogicalPixels.ToPhysical(_minWidth, dpi);
                    info->ptMinTrackSize.X = _options.Frameless
                        ? minWidth
                        : ClientToWindowSize(minWidth, 1, ComputeStyle(_options), dpi).Width;
                }

                if (_minHeight > 0)
                {
                    int minHeight = LogicalPixels.ToPhysical(_minHeight, dpi);
                    info->ptMinTrackSize.Y = _options.Frameless
                        ? minHeight
                        : ClientToWindowSize(1, minHeight, ComputeStyle(_options), dpi).Height;
                }

                return 0;
            }

            case Win32Constants.WM_CLOSE:
                // WM_CLOSE 是**同步消息**，而页面回执是异步 IPC——不可能在这里等页面答复
                // （等它就是锁死 UI 线程，而回执的处理恰好也要 UI 线程）。所以判定只有三种出路：
                //   放行 → 当场销毁；宿主否决 / 页面接管 → 不销毁（见 OrielClosePipeline）。
                switch (DecideClose())
                {
                    case OrielCloseDecision.Proceed:
                        Win32.DestroyWindow(hwnd);
                        break;

                    // Cancel 与 Suspend 都只是"这次不销毁"，但原因不同（见 OrielCloseDecision）：
                    //   Cancel  = 宿主的 Closing 拒绝了
                    //   Suspend = 页面接管了，等它稍后调 win.destroy
                    default:
                        break;
                }

                return 0;

            case Win32Constants.WM_DROPFILES:
                HandleFileDrop((nint)wParam);
                return 0;

            case Win32Constants.WM_DESTROY:
                OnWindowDestroyedCore();
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>窗口销毁时的清理：先关控制器、释放合成树，再触发 Closed 与窗口计数。</summary>
    internal void OnWindowDestroyedCore()
    {
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

    /// <summary>组合宿主不接收系统的光标设置，需把 WebView 当前光标自行应用到窗口。</summary>
    private void OnCursorChanged(object? sender, object args)
    {
        if (sender is ICoreWebView2CompositionController controller && controller.Cursor is { } cursor)
        {
            _ = Win32.SetCursor(cursor);
        }
    }

    // ------------------------------------------------------------------
    // WebView2 装配（异步回调均在 UI 线程）
    // ------------------------------------------------------------------

    internal void InitializeWebView2()
    {
        // 装配是异步的：立即返回，完成后继续（失败经 OnWebViewFailed 提示并销毁窗口）
        _ = InitializeWebView2Async();
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            EnsureLoaderInitialized();

            string? browserFolder = Environment.GetEnvironmentVariable("ORIEL_WEBVIEW2_FOLDER");

            // 先问 loader 能否找到运行时。这一步不能省：运行时缺失时
            // CreateCoreWebView2EnvironmentWithOptions 只会回一个"找不到文件"的 HRESULT，
            // 用户看到的是系统级文案，无从判断该装什么、去哪装。
            if (string.IsNullOrWhiteSpace(
                    WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString(browserFolder)))
            {
                // 必须延到消息循环启动后再处理：装配发生在 CreateWindow 内部，而
                // OrielApp.Run() 要等它返回才调用 window.Attach(backend)——此刻窗口门面
                // （ShowMessage/Close 等）还没有后端可用。延后处理保证回调拿到可用窗口。
                _backend.PostToMainThread(() => OnWebView2RuntimeMissing(browserFolder));
                return;
            }

            // 环境是**全进程共享**的（见 WindowsPlatformBackend.GetEnvironmentAsync）：
            // 每个窗口各建一个会撞在同一个 user data folder 上，而 WebView2 不允许同目录两个环境，
            // 第二个窗口会直接装配失败——"运行时再开一个窗口"因此在 Windows 上起不来。
            _environment = await _backend.GetEnvironmentAsync(browserFolder).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 环境失败（返回 null）。");

            _compositionController = await _environment.CreateCoreWebView2CompositionControllerAsync(_hwnd).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 组合控制器失败（返回 null）。");

            // 把 WebView 作为视觉挂进我们建立的 DirectComposition 树。此后它不再是子窗口，
            // 因此窗口能收到 WM_NCHITTEST —— 这是边缘调整大小得以走系统原生路径的关键。
            _compositionController.RootVisualTarget = _dcompVisual
                ?? throw new InvalidOperationException("DirectComposition 根视觉尚未建立。");

            // 挂上视觉后必须再提交一次，DComp 才会把它纳入当前的合成帧
            _dcompDevice?.Commit();

            // 组合控制器同时实现 ICoreWebView2Controller（bounds / visible / focus 等）
            _controller = _compositionController.Object as ICoreWebView2Controller
                ?? throw new InvalidOperationException("组合控制器未实现 ICoreWebView2Controller。");
            ApplyBackgroundColor();

            // 关掉 WebView2 自带的拖放：它默认会把外部文件拖放接管过去（转成页面的 drag 事件），
            // 而页面拿不到文件路径。关掉之后拖放落到窗口的 WM_DROPFILES，路径由原生侧给出。
            // 成员在 ICoreWebView2Controller4 上：老运行时没有这个接口，因此是可选增强（转换失败即跳过），
            // 这也避免"运行时版本决定行为"变成静默失败——跳过时下面的 WM_DROPFILES 路径仍然可用。
            if (_controller is ICoreWebView2Controller4 controller4)
            {
                controller4.AllowExternalDrop = false;
            }

            _compositionEvents = new CoreWebView2CompositionControllerEvents(_compositionController);
            _compositionEvents.CursorChanged += OnCursorChanged;

            _webView = _controller.CoreWebView2
                ?? throw new InvalidOperationException("获取 CoreWebView2 失败（返回 null）。");

            // 内嵌资源由 oriel:// 处理器应答（见 Win32AssetScheme）：旧实现是
            // SetVirtualHostNameToFolderMapping(host, 解压目录, DENY_CORS)，那要求资源先落盘，
            // 而且页面来源是 https 虚拟主机（与 Linux/macOS 的 file:// 不一致）。
            if (_assets is not null)
            {
                // 返回的事件处理器必须存着：托管侧没人引用它时，拦截会静默失效（页面白屏但无报错）。
                //
                // 第三个参数是 convertFileSrc 的授权器：**只接一个"这个绝对路径能不能读"的判定**，
                // 而 scope 与标识符留在应用侧（这里拿不到也不该知道）。传 null 也能跑，
                // 那样所有文件请求都会被 403——"没启用"绝不能被解释成"不限制"。
                _assetSchemeHandler = Win32AssetScheme.Attach(
                    _environment.Object,
                    _webView.Object,
                    _assets,
                    _assetHost,
                    absolutePath => App.Guard.TryAuthorizePath(
                        OrielAssetFileServer.ScopeCommand, absolutePath, App.Info.Identifier, out _));
            }

            if (_webView.Settings is { } settings)
            {
                using (settings)
                {
                    settings.AreDevToolsEnabled = _options.Debug;

                    // 缩放是**窗口**语义（见 OrielWindowOptions.Zoom）：关掉 Ctrl+滚轮 / Ctrl+± 与捏合。
                    // 文档层还有一道捕获期拦截（桥接脚本的缩放闸门），那是给没有等价开关的引擎备的。
                    settings.IsZoomControlEnabled = _options.Zoom;
                    settings.IsPinchZoomEnabled = _options.Zoom;
                }
            }

            _webViewEvents = new CoreWebView2Events(_webView);
            _webViewEvents.WebMessageReceived += OnWebMessageReceived;
            _webViewEvents.NavigationStarting += OnNavigationStarting;
            _webViewEvents.NavigationCompleted += OnNavigationCompleted;
            _webViewEvents.DocumentTitleChanged += OnDocumentTitleChanged;
            // 内建右键菜单：订阅它才有机会过滤（不订阅 = 平台原样弹）
            _webViewEvents.ContextMenuRequested += OnContextMenuRequested;

            await _webView.AddScriptToExecuteOnDocumentCreatedAsync(
                OrielBridgeJs.Build(
                    _options.ConsoleForwarding,
                    _options.Zoom,
                    App.Guard.Token,
                    App.Guard.TrustedPrefixes,
                    ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label))
                .ConfigureAwait(true);

            // 可见性按**意图态**恢复，而不是无条件置真：WithHidden() 起手时宿主已在 PostCreate 里
            // SW_HIDE，若这里硬置 true，那个顶层的 WebView2 渲染窗口（Chrome_WidgetWin_1）就会在
            // 首帧闸门期间（可达 8 秒）浮在桌面上吃点击——"程序还没显示出来就已经在吃点击"。
            // 闸门由宿主在 ui.ready 后调 Show() 打开（见 Hide() 的跨平台不变量说明）。
            _controller.IsVisible = _isVisible;
            UpdateBounds();

            // 内嵌资源的 URL 归一成 oriel://（兼容别名 https://<host>/… 也在这里统一），
            // 与 Linux/macOS 一样由 scheme 处理器应答。
            string url = _options.Url is { Length: > 0 } requested
                && AssetUrl.TryResolve(requested, _assetHost, out string relative)
                    ? AssetUrl.ForHost(_assetHost, relative)
                    : _options.Url ?? AssetUrl.DefaultDocument(_assetHost);
            _webView.Navigate(url);
        }
        catch (Exception ex)
        {
            OnWebViewFailed($"初始化 WebView2 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 一次性把 WebView2Loader.dll 从本库的嵌入资源解压并加载（单文件发布友好）。
    /// </summary>
    /// <remarks>
    /// 传的是本库（而非入口程序集）的程序集：loader 由本库内嵌（见 OrielWeb.csproj），
    /// 消费方因此不必在自己的项目里做任何发布配置。若传入口程序集，只有恰好也内嵌了
    /// loader 的应用才能加载，与本库的自包含目标相悖。
    /// </remarks>
    private static void EnsureLoaderInitialized()
    {
        if (Interlocked.Exchange(ref s_loaderInitialized, 1) == 1)
        {
            return;
        }

        WebView2Utilities.Initialize(typeof(Win32WindowHost).Assembly);
    }

    private void OnWebMessageReceived(object? sender, ICoreWebView2WebMessageReceivedEventArgs args)
    {
        // 逐消息来源（纵深加固）：WebView2 给的"发消息的文档 URI"比顶级导航 URL 快照准——
        // 服务器重定向不触发 NavigationStarting、provisional 期间有新旧文档窗口期、子帧消息
        // 也各有来源。快照（CurrentUrl）降级为兜底。
        string? messageOrigin = args.Source;
        string? originUrl = string.IsNullOrEmpty(messageOrigin) ? CurrentUrl : messageOrigin;

        string? json = args.WebMessageAsJson;
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        if (OrielIpcLimits.ExceedsLimit(json))
        {
            // 入站规模闸（见 OrielIpcLimits）：超限就不解析、直接丢弃。拿不到 id 也就回不了执，
            // 至少让它走门禁的上报点（Debug + IpcRejected），宿主能知道"页面发了条大消息被丢了"。
            App.Guard.Report(OrielIpcRejectionLayer.Size, OrielIpcLimits.OversizeReason(json.Length), originUrl, null);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("__oriel", out var kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                // 非 OrielWeb 协议的消息（页面自己的 postMessage），忽略
                return;
            }

            switch (kind.GetString())
            {
                case "invoke":
                    // 门禁在分发器里做：它有回执通道，能把"为什么被拒"送回页面，
                    // 而不是让那个 Promise 干等到 30 秒超时。
                    _ = DispatchInvokeAsync(root.Clone(), originUrl);
                    break;
                case "console":
                    // 只有开启 console 转发时页面才会发这类消息（hook 由桥接脚本注入决定）
                    if (Accept(root, originUrl))
                    {
                        RaiseConsoleMessage(ReadStringProperty(root, "level"), ReadStringProperty(root, "text"));
                    }
                    break;
                case "message":
                    // 单向消息没有命令名，因此只过来源与令牌那一层；
                    // 命令授权（allow/deny）只管 invoke，原因见 OrielCapabilityOptions。
                    if (Accept(root, originUrl))
                    {
                        RaiseMessageReceived(
                            ReadStringProperty(root, "name"),
                            root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : "null");
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // 非 OrielWeb 消息（页面自定义 postMessage），忽略
        }
    }

    /// <summary>读字符串属性；缺失或类型不符时返回空串（页面数据不可信，一律按可缺席处理）。</summary>
    private static string ReadStringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>入站消息的来源 + 令牌校验（命令授权不在这里，见分发器）。originUrl 为逐消息来源，null 时退回快照。</summary>
    private bool Accept(JsonElement root, string? originUrl)
    {
        // 拒绝的记账在门禁内部（Debug 输出 + IpcRejected 事件），这里只返回结论。
        return App.Guard.TryAccept(originUrl ?? CurrentUrl, root, out _);
    }

    private async Task DispatchInvokeAsync(JsonElement message, string? originUrl)
    {
        try
        {
            await App.Dispatcher
                .HandleInvokeAsync(message, new UiThreadReplySink(this), originUrl ?? CurrentUrl, _window)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 分发器内部已按命令捕获并净化；此处兜底分发器之外的错误。
            // 同样不透传 ex.Message（可能含内部路径），文案与分发器保持同一口径。
            OrielDiagnostics.Report($"[OrielWeb] IPC 分发意外失败：{ex}");
            PostMessageOnUi($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":\"命令执行失败。\"}}");
        }
    }

    private void OnNavigationCompleted(object? sender, ICoreWebView2NavigationCompletedEventArgs args)
    {
        // 新文档不知道当前最大化状态，重置记忆以便下面推一次初始值
        _wasMaximized = !Win32.IsZoomed(_hwnd);
        SyncMaximizedState();

        bool success = args.IsSuccess;
        // WebErrorStatus 是枚举（如 CONNECTION_ABORTED / HOST_NAME_NOT_RESOLVED），
        // 这里只做字符串化——各平台文案不同，不试图统一成人话。
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(
            success,
            _lastNavigationUri,
            success ? null : args.WebErrorStatus.ToString()));

        if (success)
        {
            RaiseLoadedIfFirst();
        }
    }

    private void OnDocumentTitleChanged(object? sender, EventArgs args)
    {
        string? title = _webView?.DocumentTitle;
        if (!string.IsNullOrEmpty(title))
        {
            RaiseTitleChanged(title);
        }
    }

    internal void OnWebViewFailed(string message)
    {
        MessageBoxResult(message, "OrielWeb", OrielMessageBoxIcon.Error);
        Win32.DestroyWindow(_hwnd);
    }

    /// <summary>
    /// WebView2 运行时不可用时的处置：优先交给应用注册的回调，未注册（或回调抛异常）则弹库的默认提示。
    /// </summary>
    /// <remarks>
    /// 库自身不引导安装：弹什么、要不要静默装、能不能自动装都是应用策略（企业环境可能禁止联网安装）。
    /// 库只负责把"缺运行时"这个事实准确暴露出来，并给出默认的、可照做的提示。
    /// </remarks>
    private void OnWebView2RuntimeMissing(string? browserFolder)
    {
        var handler = _app.WebView2RuntimeMissingHandler;
        bool handled = false;
        bool keepWindowOpen = false;

        if (handler is not null)
        {
            var args = new OrielWebView2RuntimeMissingEventArgs(_window, browserFolder);
            try
            {
                handler(args);
                handled = true;
                keepWindowOpen = args.KeepWindowOpen;
            }
            catch (Exception ex)
            {
                // 回调自身的异常不该夺走用户的知情权：回落到默认提示
                OrielDiagnostics.Report($"[OrielWeb] OnWebView2RuntimeMissing 回调抛出异常，改用默认提示：{ex}");
            }
        }

        if (!handled)
        {
            MessageBoxResult(RuntimeMissingMessage(browserFolder), "OrielWeb", OrielMessageBoxIcon.Error);
        }

        // 没有 WebView2 的窗口没有内容可按；销毁它即让应用退出，与"装完再启动"的流程一致。
        if (!keepWindowOpen)
        {
            Win32.DestroyWindow(_hwnd);
        }
    }

    /// <summary>默认提示：区分"系统未装 Evergreen 运行时"与"指定的固定版本目录无效"两种情形。</summary>
    private static string RuntimeMissingMessage(string? browserFolder) =>
        browserFolder is null
            ? "未检测到 Microsoft Edge WebView2 运行时，界面无法显示。\r\n\r\n" +
              "请安装 WebView2 运行时后重新启动本应用：\r\n" +
              OrielWebView2RuntimeMissingEventArgs.DownloadUrl
            : "未在指定目录找到 WebView2 运行时，界面无法显示。\r\n\r\n" +
              $"目录：{browserFolder}\r\n\r\n" +
              "请确认环境变量 ORIEL_WEBVIEW2_FOLDER 指向有效的固定版本运行时目录；" +
              "如需改用系统 Evergreen 运行时，请清除该变量。";

    internal void RaiseLoadedIfFirst()
    {
        if (!_loadedRaised)
        {
            _loadedRaised = true;
            Loaded?.Invoke();
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        Win32.SetWindowTextW(_hwnd, title); // 文档标题 → 窗口标题同步
        TitleChanged?.Invoke(title);
    }

    private void OnNavigationStarting(object? sender, ICoreWebView2NavigationStartingEventArgs args)
    {
        RaiseNavigationStarting(args.Uri ?? string.Empty);
    }

    internal void RaiseNavigationStarting(string url)
    {
        // _lastNavigationUri 同时充当"当前文档 URL"：入站 IPC 要做来源校验，
        // 而消息本身不带来源，只能由"这个窗口现在停在哪个 URL"来回答
        // （它原本是给 NavigationCompleted 补 URL 用的——WebView2 的完成事件里没有 URL）。
        _lastNavigationUri = url;
        NavigationStarting?.Invoke(url);
        PushEventOnUi("navigation.starting", $"{{\"url\":{JsonText.EncodeString(url)}}}");
    }

    /// <summary>当前文档的 URL（导航开始时更新）。入站 IPC 的来源校验用它。</summary>
    internal string? CurrentUrl => _lastNavigationUri;

    /// <summary>
    /// 应用"内核就绪前的窗口底色"（见 <see cref="OrielWindowOptions.BackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 用 WebView2 的 <c>DefaultBackgroundColor</c>（<c>ICoreWebView2Controller2</c>，运行时 1.0.774+）。
    /// **不用窗口类的背景刷子**：本窗口是 <c>WS_EX_NOREDIRECTIONBITMAP</c> + DirectComposition 宿主，
    /// 没有系统绘制的表面，没人会去画那个刷子——只有引擎自己知道首帧之前该铺什么色。
    /// 拿不到那个接口（老运行时）或设置失败时按默认处理并记一笔：底色是装饰性的，不该影响窗口本身。
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        if (_controller is null || !_options.TryGetBackgroundArgb(out byte a, out byte r, out byte g, out byte b))
        {
            return;
        }

        try
        {
            if (WebView2Utilities.GetInterface<ICoreWebView2Controller2>(_controller) is not { } controller2)
            {
                OrielDiagnostics.Report("[OrielWeb] 当前 WebView2 运行时没有 ICoreWebView2Controller2，BackgroundColor 未生效。");
                return;
            }

            controller2.put_DefaultBackgroundColor(new COREWEBVIEW2_COLOR { A = a, R = r, G = g, B = b }).ThrowOnError();
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 设置窗口底色失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 快照里**没有**缩放：拖动与双击用的都是页面 CSS 像素（Win32 侧同理，坐标到页面之前已经换算过），
    /// 再注入一份 scale 只会诱导页面自己折算一次。取不到时返回 0，由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 兜底。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
        => OrielSystemSnapshot.Normalize((int)Win32.GetDoubleClickTime());

    internal void RaiseNavigationCompleted(OrielNavigationCompletedEventArgs args)
    {
        NavigationCompleted?.Invoke(args);
        string error = args.Error is null ? "null" : JsonText.EncodeString(args.Error);
        PushEventOnUi(
            "navigation.completed",
            $"{{\"success\":{(args.Success ? "true" : "false")},\"url\":{JsonText.EncodeString(args.Url)},\"error\":{error}}}");
    }

    internal void RaiseConsoleMessage(string level, string text)
        => ConsoleMessage?.Invoke(new OrielConsoleMessageEventArgs(level, text));

    internal void RaiseMessageReceived(string name, string json)
        => MessageReceived?.Invoke(new OrielMessageReceivedEventArgs(name, json));

    /// <summary>
    /// 向页面推送一个 oriel 事件（页面侧 <c>window.oriel.on(name, handler)</c> 接收）。
    /// 与回执通道 <see cref="PostMessageOnUi"/> 分开：那条只投递 <c>{"__oriel":"result",…}</c> 形态。
    /// </summary>
    private void PushEventOnUi(string name, string jsonValue)
    {
        PostMessageOnUi(
            $"{{\"__oriel\":\"event\",\"name\":{JsonText.EncodeString(name)},\"value\":{jsonValue}}}");
    }

    /// <summary>
    /// 检测最大化状态变化并推送给页面。用户经原生路径最大化/还原（拖边框到屏幕顶端、
    /// 双击标题栏、Win+↑）时，页面无从得知，标题栏的"最大化/还原"图标必须靠这条通知同步。
    /// </summary>
    private void SyncMaximizedState()
    {
        bool isMaximized = Win32.IsZoomed(_hwnd);
        if (isMaximized == _wasMaximized)
        {
            return;
        }

        _wasMaximized = isMaximized;
        MaximizedChanged?.Invoke(isMaximized);
        PushEventOnUi("maximized", isMaximized ? "true" : "false");
    }

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

    internal void UpdateBounds()
    {
        SyncMaximizedState();

        var controller = _controller;
        if (controller is null || !Win32.GetClientRect(_hwnd, out var client))
        {
            return;
        }

        controller.Bounds = new DirectN.RECT
        {
            left = 0,
            top = 0,
            right = client.Right,
            bottom = client.Bottom,
        };

        // 客户区是**物理像素**，而上报给宿主的必须是**逻辑像素**（与 Resize 的入参同语义）。
        // 除而不是乘：GetClientRect 给的是设备像素，逻辑值 = 设备值 / 缩放比。
        double scale = CurrentScaleFactor();
        ReportSize(client.Right - client.Left, client.Bottom - client.Top, scale);
    }

    /// <summary>
    /// 当前窗口所在显示器的缩放比（1.0 = 100%）。
    /// </summary>
    /// <remarks>
    /// 用 <c>GetDpiForWindow</c> 而不是 <c>GetDpiForSystem</c>：本进程是 Per-Monitor-V2 感知的，
    /// 窗口被拖到另一块不同缩放的显示器后 DPI 会变，而系统 DPI 不变。用系统值的话，
    /// 副屏上的尺寸/位置上报会整体偏一个倍数。
    /// </remarks>
    private double CurrentScaleFactor()
    {
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// 把物理像素的客户区尺寸换成逻辑像素并上报（与上次相同则跳过）。
    /// </summary>
    /// <remarks>
    /// 去重是必须的：拖动边框时 <c>WM_SIZE</c> 每个像素来一次，每秒可达数百次。
    /// 每次都触发托管事件 + 页面 IPC 会把一次拖动变成一场流量风暴，而其中 99% 的值是重复的。
    /// </remarks>
    private void ReportSize(int physicalWidth, int physicalHeight, double scale)
    {
        double width = physicalWidth / scale;
        double height = physicalHeight / scale;

        if (_lastReportedSize is { } previous
            && Math.Abs(previous.Width - width) < 0.5
            && Math.Abs(previous.Height - height) < 0.5)
        {
            return;
        }

        _lastReportedSize = (width, height);
        Resized?.Invoke(new OrielSizeChangedEventArgs(width, height));

        // 页面侧转发是**按需**的（默认关）：拖动时这条路径每个像素走一次，
        // 而推给页面在 macOS/Linux 上是一次 JS 求值。见 OrielWindowEventKind。
        if ((EventForwarding & OrielWindowEventKind.Resize) != 0)
        {
            PushEventOnUi("resize", OrielEventPayload.Size(width, height));
        }
    }


    /// <summary>上报窗口位置（物理像素的窗口矩形 → 逻辑像素的左上角）；与上次相同则跳过。</summary>
    private void ReportPosition()
    {
        if (!Win32.GetWindowRect(_hwnd, out var window))
        {
            return;
        }

        double scale = CurrentScaleFactor();
        double x = window.Left / scale;
        double y = window.Top / scale;

        if (_lastReportedPosition is { } previous
            && Math.Abs(previous.X - x) < 0.5
            && Math.Abs(previous.Y - y) < 0.5)
        {
            return;
        }

        _lastReportedPosition = (x, y);
        Moved?.Invoke(new OrielPositionChangedEventArgs(x, y));

        if ((EventForwarding & OrielWindowEventKind.Move) != 0)
        {
            PushEventOnUi("move", OrielEventPayload.Position(x, y));
        }
    }

    /// <summary>上报焦点变化（与上次相同则跳过；首个事件一定会报）。</summary>
    private void ReportFocus(bool focused)
    {
        if (_lastReportedFocus == focused)
        {
            return;
        }

        _lastReportedFocus = focused;
        FocusChanged?.Invoke(focused);

        // 页面侧是两个事件名（focus / blur）而不是一个带布尔值的事件——
        // 那是 Tauri 的形状，页面可以只订阅其中一个。
        if ((EventForwarding & OrielWindowEventKind.Focus) != 0)
        {
            PushEventOnUi(focused ? "focus" : "blur", "null");
        }
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// 把屏幕坐标转换为窗口边缘的调整大小命中值；不在边缘时返回 0（交由默认处理）。
    /// 四角优先于四边（系统的判定顺序亦然）。
    /// </summary>
    private nint HitTestResizeBorder(nint lParam)
    {
        // 最大化 / 全屏时不允许拖动边缘改尺寸
        if (_isFullscreen || Win32.IsZoomed(_hwnd))
        {
            return 0;
        }

        // WM_NCHITTEST 的 lParam：低 16 位为 x、高 16 位为 y 的**有符号**屏幕坐标
        // （多显示器下可为负，故必须按 short 解释）
        long packed = lParam.ToInt64();
        int screenX = (short)(packed & 0xFFFF);
        int screenY = (short)((packed >> 16) & 0xFFFF);

        if (!Win32.GetWindowRect(_hwnd, out var window))
        {
            return 0;
        }

        int border = BorderThickness(horizontal: true);
        bool left = screenX < window.Left + border;
        bool right = screenX >= window.Right - border;
        bool top = screenY < window.Top + border;
        bool bottom = screenY >= window.Bottom - border;

        if (top && left) return Win32Constants.HTTOPLEFT;
        if (top && right) return Win32Constants.HTTOPRIGHT;
        if (bottom && left) return Win32Constants.HTBOTTOMLEFT;
        if (bottom && right) return Win32Constants.HTBOTTOMRIGHT;
        if (left) return Win32Constants.HTLEFT;
        if (right) return Win32Constants.HTRIGHT;
        if (top) return Win32Constants.HTTOP;
        if (bottom) return Win32Constants.HTBOTTOM;
        return 0;
    }

    /// <summary>
    /// 把窗口收到的鼠标消息注入组合托管的 WebView。组合模式下 WebView 收不到系统输入
    /// （它不是窗口），全部输入必须由宿主经 <c>SendMouseInput</c> 转发。
    /// </summary>
    private void ForwardMouseMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var controller = _compositionController?.Object;
        if (controller is null)
        {
            return;
        }

        if (message == Win32Constants.WM_MOUSEMOVE)
        {
            EnsureMouseLeaveTracking();
        }

        long packed = lParam.ToInt64();
        var point = new DCompPoint((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        if (point.x is < -32000 or > 32000 || point.y is < -32000 or > 32000)
        {
            return; // 窗口最小化等场景下的哨兵坐标
        }

        // 滚轮消息的坐标是屏幕坐标，其余是客户区坐标。ScreenToClient 需要 DirectN 的 POINT，
        // 而它非 blittable（无法用于源生成 P/Invoke），故用客户区原点自行换算。
        if (message is Win32Constants.WM_MOUSEWHEEL or Win32Constants.WM_MOUSEHWHEEL)
        {
            var origin = new POINT(0, 0);
            if (!Win32.ClientToScreen(hwnd, ref origin))
            {
                return;
            }
            point = new DCompPoint(point.x - origin.X, point.y - origin.Y);
        }

        var kind = message switch
        {
            Win32Constants.WM_MOUSEMOVE => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE,
            Win32Constants.WM_LBUTTONDOWN or Win32Constants.WM_LBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOWN,
            Win32Constants.WM_LBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_UP,
            Win32Constants.WM_RBUTTONDOWN or Win32Constants.WM_RBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOWN,
            Win32Constants.WM_RBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_UP,
            Win32Constants.WM_MBUTTONDOWN or Win32Constants.WM_MBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_DOWN,
            Win32Constants.WM_MBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_UP,
            Win32Constants.WM_MOUSEWHEEL => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_WHEEL,
            _ => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_HORIZONTAL_WHEEL,
        };

        // 滚轮的滚动量在 wParam 高 16 位（有符号），其余消息按按键状态折算虚拟键
        uint data = message is Win32Constants.WM_MOUSEWHEEL or Win32Constants.WM_MOUSEHWHEEL
            ? (uint)(short)((long)wParam >> 16)
            : 0;

        var keys = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;
        long state = (long)wParam;
        if ((state & Win32Constants.MK_LBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON;
        if ((state & Win32Constants.MK_RBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON;
        if ((state & Win32Constants.MK_MBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_MIDDLE_BUTTON;
        if ((state & Win32Constants.MK_SHIFT) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_SHIFT;
        if ((state & Win32Constants.MK_CONTROL) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_CONTROL;

        // 记下最后落点：WM_MOUSELEAVE 到达时指针已经在窗口外，系统给的坐标没有意义，
        // 补发 LEAVE 要用窗口内最后一次有效坐标（见 ForwardMouseLeave）。
        _lastMousePoint = point;
        _hasLastMousePoint = true;

        controller.SendMouseInput(kind, keys, data, point);
    }

    /// <summary>补发一次 <c>COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE</c>。</summary>
    /// <remarks>
    /// 组合托管模式下 WebView 收不到任何系统输入（它不是窗口），"离开"同样要宿主转发；
    /// WebView2 只认这个事件来清 hover。坐标用窗口内最后一个有效落点——LEAVE 关心的是
    /// "离开了"这个事实，而不是离开后的坐标。窗口最小化、或还没转发过任何鼠标（没有可用落点、
    /// 也就没有可清的 hover）时不发，避免注入无意义的指针事件。
    /// </remarks>
    private void ForwardMouseLeave()
    {
        var controller = _compositionController?.Object;
        if (controller is null || !_hasLastMousePoint)
        {
            return;
        }

        controller.SendMouseInput(
            COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE,
            COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
            0,
            _lastMousePoint);
    }

    /// <summary>令光标离开窗口时补发一次 <c>WM_MOUSELEAVE</c>（Windows 默认不持续发送）。</summary>
    private void EnsureMouseLeaveTracking()
    {
        if (_trackingMouseLeave)
        {
            return;
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = 0x00000002, // TME_LEAVE
            hwndTrack = _hwnd,
        };
        if (Win32.TrackMouseEvent(ref track))
        {
            _trackingMouseLeave = true;
        }
    }

    /// <summary>系统窗口边框厚度（物理像素，随窗口所在显示器 DPI 缩放）。</summary>
    private int BorderThickness(bool horizontal)
    {
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        // 必须用 GetSystemMetricsForDpi：GetSystemMetrics 在此进程的 per-monitor DPI 感知下
        // 返回的是系统 DPI 的值，在副屏（不同缩放）上会明显偏小。
        int frame = Win32.GetSystemMetricsForDpi(
            horizontal ? Win32Constants.SM_CXSIZEFRAME : Win32Constants.SM_CYSIZEFRAME, dpi);
        frame += Win32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, dpi);
        return frame > 0 ? frame : 8;
    }

    /// <summary>
    /// 客户区尺寸（物理像素）→ 整窗尺寸（物理像素）。
    /// </summary>
    /// <param name="clientWidth">客户区宽（物理像素）。</param>
    /// <param name="clientHeight">客户区高（物理像素）。</param>
    /// <param name="style">窗口样式（决定边框与标题栏的组成）。</param>
    /// <param name="dpi">窗口所在显示器的 DPI。</param>
    /// <remarks>
    /// <para>
    /// 存在的唯一理由：把 <c>WithSize</c> / <c>Resize</c> 的语义统一成**客户区**。
    /// Linux 的 <c>gtk_window_resize</c> 与 macOS 的 <c>setContentSize:</c> 都直接设客户区，
    /// 只有 Win32 设整窗——不换算就是"同一份代码在 Windows 上小一圈"。
    /// </para>
    /// <para>
    /// <b>调用方必须自己排除 frameless 窗口</b>：那种窗口的 <c>WM_NCCALCSIZE</c> 让客户区
    /// 铺满整窗，客户区已等于整窗，再换算会大一圈。这里不做判断，因为样式与
    /// <see cref="OrielWindowOptions.Frameless"/> 是两处信息，放在一处判更不容易漏。
    /// </para>
    /// <para>
    /// 换算失败（<c>AdjustWindowRectExForDpi</c> 返回 false）时**退回原值**而不是抛异常：
    /// 拿不到边框尺寸时"窗口小一圈"比"应用起不来"好得多，而且这属于平台调用失败，
    /// 不该让一个纯观感问题升级成启动失败。
    /// </para>
    /// </remarks>
    private static (int Width, int Height) ClientToWindowSize(
        int clientWidth, int clientHeight, uint style, uint dpi)
    {
        if (dpi == 0)
        {
            dpi = 96;
        }

        var rect = new RECT { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
        if (!Win32.AdjustWindowRectExForDpi(ref rect, style, false, Win32Constants.WS_EX_NOREDIRECTIONBITMAP, dpi))
        {
            return (clientWidth, clientHeight);
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // 极端情况下（样式异常、DPI 巨大）可能算出非正值：退回原值，不让窗口变成 0 宽
        return width > 0 && height > 0 ? (width, height) : (clientWidth, clientHeight);
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

    /// <summary>窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。</summary>
    public double ScaleFactor => CurrentScaleFactor();

    /// <summary>客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。</summary>
    public OrielSize InnerSize
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetClientRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielSize((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
        }
    }

    /// <summary>整窗尺寸（逻辑像素）；见 <see cref="IWindowBackend.OuterSize"/>。</summary>
    public OrielSize OuterSize
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetWindowRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielSize((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
        }
    }

    /// <summary>窗口左上角（逻辑像素）；见 <see cref="IWindowBackend.OuterPosition"/>。</summary>
    public OrielPosition OuterPosition
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetWindowRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielPosition(rect.Left / scale, rect.Top / scale);
        }
    }

    /// <summary>
    /// 客户区左上角（逻辑像素）；见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </summary>
    /// <remarks>
    /// 用 <c>ClientToScreen(hwnd, (0,0))</c> 而不是"外框位置 + 边框厚度"：
    /// 后者要分别猜左右边框与标题栏三处，而 Win32 直接给了精确答案。
    /// </remarks>
    public OrielPosition InnerPosition
    {
        get
        {
            if (_hwnd == 0)
            {
                return default;
            }

            var origin = new POINT(0, 0);
            if (!Win32.ClientToScreen(_hwnd, ref origin))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielPosition(origin.X / scale, origin.Y / scale);
        }
    }

    internal void PostWebMessageOnUi(string json) => PostMessageOnUi(json);

    private void PostMessageOnUi(string json)
    {
        try
        {
            _webView?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // 窗口销毁后的迟到回执，忽略
            OrielDiagnostics.Report($"[OrielWeb] 投递回执失败（窗口可能已销毁）：{ex.Message}");
        }
    }

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        uint flags = Win32Constants.MB_OK | icon switch
        {
            OrielMessageBoxIcon.Warning => Win32Constants.MB_ICONWARNING,
            OrielMessageBoxIcon.Error => Win32Constants.MB_ICONERROR,
            OrielMessageBoxIcon.Question => Win32Constants.MB_ICONQUESTION,
            _ => Win32Constants.MB_ICONINFORMATION,
        };
        Win32.MessageBoxW(_hwnd, text, title ?? _title, flags);
    }

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

    /// <summary>
    /// 显示窗口：**先** <c>SW_SHOW</c> 宿主，**再**把 WebView2 置为可见。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Hide"/> 成对，二者共同维护一条**跨平台不变量**（见 <see cref="Hide"/> 的说明）。
    /// 顺序不能反：宿主先可见、渲染面后可见，恢复过程不会出现"有窗口、没内容"的空框帧。
    /// </remarks>
    public void Show()
    {
        Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOW);
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

    /// <summary>
    /// 记录可见性意图，并在控制器已就绪时同步给 WebView2。
    /// </summary>
    /// <remarks>
    /// 控制器在装配完成前为 null（例如 <c>WithHidden()</c> 起手、页面尚未加载就调 <see cref="Show"/>）：
    /// 这时只更新意图，等 <see cref="InitializeWebView2Async"/> 按意图恢复。
    /// 设置失败只记一笔——可见性是窗口状态的一部分，不该让一次 COM 调用失败掀翻整个窗口操作。
    /// </remarks>
    private void SetControllerVisible(bool visible)
    {
        _isVisible = visible;
        if (_controller is not { } controller)
        {
            return;
        }

        try
        {
            controller.IsVisible = visible;
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 设置 WebView2 可见性失败：{ex.Message}");
        }
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

    public void Maximize() => Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMAXIMIZED);
    public void Minimize() => Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMINIMIZED);
    public void Restore() => Win32.ShowWindow(_hwnd, Win32Constants.SW_RESTORE);

    public void BeginDrag()
    {
        // 经典技巧：释放鼠标捕获后发送 NC 按钮消息，让系统进入标题栏拖动模态循环
        if (Win32.ReleaseCapture())
        {
            _ = Win32.SendMessageW(_hwnd, Win32Constants.WM_NCLBUTTONDOWN, (nuint)Win32Constants.HTCAPTION, 0);
        }
    }

    // 流式拖动（macOS 用）；Windows 原生模态拖动已覆盖，此处 no-op
    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
    {
    }

    public void DragTo(double dx, double dy)
    {
    }

    public void EndDrag()
    {
    }

    public bool ToggleMaximize()
    {
        if (Win32.IsZoomed(_hwnd))
        {
            Restore();
        }
        else
        {
            Maximize();
        }
        return Win32.IsZoomed(_hwnd);
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        Win32.SetWindowPos(
            _hwnd,
            enabled ? Win32Constants.HWND_TOPMOST : Win32Constants.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled == _isFullscreen)
        {
            return;
        }

        if (enabled)
        {
            var placement = default(WINDOWPLACEMENT);
            placement.length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();
            Win32.GetWindowPlacement(_hwnd, ref placement);
            _savedPlacement = placement;

            // 保存原始样式：退出全屏时必须原样还原。此前退出时无条件
            // style |= WS_OVERLAPPEDWINDOW，会把无边框（WS_POPUP）窗口全屏后变成带边框
            nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
            _savedStyle = style;
            style &= ~(nint)(Win32Constants.WS_CAPTION | Win32Constants.WS_THICKFRAME);
            // WS_POPUP = 0x80000000：在 AnyCPU 下编译期 nint 被视为 32 位，常量转换会报 CS8778。
            // 实际运行于 64 位进程，语义为置位高位，unchecked 是有意为之。
            style |= unchecked((nint)Win32Constants.WS_POPUP);
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);

            var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            Win32.GetMonitorInfo(monitor, ref info);
            Win32.SetWindowPos(_hwnd, 0,
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED | Win32Constants.SWP_SHOWWINDOW);
            _isFullscreen = true;
        }
        else
        {
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, _savedStyle);

            var placement = _savedPlacement;
            Win32.SetWindowPlacement(_hwnd, ref placement);
            Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
            _isFullscreen = false;
        }
    }

    /// <summary>当前窗口标题（见 <see cref="IWindowBackend.Title"/>）。</summary>
    public string Title => _title;

    public void SetTitle(string title)
    {
        _title = title;
        Win32.SetWindowTextW(_hwnd, title);
    }

    public void SetResizable(bool enabled)
    {
        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
        const uint resizableMask = Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX;
        style = enabled
            ? style | (nint)resizableMask
            : style & ~(nint)resizableMask;
        Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
            Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
    }

    public void MoveTo(int x, int y)
    {
        // x/y 是屏幕逻辑坐标（API.md 的契约，三平台一致），按当前窗口 DPI 折算成物理像素——
        // 与 Resize/最小尺寸共用 LogicalPixels（评审 2026-10-08 发现 2：此前只有尺寸折算）。
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        Win32.SetWindowPos(
            _hwnd, 0,
            LogicalPixels.ToPhysical(x, dpi), LogicalPixels.ToPhysical(y, dpi),
            0, 0,
            Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Resize(int width, int height)
    {
        // width/height 是**逻辑像素的客户区**（与 Linux gtk_window_resize、macOS setContentSize: 同语义）：
        //   1. 逻辑 → 物理（按当前窗口 DPI）；
        //   2. 客户区 → 整窗（仅 framed；frameless 的客户区已等于整窗）。
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        int physicalWidth = LogicalPixels.ToPhysical(width, dpi);
        int physicalHeight = LogicalPixels.ToPhysical(height, dpi);

        if (!_options.Frameless)
        {
            (physicalWidth, physicalHeight) = ClientToWindowSize(
                physicalWidth, physicalHeight, ComputeStyle(_options), dpi);
        }

        Win32.SetWindowPos(
            _hwnd, 0, 0, 0,
            physicalWidth, physicalHeight,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Center()
    {
        if (!Win32.GetWindowRect(_hwnd, out var windowRect))
        {
            return;
        }
        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;

        var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        int x = info.rcWork.Left + ((info.rcWork.Right - info.rcWork.Left) - width) / 2;
        int y = info.rcWork.Top + ((info.rcWork.Bottom - info.rcWork.Top) - height) / 2;
        Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        var webView = _webView
            ?? throw new InvalidOperationException("WebView2 尚未就绪。");

        return await webView.ExecuteScriptAsync(script).ConfigureAwait(true) ?? "null";
    }

    public void PostMessageAsJson(string json) => _backend.PostToMainThread(() => PostMessageOnUi(json));

    // ------------------------------------------------------------------
    // 对话框
    // ------------------------------------------------------------------

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
        => ShowFileDialog(
            isSave: false,
            options.Title,
            options.Filters,
            defaultExtension: null,
            options.InitialDirectory,
            options.AllowMultiple);

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        string[] paths = ShowFileDialog(
            isSave: true,
            options.Title,
            options.Filters,
            options.DefaultExtension,
            options.InitialDirectory,
            allowMultiple: false);

        return paths.Length > 0 ? paths[0] : null;
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        // initialDirectory 在 Windows 上被忽略：SHBrowseForFolder 要设初始位置得挂 BFFM_INITIALIZED 回调
        // （见 Win32Native 里选型说明）。这里显式丢弃，而不是假装支持。
        _ = initialDirectory;

        nint titlePtr = title is null ? 0 : Marshal.StringToHGlobalUni(title);
        nint pathBuffer = Marshal.AllocHGlobal((Win32Constants.MAX_PATH + 1) * sizeof(char));
        try
        {
            Marshal.WriteInt16(pathBuffer, 0);

            var browseInfo = new BROWSEINFOW
            {
                hwndOwner = _hwnd,
                lpszTitle = titlePtr,
                ulFlags = Win32Constants.BIF_RETURNONLYFSDIRS | Win32Constants.BIF_USENEWUI,
            };

            nint pidl = Win32.SHBrowseForFolderW(ref browseInfo);
            if (pidl == 0)
            {
                return null; // 取消
            }

            try
            {
                if (!Win32.SHGetPathFromIDListW(pidl, pathBuffer))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(pathBuffer) is { Length: > 0 } path ? path : null;
            }
            finally
            {
                Win32.CoTaskMemFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
            if (titlePtr != 0)
            {
                Marshal.FreeHGlobal(titlePtr);
            }
        }
    }

    /// <summary>
    /// 打开/保存对话框的公共实现。返回路径数组（取消为空数组）。
    /// </summary>
    private string[] ShowFileDialog(
        bool isSave,
        string? title,
        IReadOnlyList<OrielFileFilter>? filters,
        string? defaultExtension,
        string? initialDirectory,
        bool allowMultiple)
    {
        string filterString = OrielFileFilter.RenderForWin32(filters ?? []);
        nint filterPtr = Marshal.StringToHGlobalUni(filterString);
        nint bufferPtr = Marshal.AllocHGlobal(32768 * sizeof(char));
        nint titlePtr = title is null ? 0 : Marshal.StringToHGlobalUni(title);
        nint initialDirPtr = initialDirectory is null ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        nint defExtPtr = defaultExtension is null ? 0 : Marshal.StringToHGlobalUni(defaultExtension);

        try
        {
            // 预填当前目录，避免对话框落在系统目录
            Marshal.WriteInt16(bufferPtr, 0);

            uint flags = isSave
                ? Win32Constants.OFN_OVERWRITEPROMPT | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR
                : Win32Constants.OFN_FILEMUSTEXIST | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR;

            if (allowMultiple && !isSave)
            {
                // OFN_EXPLORER 必须一起带：否则原生对话框按旧格式返回，且单个文件也会带出目录段
                flags |= Win32Constants.OFN_ALLOWMULTISELECT | Win32Constants.OFN_EXPLORER;
            }

            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = _hwnd,
                lpstrFilter = filterPtr,
                lpstrFile = bufferPtr,
                nMaxFile = 32768,
                lpstrTitle = titlePtr,
                lpstrInitialDir = initialDirPtr,
                lpstrDefExt = defExtPtr,
                Flags = flags,
            };

            bool ok = isSave ? Win32.GetSaveFileNameW(ref ofn) : Win32.GetOpenFileNameW(ref ofn);
            if (!ok)
            {
                return []; // 取消或错误（CommDlgExtendedError 区分，统一当取消）
            }

            // 多选时缓冲区里是"目录\0名\0名\0\0"这种多段结构，所以**必须按长度读**：
            // Marshal.PtrToStringUni(ptr) 遇到第一个 \0 就截断，那样拿到的是目录段（或被截短的文件名）。
            return OrielFileDialogSupport.ParseWin32MultiSelect(Marshal.PtrToStringUni(bufferPtr, 32768));
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(bufferPtr);
            if (titlePtr != 0) Marshal.FreeHGlobal(titlePtr);
            if (initialDirPtr != 0) Marshal.FreeHGlobal(initialDirPtr);
            if (defExtPtr != 0) Marshal.FreeHGlobal(defExtPtr);
        }
    }

    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自定义按钮文案在 Windows 上不生效</b>：<c>MessageBoxW</c> 只显示系统文案
    /// （"确定"/"取消"/"是"/"否"），要自定义得上 <c>TaskDialogIndirect</c>，
    /// 而那个 API 需要 comctl32 v6 的激活清单——为一个文案引入那套机制不划算。
    /// 返回的是**语义**，所以页面侧的判断不受影响，只有显示出来的文字是系统的。
    /// </para>
    /// <para>
    /// <c>MessageBoxW</c> 返回按钮 ID（<c>IDOK</c>=1、<c>IDCANCEL</c>=2、<c>IDYES</c>=6、
    /// <c>IDNO</c>=7），而 <c>MB_YESNO</c> 与 <c>MB_OKCANCEL</c> 的"取消"返回的都是
    /// <c>IDCANCEL</c>——所以映射是"ID → 语义"，不需要（也做不到）靠传进来的按钮集合反推。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        // 显示文案只能用系统的（MessageBoxW 没有自定义入口）。
        // 显式丢弃而不是"忘了用"：这两个参数在另两个平台上是有用的。
        _ = okLabel;
        _ = cancelLabel;

        uint flags = buttons switch
        {
            MessageButtons.OkCancel => Win32Constants.MB_OKCANCEL,
            MessageButtons.YesNo => Win32Constants.MB_YESNO,
            MessageButtons.YesNoCancel => Win32Constants.MB_YESNOCANCEL,
            _ => Win32Constants.MB_OK,
        };

        flags |= kind switch
        {
            "warning" => Win32Constants.MB_ICONWARNING,
            "error" => Win32Constants.MB_ICONERROR,
            "question" => Win32Constants.MB_ICONQUESTION,
            _ => Win32Constants.MB_ICONINFORMATION,
        };

        int result = Win32.MessageBoxW(_hwnd, message, title ?? _title, flags);

        return result switch
        {
            Win32Constants.IDOK => MessageResult.Ok,
            Win32Constants.IDCANCEL => MessageResult.Cancel,
            Win32Constants.IDYES => MessageResult.Yes,
            Win32Constants.IDNO => MessageResult.No,
            _ => null,                  // 被外部关掉（比如任务管理器）时没有结果
        };
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
