using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 窗口宿主：NSWindow + WKWebView，全部经 objc_msgSend。
/// 窗口原点/尺寸由 JS 侧提供 + 宿主跟踪增量（不做 struct 返回的 msgSend）。
/// </summary>
internal sealed partial class MacOSWindowHost : IWindowBackend
{
    // NSWindowStyleMask
    private const nuint StyleTitled = 1 << 0;
    private const nuint StyleClosable = 1 << 1;
    private const nuint StyleMiniaturizable = 1 << 2;
    private const nuint StyleResizable = 1 << 3;
    private const nuint StyleFullSizeContentView = 1 << 15;

    private const nint NSWindowCloseButton = 0;
    private const nint NSWindowMiniaturizeButton = 1;
    private const nint NSWindowZoomButton = 2;

    private const nint NSWindowTitleHidden = 1;
    private const nint NSFloatingWindowLevel = 3; // kCGFloatingWindowLevel（置顶）
    private const nint NSViewWidthSizable = 2;
    private const nint NSViewHeightSizable = 16;

    private readonly WebviewWindow _window;
    private readonly OrielWindowOptions _options;
    private readonly OrielApp _app;

    /// <summary>
    /// 本窗口的门面对象。内建 <c>win.*</c> 命令要靠它作用到**发起调用的那个窗口**上
    /// （多窗口下不能用一个全局的"当前窗口"代替）。
    /// </summary>
    internal WebviewWindow Window => _window;
    private readonly MacOSPlatformBackend _backend;
    private readonly EmbeddedAssetStore? _assets;

    /// <summary>oriel:// 的 scheme 处理器（挂在 config 上，必须活到 webview 销毁）。</summary>
    private nint _assetSchemeHandler;
    private readonly MacOSWebMessageHandler _messageHandler;

    private nint _nsWindow;
    private nint _nsWindowDelegate;
    private nint _webview;

    /// <summary>运行期设过的窗口底色（<c>setBackgroundColor</c>）；<c>null</c> 表示没设过，用于回落创建期选项。</summary>
    private uint? _runtimeBackgroundArgb;
    private nint _navigationDelegate;
    private nint _scriptHandler;
    /// <summary>承载拖放的容器视图（webview 是它的子视图）。</summary>
    private nint _dropView;
    /// <summary>UI 委托：目前只为 <c>willOpenMenu:</c>（过滤内建右键菜单）而存在。</summary>
    private nint _uiDelegate;
    private volatile bool _loadedRaised;

    // 无边框/窗口几何跟踪（点；cocoa 坐标原点在左下）
    private double _cocoaX;
    private double _cocoaY;
    private (double X, double Y)? _dragPointerStart;
    private (double X, double Y)? _dragWindowOrigin;

    private int _minWidth;
    private int _minHeight;

    /// <summary>最大尺寸（逻辑像素，0 = 不限制）。与 <c>_minWidth</c> / <c>_minHeight</c> 配对。</summary>
    private int _maxWidth;
    private int _maxHeight;

    /// <summary>已发出的"请求注意"请求 id（<c>cancelUserAttentionRequest:</c> 要用）。</summary>
    /// <remarks>
    /// macOS 的 <c>requestUserAttention:</c> 返回一个 id，取消时要把它交回去——
    /// 这与另两个平台（"取消"是个无参动作）不同，所以要记下来。
    /// </remarks>
    private nint _attentionRequestId;

    private bool _isFullscreen;
    private bool _isOnTop;
    private string _title;
    private int _evalSeq;

    // 上一次已上报（托管事件 + 页面）的最大化状态。NSWindow 的 zoom 是同步生效的，
    // 这个记忆值用于去重：windowDidResize / ToggleMaximize / 导航完成都可能触发同步。
    private bool _wasMaximized;
    // 页面是否已加载完成（桥接脚本就绪）。就绪前不推事件；加载完成时会强制补推一次当前状态。
    private bool _pageReady;

    // 上一次已上报的客户区尺寸 / 窗口位置（逻辑像素）。windowDidResize / windowDidMove
    // 在拖动时每个像素来一次，不去重就是把每秒几百次 ObjC 回调变成同样数量的托管事件 + 页面 IPC。
    private (double Width, double Height)? _lastReportedSize;
    private (double X, double Y)? _lastReportedPosition;
    // 上一次已上报的焦点状态；null 表示"还没报过"（首个 didBecomeKey 必须报，哪怕值相同）
    private bool? _lastReportedFocus;

    /// <summary>上一次已上报的缩放比；null 表示"还没报过"。</summary>
    private double? _lastReportedScaleFactor;

    // 强制销毁中：Destroy() 置它，用来让关闭路径上的判断看出"这次不受拦"。
    private bool _forceDestroy;

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
    private event Action<string>? ContextMenuItemClicked;
    private event Action<OrielFileDropEventArgs>? FileDropped;

    /// <summary>渲染引擎内建右键菜单的策略；见 <see cref="OrielContextMenuPolicy"/>。</summary>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>
    /// 内建右键菜单即将弹出（由 <c>OrielUIDelegate</c> 的 <c>willOpenMenu:</c> 调进来）。
    /// </summary>
    /// <remarks>
    /// <c>willOpenMenu:</c> 没有"拒绝弹出"的返回值，所以接管的方式是**换掉菜单内容**：
    /// 清空 WebKit 的项，换上只含剪辑命令的项。不再就地增删 WebKit 的项——那类做法依赖渲染引擎
    /// 的内部结构（Linux 侧同源的写法在 WebKitGTK 4.1 上会破坏内存），而自建菜单这条路本来就有。
    /// </remarks>
    internal void FilterContextMenu(nint menu)
    {
        if (menu == 0)
        {
            return;
        }

        switch (ContextMenuPolicy)
        {
            case OrielContextMenuPolicy.Native:
                return; // 平台原样

            case OrielContextMenuPolicy.Disabled:
                ObjCRuntime.SendVoid(menu, ObjCRuntime.Sel("removeAllItems"));
                return;

            default:
                ObjCRuntime.SendVoid(menu, ObjCRuntime.Sel("removeAllItems"));
                AppendEditingItems(menu);
                return;
        }
    }

    /// <summary>
    /// 往菜单里追加剪切 / 复制 / 粘贴。
    /// </summary>
    /// <remarks>
    /// action 用 AppKit 的标准 selector、target 留空，选择时由响应链交给 <c>WKWebView</c> 执行，
    /// 因此真正作用于页面选区与系统剪贴板；顺带白拿 Cmd+X/C/V 的快捷键。
    /// </remarks>
    private static void AppendEditingItems(nint menu)
    {
        foreach (OrielMenuItem item in OrielMenuRoles.EditingMenuItems())
        {
            string? selectorName = EditingSelector(item.Role);
            if (selectorName is null)
            {
                continue;
            }

            nint menuItem = ObjCRuntime.SendIdObjObjObj(
                ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSMenuItem"), ObjCRuntime.Sel("alloc")),
                ObjCRuntime.Sel("initWithTitle:action:keyEquivalent:"),
                ObjCRuntime.MakeNSString(OrielMenuRoles.DefaultLabel(item.Role) ?? string.Empty),
                ObjCRuntime.Sel(selectorName),
                ObjCRuntime.MakeNSString(EditingKeyEquivalent(item.Role)));

            if (menuItem == 0)
            {
                continue;
            }

            // NSEventModifierFlagCommand = 1 << 20
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setKeyEquivalentModifierMask:"), 1 << 20);
            ObjCRuntime.SendVoidObj(menu, ObjCRuntime.Sel("addItem:"), menuItem);
        }
    }

    /// <summary>编辑类 role → AppKit 的标准 selector；不认识的 role 返回 null。</summary>
    private static string? EditingSelector(string? role) => role switch
    {
        OrielMenuRole.Cut => "cut:",
        OrielMenuRole.Copy => "copy:",
        OrielMenuRole.Paste => "paste:",
        OrielMenuRole.Undo => "undo:",
        OrielMenuRole.Redo => "redo:",
        OrielMenuRole.SelectAll => "selectAll:",
        OrielMenuRole.Delete => "delete:",
        _ => null,
    };

    private static string EditingKeyEquivalent(string? role) => role switch
    {
        OrielMenuRole.Cut => "x",
        OrielMenuRole.Copy => "c",
        OrielMenuRole.Paste => "v",
        _ => string.Empty,
    };

    /// <summary>
    /// 把编辑类 role 经 AppKit 的响应链交给 <c>WKWebView</c> 执行。
    /// </summary>
    /// <remarks>
    /// 自建菜单里的编辑项走这里（<c>document.execCommand('paste')</c> 会被安全策略拦下）。
    /// target 传 nil 是有意的：<c>sendAction:to:from:</c> 会从当前第一响应者沿响应链找实现，
    /// 编辑时第一响应者正是 webview 内部的编辑视图；找不到时返回 false，不会崩。
    /// </remarks>
    private bool TryExecuteNativeEditing(string role)
    {
        string? selectorName = EditingSelector(role);
        if (selectorName is null)
        {
            return false;
        }

        nint app = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));

        return ObjCRuntime.SendBoolRetObjObjObj(
            app, ObjCRuntime.Sel("sendAction:to:from:"), ObjCRuntime.Sel(selectorName), 0, 0);
    }

    internal MacOSWindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, MacOSPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app;
        _backend = backend;
        _assets = assets;
        _title = options.Title;
        // 策略是可写属性（运行时能改），初值取自 options
        ContextMenuPolicy = options.ContextMenuPolicy;
        CloseInterceptEnabled = options.InterceptClose;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        _messageHandler = new MacOSWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _nsWindow;

    public OrielApp App => _app;

    /// <summary>本窗口的 label（见 <see cref="OrielWindowOptions.Label"/>）。</summary>
    public string Label => _options.Label;
    /// <remarks>本类为 internal，public 成员不增加对外 API 面；它实现 <see cref="IWindowBackend.IsOnUiThread"/>。</remarks>
    public bool IsOnUiThread() => _backend.IsOnUiThread();
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

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

    public bool IsMaximized => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed"));

    /// <summary>
    /// 窗口当前是否"显示中"（<see cref="Hide"/> 之后为 false；最小化仍算显示中）。
    /// </summary>
    /// <remarks>
    /// <c>orderOut:</c> 藏的是整个 NSWindow，WKWebView 是它的 contentView 子视图、随之一并消失，
    /// 不存在 Windows 组合宿主那种"渲染面是独立顶层窗口"的残余（见 <c>Win32WindowHost.Hide</c> 的不变量）。
    /// </remarks>
    public bool IsVisible => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isVisible"));

    /// <summary>是否最小化（见 <see cref="IWindowBackend.IsMinimized"/>）。</summary>
    /// <remarks>
    /// Cocoa 的 <c>isMiniaturized</c> 就是"收进 Dock 了"。
    /// 注意它与 <c>isVisible</c> **不互斥**：最小化的窗口仍然 visible。
    /// </remarks>
    public bool IsMinimized => _nsWindow != 0 && ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isMiniaturized"));

    /// <summary>是否有键盘焦点（见 <see cref="IWindowBackend.IsFocused"/>）。</summary>
    /// <remarks>
    /// <c>isKeyWindow</c> 是"接收键盘事件的那个窗口"，语义与另两个平台一致。
    /// 它与 <c>isMainWindow</c> 不同：后者是"主窗口"，一个应用可以有多个主窗口，
    /// 但同时只有一个 key window。
    /// </remarks>
    public bool IsFocused => _nsWindow != 0 && ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isKeyWindow"));

    /// <summary>是否全屏（取库记住的状态，见 <see cref="IWindowBackend.IsFullscreen"/>）。</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>是否置顶（取库记住的状态，见 <see cref="IWindowBackend.IsAlwaysOnTop"/>）。</summary>
    public bool IsAlwaysOnTop => _isOnTop;

    /// <summary>是否有系统装饰（取创建时的选项，见 <see cref="IWindowBackend.IsDecorated"/>）。</summary>
    public bool IsDecorated => !_options.Frameless;

    /// <summary>是否可缩放（原生读，见 <see cref="IWindowBackend.IsResizable"/>）。</summary>
    public bool IsResizable => (CurrentStyleMask() & StyleResizable) != 0;

    /// <summary>
    /// 是否可最大化（见 <see cref="IWindowBackend.IsMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// 用 resizable 位：AppKit 的"缩放"（zoom / 绿灯）以
    /// <c>NSWindowStyleMaskResizable</c> 为前提，没有独立的最大化位。
    /// </remarks>
    public bool IsMaximizable => (CurrentStyleMask() & StyleResizable) != 0;

    /// <summary>是否可最小化（原生读，见 <see cref="IWindowBackend.IsMinimizable"/>）。</summary>
    public bool IsMinimizable => (CurrentStyleMask() & StyleMiniaturizable) != 0;

    /// <summary>是否可关闭（原生读，见 <see cref="IWindowBackend.IsClosable"/>）。</summary>
    public bool IsClosable => (CurrentStyleMask() & StyleClosable) != 0;

    /// <summary>
    /// 是否接收输入（见 <see cref="IWindowBackend.IsEnabled"/>）。
    /// </summary>
    /// <remarks>
    /// **只挡鼠标、不挡键盘**：Cocoa 没有"禁用窗口"这个概念，这里用
    /// <c>ignoresMouseEvents</c> 近似。如实写出来，而不是假装与另两个平台一致。
    /// </remarks>
    public bool IsEnabled => _nsWindow != 0
        && !ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("ignoresMouseEvents"));

    /// <summary>启用/禁用输入（见 <see cref="IWindowBackend.SetEnabled"/>）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_nsWindow != 0)
        {
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), !enabled);
        }
    }

    /// <summary>设置最大尺寸（见 <see cref="IWindowBackend.SetMaxSize"/>）。</summary>
    /// <remarks>
    /// <c>setContentMaxSize:</c> 收 <c>NSSize</c>。<b>传 0 表示不限制</b>——
    /// Cocoa 的约定是用一个很大的值（<c>CGFLOAT_MAX</c>）表示"没有上限"，
    /// 而不是 0（0 会把窗口卡成 0 宽）。这里用 <c>double.MaxValue</c> 对应它。
    /// </remarks>
    public void SetMaxSize(int width, int height)
    {
        _maxWidth = width;
        _maxHeight = height;

        if (_nsWindow == 0)
        {
            return;
        }

        // 0 → "没有上限"。用 double.MaxValue 而不是 0：后者是"最多 0 像素"。
        ObjCRuntime.SendVoidDouble2(
            _nsWindow,
            ObjCRuntime.Sel("setContentMaxSize:"),
            width > 0 ? width : double.MaxValue,
            height > 0 ? height : double.MaxValue);
    }

    /// <summary>请求用户注意（见 <see cref="IWindowBackend.RequestUserAttention"/>）。</summary>
    /// <remarks>
    /// <c>requestUserAttention:</c> 的取值是 <c>NSCriticalRequest = 0</c> /
    /// <c>NSInformationalRequest = 10</c>（**不是** 0/1，容易记错），
    /// 返回值是一个请求 id，取消时要交给 <c>cancelUserAttentionRequest:</c>。
    /// </remarks>
    public void RequestUserAttention(OrielUserAttention? type)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        if (type is null)
        {
            if (_attentionRequestId != 0)
            {
                ObjCRuntime.SendVoidNint(
                    ObjCRuntime.GetClass("NSApplication"),
                    ObjCRuntime.Sel("cancelUserAttentionRequest:"),
                    _attentionRequestId);
                _attentionRequestId = 0;
            }

            return;
        }

        const int NsCriticalRequest = 0;
        const int NsInformationalRequest = 10;

        int request = type == OrielUserAttention.Critical ? NsCriticalRequest : NsInformationalRequest;

        _attentionRequestId = ObjCRuntime.SendNintNint(
            _nsWindow, ObjCRuntime.Sel("requestUserAttention:"), request);
    }

    /// <summary>
    /// 读当前的 <c>styleMask</c>。
    /// </summary>
    /// <remarks>
    /// 每次读一次而不是缓存：<c>SetResizable</c> 就是改这个位，
    /// 缓存会与"AppKit 实际接受的状态"脱节。
    /// </remarks>
    private nuint CurrentStyleMask()
        => _nsWindow == 0 ? 0 : (nuint)ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));

    // ---- 导航操作（WKWebView 内建历史；无历史时调用是空操作）----
    // _webview 在窗口销毁后被置 0，判空是必需的。

    public bool CanGoBack => _webview != 0 && ObjCRuntime.SendBoolRet(_webview, ObjCRuntime.Sel("canGoBack"));
    public bool CanGoForward => _webview != 0 && ObjCRuntime.SendBoolRet(_webview, ObjCRuntime.Sel("canGoForward"));

    public void GoBack()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("goBack"));
        }
    }

    public void GoForward()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("goForward"));
        }
    }

    public void Reload()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("reload"));
        }
    }

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    // ------------------------------------------------------------------
    // 上下文菜单
    // ------------------------------------------------------------------

    private MacOSMenu? _contextMenu;

    /// <summary>
    /// 上下文菜单：构建 → 在鼠标位置弹出。
    /// </summary>
    /// <remarks>
    /// 弹出是异步的，菜单对象因此留到下一次弹出前才释放（不能弹完即抛）。
    /// 位置由当前 <c>NSEvent</c> 决定；没有当前事件时 <see cref="MacOSMenu.Popup"/> 会明确跳过并记日志，
    /// 而不是弹到随机位置。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
    {
        _contextMenu?.Dispose();
        _contextMenu = MacOSMenu.Build(items, ActivateMenuItem);
        if (_contextMenu is not null && _webview != 0)
        {
            _contextMenu.Popup(_webview);
        }
    }

    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window, TryExecuteNativeEditing))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 macOS 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }

    /// <summary>拖放视图解析出的本地路径（由 <c>MacOSObjCClasses</c> 的 trampoline 调用）。</summary>
    internal void OnFilesDropped(IReadOnlyList<string> paths)
        => FileDropped?.Invoke(new OrielFileDropEventArgs(paths));

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    internal void Create()
    {
        // 取类即校验：类不存在时宁可显式报错，也不要让后面一长串 objc_msgSend 静默 no-op
        var clsWindow = ObjCRuntime.GetClassOrThrow("NSWindow");
        var clsWKWebView = ObjCRuntime.GetClassOrThrow("WKWebView");
        var clsWKWebViewConfiguration = ObjCRuntime.GetClassOrThrow("WKWebViewConfiguration");
        var clsWKUserScript = ObjCRuntime.GetClassOrThrow("WKUserScript");

        double width = _options.Width;
        double height = _options.Height;

        nuint style = StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable;
        if (_options.Frameless)
        {
            style |= StyleFullSizeContentView;
        }

        _nsWindow = ObjCRuntime.SendIdDouble4NuintNuintBool(
            ObjCRuntime.SendId(clsWindow, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithContentRect:styleMask:backing:defer:"),
            0, 0, width, height,
            style,
            2, // NSBackingStoreBuffered
            false);
        ObjCRuntime.objc_retain(_nsWindow);

        _nsWindowDelegate = MacOSObjCClasses.CreateWindowDelegate(this);
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setDelegate:"), _nsWindowDelegate);

        SetTitle(_options.Title);

        // 无边框外观：透明标题栏 + 隐藏系统按钮（页面提供自绘控制）
        if (_options.Frameless)
        {
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), true);
            ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setTitleVisibility:"), NSWindowTitleHidden);
            HideStandardButton(NSWindowCloseButton);
            HideStandardButton(NSWindowMiniaturizeButton);
            HideStandardButton(NSWindowZoomButton);
        }

        // ---- WKWebView ----
        var config = ObjCRuntime.SendId(
            ObjCRuntime.SendId(clsWKWebViewConfiguration, ObjCRuntime.Sel("alloc")), ObjCRuntime.Sel("init"));
        var userContentController = ObjCRuntime.SendId(config, ObjCRuntime.Sel("userContentController"));

        _scriptHandler = MacOSObjCClasses.CreateScriptHandler(_messageHandler);
        ObjCRuntime.SendVoidObjObj(
            userContentController,
            ObjCRuntime.Sel("addScriptMessageHandler:name:"),
            _scriptHandler,
            ObjCRuntime.MakeNSString("oriel"));

        // 桥接脚本（document 创建时注入）
        // initWithSource: 要的是 NSString*（不是 const char*）：直接传 C# 字符串会被
        // marshal 成 char*，WebKit 内部校验对象类型时 __CF_IS_OBJC 直接 trap。
        var userScript = ObjCRuntime.SendIdObjNintBool(
            ObjCRuntime.SendId(clsWKUserScript, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            ObjCRuntime.MakeNSString(
                MacOSBridgeJs.Build(
                    _options.ConsoleForwarding,
                    _options.Zoom,
                    App.Guard.Token,
                    App.Guard.TrustedPrefixes,
                    ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label)),
            0, // WKUserScriptInjectionTimeAtDocumentStart
            true);
        ObjCRuntime.SendVoidObj(userContentController, ObjCRuntime.Sel("addUserScript:"), userScript);

        // oriel:// 的处理器必须挂在 config 上，且必须在创建 webview **之前**（见 MacOSObjCClasses）。
        if (_assets is not null)
        {
            _assetSchemeHandler = MacOSObjCClasses.CreateAssetSchemeHandler(_assets, _app.AssetHost);
            ObjCRuntime.SendVoidObjObj(
                config,
                ObjCRuntime.Sel("setURLSchemeHandler:forURLScheme:"),
                _assetSchemeHandler,
                ObjCRuntime.MakeNSString(AssetUrl.Scheme));
        }

        _webview = ObjCRuntime.SendIdDouble4Obj(
            ObjCRuntime.SendId(clsWKWebView, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithFrame:configuration:"),
            0, 0, width, height,
            config);
        ObjCRuntime.objc_retain(_webview);

        // DevTools：与 Windows 的 AreDevToolsEnabled 语义一致——只"允许"检查（Safari 的
        // 「开发」菜单里能看到这个 webview），不自动打开面板。isInspectable 是 macOS 13.3+
        // 的公开 API，更早的系统没有这个方法，故先 respondsToSelector: 探测再调用——
        // 向不认识的 selector 发消息会直接 crash。
        if (ObjCRuntime.SendBoolRetObj(_webview, ObjCRuntime.Sel("respondsToSelector:"), ObjCRuntime.Sel("setInspectable:")))
        {
            ObjCRuntime.SendVoidBool(_webview, ObjCRuntime.Sel("setInspectable:"), _options.Debug);
        }

        // 缩放是**窗口**语义（见 OrielWindowOptions.Zoom）：关掉触摸板捏合与智能缩放。
        // allowsMagnification 是 WKWebView 的公开属性（10.10+），无需能力探测；
        // Ctrl+滚轮那一路在 macOS 上不是引擎默认行为，由桥接脚本的缩放闸门兜住。
        ObjCRuntime.SendVoidBool(_webview, ObjCRuntime.Sel("setAllowsMagnification:"), _options.Zoom);

        // 内核就绪前的底色（见 OrielWindowOptions.BackgroundColor）。创建期与运行期共用
        // ApplyBackgroundColor——AppKit 那边两边都是同一组 setter。
        ApplyBackgroundColor();

        _navigationDelegate = MacOSObjCClasses.CreateNavigationDelegate(this);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setNavigationDelegate:"), _navigationDelegate);

        // UI 委托：唯一用途是 willOpenMenu:（内建右键菜单弹出前过滤）
        _uiDelegate = MacOSObjCClasses.CreateUIDelegate(this);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setUIDelegate:"), _uiDelegate);

        // 视图层级：contentView → OrielDropView（收拖放）→ WKWebView（渲染页面）。
        // 中间这层是拖放所必需的：协议方法得由"注册了 dragged types 的 view"实现，
        // 而 AppKit 会沿父视图链找到它（见 MacOSObjCClasses.BuildDropView 的说明）。
        var contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));

        _dropView = MacOSObjCClasses.CreateDropView(this);
        ObjCRuntime.SendVoidDouble4(_dropView, ObjCRuntime.Sel("setFrame:"), 0, 0, width, height);
        ObjCRuntime.SendVoidObj(contentView, ObjCRuntime.Sel("addSubview:"), _dropView);
        ObjCRuntime.SendVoidNint(_dropView, ObjCRuntime.Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewHeightSizable);

        // 只受理文件 URL："public.file-url" 是 NSPasteboardTypeFileURL 的底层值
        ObjCRuntime.SendVoidObj(
            _dropView,
            ObjCRuntime.Sel("registerForDraggedTypes:"),
            MakeStringArray(["public.file-url"]));

        // WebView 填满容器，并随容器尺寸自动调整
        ObjCRuntime.SendVoidDouble4(_webview, ObjCRuntime.Sel("setFrame:"), 0, 0, width, height);
        ObjCRuntime.SendVoidObj(_dropView, ObjCRuntime.Sel("addSubview:"), _webview);
        ObjCRuntime.SendVoidNint(_webview, ObjCRuntime.Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewHeightSizable);

        // 最小尺寸
        if (_options.MinWidth is int minWidth || _options.MinHeight is int minHeight)
        {
            ObjCRuntime.SendVoidDouble2(
                _nsWindow,
                ObjCRuntime.Sel("setContentMinSize:"),
                _options.MinWidth ?? 0,
                _options.MinHeight ?? 0);
        }

        if (_options.X is not null || _options.Y is not null)
        {
            // At(x,y)：顶左角落在屏幕逻辑坐标 (x,y)——与 Windows（CreateWindowExW 的 x/y）
            // 语义对齐。At 一次给两个值，这里按可空处理只是防御只给一维的写法。
            double maxY = ScreenMaxY();
            ObjCRuntime.SendVoidDouble2(
                _nsWindow,
                ObjCRuntime.Sel("setFrameTopLeftPoint:"),
                _options.X.GetValueOrDefault(),
                maxY - _options.Y.GetValueOrDefault());
        }
        else if (_options.Center)
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));
        }

        if (!_options.Hidden)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
            ObjCRuntime.SendVoidBool(SharedApplication(), ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
        }
        else
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("orderOut:"), 0);
        }

        if (_options.Maximized)
        {
            Maximize();
        }
        if (_options.Fullscreen)
        {
            SetFullscreen(true);
        }

        // 图标：macOS 没有窗口级图标，能设的只有应用（Dock）图标——平台事实，不是偷懒。
        // 运行期换图标走 SetIcon，两边共用 ApplyIconToApplication。
        if (!string.IsNullOrEmpty(_options.Icon))
        {
            ApplyIconToApplication(_options.Icon);
        }

        // 底色（WithBackgroundColor）在创建期由上面那处 ApplyBackgroundColor() 应用。

        Navigate();
    }

    /// <summary>把图标文件设成**应用（Dock）图标**（见 <see cref="IWindowBackend.SetIcon"/>）。</summary>
    /// <remarks>
    /// <c>initWithContentsOfFile:</c> 要的是 <c>NSString*</c>（不是 <c>char*</c>）：必须用
    /// <c>MakeNSString</c> 造对象，直接传 C# 字符串会被 marshal 成 <c>char*</c>，
    /// ObjC 侧一问类型就 trap（见 ObjCRuntime 的注释）。
    /// <para>
    /// 路径不存在时 <c>initWithContentsOfFile:</c> 返回 nil → 这里静默保持原图标，
    /// 与创建期行为一致（那边也是"设不上就不设"）。
    /// </para>
    /// </remarks>
    private static void ApplyIconToApplication(string path)
    {
        nint image = ObjCRuntime.SendIdObj(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSImage"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithContentsOfFile:"),
            ObjCRuntime.MakeNSString(path));
        if (image != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), image);
        }
    }

    /// <summary>
    /// 设置窗口底色（见 <see cref="IWindowBackend.SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 只记下运行期的值再走一遍 <see cref="ApplyBackgroundColor"/>——创建期与运行期
    /// 在 AppKit 上是同一组 setter，没有理由写两份。
    /// </remarks>
    public void SetBackgroundColor(uint argb)
    {
        _runtimeBackgroundArgb = argb;
        ApplyBackgroundColor();
    }

    /// <summary>
    /// 把当前生效的底色应用上去（见 <see cref="SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 设两处：<c>NSWindow.backgroundColor</c>（窗口底色）与（12+）
    /// <c>WKWebView.underPageBackgroundColor</c>——后者才是"页面没铺满时露出来的那块"。
    /// 老系统上取不到就只剩窗口底色。
    /// <para>
    /// <c>underPageBackgroundColor</c> 是 macOS 12 才有的选择器，对老系统发这条消息不是
    /// 抛异常而是 trap，所以先用 <c>respondsToSelector:</c> 问一句。
    /// </para>
    /// <para>
    /// 用 <c>colorWithSRGBRed:…</c> 而不是 <c>colorWithRed:…</c>：后者按**设备**色彩空间解释，
    /// 同一个数值在 P3 屏与 sRGB 屏上会显示成不同的颜色。
    /// NSColor 由类方法返回、autoreleased，不需要自己 release。
    /// </para>
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        // 运行期设过就以它为准（后设的赢）；没设过才回落到创建期选项。
        uint? effective = _runtimeBackgroundArgb;
        if (effective is null && _options.TryGetBackgroundArgb(out byte ca, out byte cr, out byte cg, out byte cb))
        {
            effective = ((uint)ca << 24) | ((uint)cr << 16) | ((uint)cg << 8) | cb;
        }

        if (effective is not { } argb || _nsWindow == 0)
        {
            return;
        }

        OrielWindowOptions.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);

        nint color = ObjCRuntime.SendIdDouble4(
            ObjCRuntime.GetClass("NSColor"),
            ObjCRuntime.Sel("colorWithSRGBRed:green:blue:alpha:"),
            r / 255.0,
            g / 255.0,
            b / 255.0,
            a / 255.0);

        if (color == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setBackgroundColor:"), color);

        if (_webview != 0
            && ObjCRuntime.SendBoolRetObj(
                _webview,
                ObjCRuntime.Sel("respondsToSelector:"),
                ObjCRuntime.Sel("setUnderPageBackgroundColor:")))
        {
            ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setUnderPageBackgroundColor:"), color);
        }
    }

    private static nint SharedApplication()
        => ObjCRuntime.SendId(ObjCRuntime.GetClass("NSApplication"), ObjCRuntime.Sel("sharedApplication"));

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            // 内嵌资源交给 WKWebView 的 oriel:// 处理器（见 MacOSAssetScheme）；兼容别名
            // https://<host>/… 在这里被归一成 oriel://，于是页面来源与 Windows/Linux 一致。
            // （旧实现把 https:// 映射成解压目录里的 file:// 本地文件：那要求资源必须落盘，
            // 而且 loadFileURL:allowingReadAccessToURL: 的读权限是"按目录"给的，多一层坑。）
            NavigateCore(AssetUrl.TryResolve(externalUrl, _app.AssetHost, out string relative)
                ? AssetUrl.ForHost(_app.AssetHost, relative)
                : externalUrl);
            return;
        }

        // 内嵌资产首页
        if (_assets is not null)
        {
            NavigateCore(AssetUrl.DefaultDocument(_app.AssetHost));
        }
    }

    private void NavigateCore(string url)
    {
        // URLWithString: 要 NSString*，不是 char*（把 char* 当 NSString* 传会在
        // CoreFoundation 的 __CF_IS_OBJC 里 trap，真机 CI 上实测过）。
        var nsUrl = ObjCRuntime.SendIdObj(
            ObjCRuntime.GetClass("NSURL"),
            ObjCRuntime.Sel("URLWithString:"),
            ObjCRuntime.MakeNSString(url));
        var request = ObjCRuntime.SendIdObj(ObjCRuntime.GetClass("NSURLRequest"), ObjCRuntime.Sel("requestWithURL:"), nsUrl);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("loadRequest:"), request);
    }

    private void HideStandardButton(nint buttonKind)
    {
        var button = ObjCRuntime.SendIdNint(_nsWindow, ObjCRuntime.Sel("standardWindowButton:"), buttonKind);
        if (button != 0)
        {
            ObjCRuntime.SendVoidBool(button, ObjCRuntime.Sel("setHidden:"), true);
        }
    }

    // ------------------------------------------------------------------
    // 生命周期回调（由 trampoline 调用，UI 线程）
    // ------------------------------------------------------------------

    /// <summary>
    /// Cocoa <c>windowShouldClose:</c>：返回 true 表示**允许**关闭。
    /// </summary>
    /// <remarks>
    /// 与 Linux 的 <c>delete-event</c> 相反（那边 true = 阻止），是本平台内的翻译点。
    /// </remarks>
    internal bool OnWindowShouldClose() => DecideClose() == OrielCloseDecision.Proceed;

    /// <summary>
    /// 判定一次关闭请求（宿主否决 → 页面接管 → 放行）。
    /// </summary>
    /// <remarks>
    /// "页面可决定"要求页面已开启接管**且已加载完成**：那条 <c>close-requested</c>
    /// 送不到未加载的页面，挂起只会让窗口变成点 X 没反应。
    /// </remarks>
    internal OrielCloseDecision DecideClose()
    {
        // 强制销毁途中不再问任何人：Destroy() 之后的任何关闭请求都直接放行，
        // 否则"页面接管"会把强制销毁也拦下来（那与 Destroy 的语义直接冲突）。
        if (_forceDestroy)
        {
            return OrielCloseDecision.Proceed;
        }

        return OrielClosePipeline.Decide(
            Closing,
            pageCanDecide: CloseInterceptEnabled && _pageReady,
            emitCloseRequested: () => PushEventOnUi(OrielClosePipeline.CloseRequestedEventName, "null"));
    }

    internal void OnWindowWillClose()
    {
        // 先清理状态注册表与委托指针，再触发 Closed——避免用户在回调里发起 IPC 时
        // 走到已销毁的宿主，也避免实例指针被复用时把事件路由到旧宿主（ABA）。
        // 注册表持有的是托管 host 的强引用，不清理则整棵对象树永不释放。
        MacOSObjCClasses.RemoveWindowDelegate(_nsWindowDelegate);
        MacOSObjCClasses.RemoveNavigationDelegate(_navigationDelegate);
        MacOSObjCClasses.RemoveScriptHandler(_scriptHandler);
        MacOSObjCClasses.RemoveDropView(_dropView);
        MacOSObjCClasses.RemoveUIDelegate(_uiDelegate);
        MacOSObjCClasses.RemoveAssetSchemeHandler(_assetSchemeHandler);
        _assetSchemeHandler = 0;
        _nsWindowDelegate = 0;
        _navigationDelegate = 0;
        _scriptHandler = 0;
        _dropView = 0;
        _uiDelegate = 0;

        // 保留的上下文菜单一并释放：MacOSMenu.s_handlers 的条目与 ObjC handler 实例
        // 不释放会滞留到进程结束（注册表同样被 GCHandle 根住）。
        _contextMenu?.Dispose();
        _contextMenu = null;

        // 页面回环随文档一起消失：挂起的 ExecuteScriptAsync 在这里立刻失败，
        // 而不是让调用方的 await 永不返回。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：窗口已销毁，页面回环不会再返回结果。");

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    internal void OnNavigationStarted()
    {
        // 导航开始即原文档开始卸载：它注入的回环脚本不会再回执，先清掉挂起的 eval。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：页面正在离开原文档，回环结果不会再返回。");

        RaiseNavigationStarting(CurrentUri());
    }

    /// <summary>
    /// 导航结束（成功或失败）。失败由 WKNavigationDelegate 的 didFail* 回调传来，
    /// 带 NSError 压成的一行文本。
    /// </summary>
    internal void OnNavigationCompleted(bool success, string? error = null)
    {
        if (success)
        {
            // 新文档不知道当前最大化状态，且下面的 Loaded 只触发首次——所以补推放在早退之前，
            // 每次导航完成都无条件补推一次当前值（与 Windows 的 OnNavigationCompleted 对齐）。
            _pageReady = true;
            SyncMaximizedState(force: true);
        }

        // 失败时 webView.URL 仍然指向旧文档（取不到失败地址），所以失败一律给空串 URL。
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(
            success, success ? CurrentUri() : string.Empty, error));
        if (!success)
        {
            return;
        }

        if (!_loadedRaised)
        {
            _loadedRaised = true;
            Loaded?.Invoke();

            // 文档标题 → 窗口标题
            var title = ObjCRuntime.ToManagedString(ObjCRuntime.SendId(_webview, ObjCRuntime.Sel("title")));
            if (!string.IsNullOrEmpty(title))
            {
                RaiseTitleChanged(title);
            }
        }
    }

    // ------------------------------------------------------------------
    // IWindowBackend
    // ------------------------------------------------------------------

    public void Show() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
    public void Hide() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("orderOut:"), 0);

    public void Close() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("performClose:"), 0);

    /// <summary>强制销毁窗口：绕过 <c>Closing</c> 与页面接管（对标 Tauri 2 的 <c>destroy</c>）。</summary>
    /// <remarks>
    /// <b>必须置 <c>_forceDestroy</c> 再 <c>close</c></b>：Cocoa 没有"直接销毁"的入口，
    /// <c>close</c> 一定会先问委托的 <c>windowShouldClose:</c>。那条路是我们自己接的，
    /// 所以让 <see cref="DecideClose"/> 在强制销毁时直接放行即可——
    /// 这与 Windows/Linux 用"不发 WM_CLOSE / 不走 delete-event"达到的效果相同。
    /// </remarks>
    public void Destroy()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        _forceDestroy = true;
        ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("close"));
    }

    /// <summary>是否让页面接管关闭；初值取自 <see cref="OrielWindowOptions.InterceptClose"/>。</summary>
    public bool CloseInterceptEnabled { get; set; }

    /// <summary>哪几类宿主事件转发给页面；默认全关（见 <see cref="OrielWindowEventKind"/>）。</summary>
    public OrielWindowEventKind EventForwarding { get; set; }

    /// <summary>
    /// 窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。
    /// </summary>
    /// <remarks>
    /// 取 <c>backingScaleFactor</c>（Retina 上为 2.0）。窗口 API 收的是**点**，
    /// 点与逻辑像素一一对应，所以换算 <c>PhysicalSize</c> 时除以它正是想要的。
    /// 取 <c>window.screen</c> 而不是 <c>NSScreen.mainScreen</c>：后者永远是主屏，
    /// 窗口被拖到副屏（不同缩放）时会给错倍数。
    /// </remarks>
    public double ScaleFactor
    {
        get
        {
            if (_nsWindow == 0)
            {
                return 1.0;
            }

            nint screen = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("screen"));
            if (screen == 0)
            {
                return 1.0;
            }

            double scale = ObjCRuntime.SendDouble(screen, ObjCRuntime.Sel("backingScaleFactor"));
            return scale > 0 ? scale : 1.0;
        }
    }

    /// <summary>
    /// 客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。
    /// </summary>
    /// <remarks>
    /// 读 <c>contentView.frame</c> 而不是整窗 frame 减标题栏：contentView 就是客户区，
    /// 无边框（FullSizeContentView）与装饰窗口下它都给出正确答案，而"减标题栏"要猜高度。
    /// 注意 macOS 的"点"与逻辑像素一一对应，所以**不**需要除缩放比。
    /// </remarks>
    public OrielSize InnerSize
    {
        get
        {
            if (ReadContentFrame() is not { } content)
            {
                return default;
            }

            return new OrielSize(content.Width, content.Height);
        }
    }

    /// <summary>整窗尺寸（逻辑像素）；见 <see cref="IWindowBackend.OuterSize"/>。</summary>
    public OrielSize OuterSize
    {
        get
        {
            if (_nsWindow == 0)
            {
                return default;
            }

            (_, _, double width, double height) = ReadFrame();
            return new OrielSize(width, height);
        }
    }

    /// <summary>
    /// 窗口左上角（逻辑像素、左上角原点）；见 <see cref="IWindowBackend.OuterPosition"/>。
    /// </summary>
    /// <remarks>
    /// cocoa 的原点在左下、y 向上，而公共契约是左上角原点——所以按主屏高度换算：
    /// <c>y_app = maxY - (origin.y + height)</c>。与 <c>ReportPosition</c> 用的是同一套换算。
    /// </remarks>
    public OrielPosition OuterPosition
    {
        get
        {
            if (_nsWindow == 0)
            {
                return default;
            }

            (double originX, double originY, _, double height) = ReadFrame();
            return new OrielPosition(originX, ScreenMaxY() - (originY + height));
        }
    }

    /// <summary>
    /// 客户区左上角（逻辑像素、左上角原点）；见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不需要 <c>contentRectForFrameRect:</c>：**contentView 锚在窗口的左下角**
    /// （cocoa 的 y 向上，窗口底边就是内容区底边），所以内容区顶边 = <c>frame.origin.y + contentHeight</c>。
    /// 两者之差正好是标题栏高度。装饰窗口与无边框窗口（<c>StyleFullSizeContentView</c>）
    /// 都成立——后者的 contentHeight 等于 frameHeight，于是内外位置相等。
    /// </para>
    /// <para>
    /// 那个 ObjC 方法的返回值是 NSRect 结构体，msgSend 直取要走 stret（x86_64）/ 隐式 sret（arm64），
    /// 绑定里没有那条路。上面的算法用两次已有的 frame 读取就得出同样结果，没有理由再引一层结构体传参。
    /// </para>
    /// </remarks>
    public OrielPosition InnerPosition
    {
        get
        {
            if (_nsWindow == 0 || ReadContentFrame() is not { } content)
            {
                return OuterPosition;
            }

            (double originX, double originY, _, _) = ReadFrame();
            return new OrielPosition(originX, ScreenMaxY() - (originY + content.Height));
        }
    }

    /// <summary>读 contentView 的 frame（客户区，cocoa 点）；取不到时返回 null。</summary>
    private (double X, double Y, double Width, double Height)? ReadContentFrame()
    {
        if (_nsWindow == 0)
        {
            return null;
        }

        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        if (contentView == 0)
        {
            return null;
        }

        nint boxed = ObjCRuntime.SendIdObj(
            contentView, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return null;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 0)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24)));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    public void Focus()
    {
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
        ObjCRuntime.SendVoidBool(SharedApplication(), ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
    }

    public void Maximize() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("zoom:"));
    public void Minimize() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("miniaturize:"));

    public void Restore()
    {
        if (ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed")))
        {
            Maximize(); // zoom: 为切换语义，zoomed 状态下再次调用即还原
        }
        else
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("deminiaturize:"));
        }
    }

    public bool ToggleMaximize()
    {
        Maximize(); // NSWindow zoom: 本身就是切换语义
        // zoom: 是同步生效的，这里读到的就是切换后的真实值（与 Linux 的 GTK 异步语义不同）。
        // 统一走 SyncMaximizedState：既更新托管事件，也把事件推给页面。
        return SyncMaximizedState(force: true);
    }

    /// <summary>
    /// 读取当前最大化状态（NSWindow isZoomed），与上次上报值比对，变化时（或 <paramref name="force"/> 时）
    /// 上报给托管事件与页面。调用时机：ToggleMaximize（zoom: 同步生效）、windowDidResize（原生 Zoom 路径）、
    /// 每次导航完成。
    /// </summary>
    internal bool SyncMaximizedState(bool force = false)
    {
        bool isMaximized = IsMaximized;
        if (!force && isMaximized == _wasMaximized)
        {
            return isMaximized;
        }

        _wasMaximized = isMaximized;
        MaximizedChanged?.Invoke(isMaximized);
        PushEventOnUi("maximized", isMaximized ? "true" : "false");
        return isMaximized;
    }

    /// <summary>
    /// 向页面推送一个 oriel 事件（页面侧 window.oriel.on(name, handler) 接收）。
    /// 与 PostWebMessageOnUi 的回执通道分开——那条只认 {"id","ok"} 回执形态。
    /// 页面未就绪（桥接脚本尚未注入）时直接丢弃：加载完成会强制补推当前状态，不会丢状态。
    /// </summary>
    private void PushEventOnUi(string name, string jsonValue)
    {
        if (_webview == 0 || !_pageReady)
        {
            return;
        }
        try
        {
            var js = "window.oriel._onEvent(" + JsonText.EncodeString(name) + ", " + jsonValue + ")";
            ObjCRuntime.SendVoidObjNint(
                _webview,
                ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                ObjCRuntime.MakeNSString(js),
                0);
        }
        catch
        {
            // 窗口销毁后的迟到事件，忽略
        }
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled != _isFullscreen)
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("toggleFullScreen:"));
            _isFullscreen = enabled;
        }
    }

    /// <summary>
    /// 原生全屏路径回灌（绿钮、ESC、系统菜单的进出全屏不经过 <see cref="SetFullscreen"/>）。
    /// </summary>
    /// <remarks>
    /// 不回灌的话 <c>_isFullscreen</c> 会与真实状态相反，下一次 Toggle/SetFullscreen 行为颠倒——
    /// Linux 由 window-state-event 回灌（SyncFullscreenState）、Windows 全屏只经 API 发起
    /// 无此问题，macOS 是最后一个缺口（评审 P2-9）。
    /// </remarks>
    internal void OnNativeFullscreenChanged(bool isFullscreen)
    {
        if (_isFullscreen != isFullscreen)
        {
            _isFullscreen = isFullscreen;
        }
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setLevel:"), enabled ? NSFloatingWindowLevel : 0);
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    /// <summary>当前窗口标题（见 <see cref="IWindowBackend.Title"/>）。</summary>
    public string Title => _title;

    public void SetTitle(string title)
    {
        _title = title;
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString(title));
    }

    public void SetResizable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled ? (nint)((nuint)style | StyleResizable) : (nint)((nuint)style & ~StyleResizable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setContentMinSize:"), width, height);
    }

    /// <summary>
    /// 设置是否可最大化（见 <see cref="IWindowBackend.SetMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>在 macOS 上这就是改 <c>NSWindowStyleMaskResizable</c></b>，与
    /// <see cref="SetResizable"/> 是同一个操作——AppKit 根本没有独立的最大化位，
    /// 缩放按钮可用与否完全由 resizable 位决定（<see cref="IsMaximizable"/>
    /// 读的也是这一位，所以"写进去能读回来"成立）。
    /// <para>
    /// 副作用要说清楚：调 <c>setMaximizable(false)</c> 会**同时**让窗口不可拖拽缩放。
    /// 这是 AppKit 的模型，不是本库的取舍——Tauri 在 macOS 上也是同一落点。
    /// 迁移代码若同时调过 <c>setResizable</c> 和 <c>setMaximizable</c>，
    /// 在 macOS 上后调的那个赢。
    /// </para>
    /// </remarks>
    public void SetMaximizable(bool enabled) => SetResizable(enabled);

    /// <summary>设置是否可最小化（切 <c>NSWindowStyleMaskMiniaturizable</c>，见 <see cref="IWindowBackend.SetMinimizable"/>）。</summary>
    public void SetMinimizable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled
            ? (nint)((nuint)style | StyleMiniaturizable)
            : (nint)((nuint)style & ~StyleMiniaturizable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }

    /// <summary>
    /// 设置窗口图标（见 <see cref="IWindowBackend.SetIcon"/>）。
    /// </summary>
    /// <remarks>
    /// <b>macOS 上没有窗口级图标</b>，能设的只有应用（Dock）图标——所以这个调用会影响
    /// 整个应用而不只是本窗口。这是平台事实（AppKit 的模型），本库照实实现并写明，
    /// 而不是假装它只作用于本窗口。
    /// <para>
    /// 与创建期图标共用 <see cref="ApplyIconToApplication"/>：AppKit 那边也是同一个 API。
    /// </para>
    /// </remarks>
    public void SetIcon(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ApplyIconToApplication(path);
    }

    /// <summary>
    /// 设置是否可关闭（切 <c>NSWindowStyleMaskClosable</c>，见 <see cref="IWindowBackend.SetClosable"/>）。
    /// </summary>
    /// <remarks>
    /// 与 Windows 的落点（灰化系统菜单项）不同，这里就是<b>直接切样式位</b>——
    /// 红色关闭按钮会随位消失/出现。之所以两边落点不同，是因为 AppKit 确实有这个位
    /// 而 Win32 没有（Win32 上"按钮"属于系统菜单，见 <see cref="IsClosable"/> 的说明）。
    /// <para>
    /// 不变量"写进去能读回来"成立：<see cref="IsClosable"/> 读的就是这一位。
    /// </para>
    /// </remarks>
    public void SetClosable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled
            ? (nint)((nuint)style | StyleClosable)
            : (nint)((nuint)style & ~StyleClosable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }

    public void MoveTo(int x, int y)
    {
        // API.md 的契约：屏幕坐标（应用像素、**左上角**原点）。cocoa 是左下角原点且 y 向上，
        // 顶左点 (x, y_app) 的 cocoa Y = maxY - y_app；setFrameTopLeftPoint 直接按顶左语义
        // 落位，连窗口高度都不需要。（旧实现把 x/y 当左下角坐标原样喂给 setFrameOrigin:，
        // 与公共契约相反——同文件 BeginDragStreaming 的 sh - wy - wh 才是契约本意。）
        double maxY = ScreenMaxY();
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setFrameTopLeftPoint:"), x, maxY - y);
    }

    /// <summary>
    /// 主屏高度的上沿（cocoa 点、y 向上坐标系的最大 Y）。"左上角原点"契约换算成 cocoa
    /// 坐标时要用它。NSRect 是结构体：msgSend 直取要走 stret（x86_64）/ 隐式 sret（arm64），
    /// 绑定里没有那条路——改走 KVC：<c>valueForKey:@"frame"</c> 得到 NSValue，再
    /// <c>getValue:</c> 按指针读出四个 double（origin.x/y、size.w/h）。
    /// </summary>
    private double ScreenMaxY()
    {
        nint screen = ObjCRuntime.SendId(ObjCRuntime.GetClass("NSScreen"), ObjCRuntime.Sel("mainScreen"));
        nint boxed = screen == 0
            ? 0
            : ObjCRuntime.SendIdObj(screen, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return 0;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            // Marshal 没有 ReadDouble：按 8 字节整数读出再按位转回 double（IEEE 754 直通）
            return BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8))    // origin.y
                 + BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24)); // size.height
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    // ------------------------------------------------------------------
    // 尺寸 / 位置 / 焦点上报（对标 Tauri 2 的 onResized / onMoved / onFocusChanged）
    // ------------------------------------------------------------------

    /// <summary>
    /// 读窗口的 cocoa frame（origin.x、origin.y、size.width、size.height，单位 cocoa 点）。
    /// </summary>
    /// <remarks>
    /// 走 KVC + <c>getValue:</c> 而不是直取 <c>frame</c> 的返回值：NSRect 是结构体，
    /// msgSend 直取在 x86_64 上要走 stret、arm64 上是隐式 sret，绑定里没有那条路。
    /// 与 <see cref="ScreenMaxY"/> 用的是同一套读法（它读的是 NSScreen 的 frame）。
    /// </remarks>
    private (double X, double Y, double Width, double Height) ReadFrame()
    {
        nint boxed = ObjCRuntime.SendIdObj(_nsWindow, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return (0, 0, 0, 0);
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 0)),   // origin.x
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8)),   // origin.y
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16)),  // size.width
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24))); // size.height
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 上报客户区尺寸（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 用 <c>contentLayoutRect</c> 的等价量：先取整窗 frame 的 size，再减去标题栏高度。
    /// 但更可靠的是直接问 <c>contentView</c> 的 frame——它在无边框（FullSizeContentView）
    /// 与装饰窗口下都给出**客户区**，而整窗 frame 在无边框下等于客户区、装饰下多一圈标题栏。
    /// </remarks>
    internal void ReportGeometry()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        // 窗口几何变了就顺带看一眼缩放比：窗口被拖到不同缩放的显示器时，
        // windowDidResize: / windowDidMove: 至少有一条会发（见下面 ReportScaleFactorIfChanged 的说明）。
        ReportScaleFactorIfChanged();

        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        if (contentView == 0)
        {
            return;
        }

        // contentView 的 frame 直接就是客户区尺寸（cocoa 点 = 逻辑像素，macOS 不需要额外折算）
        nint boxed = ObjCRuntime.SendIdObj(contentView, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            double width = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16));
            double height = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24));
            ReportSize(width, height);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 上报窗口位置（逻辑像素、**左上角原点**）。
    /// </summary>
    /// <remarks>
    /// cocoa 的原点在左下、y 向上，而公共契约是左上角原点——所以要按主屏高度换算：
    /// <c>y_app = maxY - (origin.y + height)</c>。这与 <see cref="MoveTo"/> 的换算是同一套
    /// （那边是 <c>setFrameTopLeftPoint</c> 直接吃左上角语义，方向相反但依据相同）。
    /// </remarks>
    /// <summary>
    /// 缩放比变了就上报（见 <see cref="IWindowBackend.ScaleFactorChanged"/>）。
    /// </summary>
    /// <remarks>
    /// <b>触发点是 <c>windowDidResize:</c> / <c>windowDidMove:</c>（经 ReportGeometry / ReportPosition），
    /// 而不是一条专门的通知。</b>窗口被拖到不同缩放的显示器上时这两条至少有一条会发，
    /// 所以"跨屏移动"这个主要场景被覆盖了，而且不用再引入一个通知观察者。
    /// <para>
    /// <b>已知的漏报场景</b>：在"系统设置 → 显示器"里只改缩放、窗口完全不动时，这两条回调都不发，
    /// 页面就收不到 <c>scale.changed</c>。要覆盖它得观察
    /// <c>NSWindowDidChangeBackingPropertiesNotification</c>；这里如实记下这个缺口，
    /// 而不是假装它是完整的。
    /// </para>
    /// <para>
    /// payload 里的 <c>size</c> 是**物理像素**客户区（与 Tauri 的 <c>PhysicalSize</c> 对齐）。
    /// </para>
    /// </remarks>
    internal void ReportScaleFactorIfChanged()
    {
        double scale = ScaleFactor;
        if (_lastReportedScaleFactor is { } previous && Math.Abs(previous - scale) < 0.0001)
        {
            return;
        }

        _lastReportedScaleFactor = scale;
        ScaleFactorChanged?.Invoke(scale);

        if ((EventForwarding & OrielWindowEventKind.Scale) != 0)
        {
            OrielSize inner = InnerSize;
            PushEventOnUi(
                "scale.changed",
                OrielEventPayload.ScaleFactor(scale, inner.Width * scale, inner.Height * scale));
        }
    }

    internal void ReportPosition()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        (double originX, double originY, _, double height) = ReadFrame();
        double maxY = ScreenMaxY();
        ReportPosition(maxY - (originY + height), originX);
    }

    /// <summary>上报位置（已换算成左上角原点）；与上次相同则跳过。</summary>
    private void ReportPosition(double x, double y)
    {
        if (_lastReportedPosition is { } previous
            && Math.Abs(previous.X - x) < 0.5
            && Math.Abs(previous.Y - y) < 0.5)
        {
            return;
        }

        _lastReportedPosition = (x, y);
        Moved?.Invoke(new OrielPositionChangedEventArgs(x, y));

        // 页面侧转发是按需的（默认关）：拖动时这条路径每个像素走一次，
        // 而这里的推送是一次 JS 求值。见 OrielWindowEventKind。
        if ((EventForwarding & OrielWindowEventKind.Move) != 0)
        {
            PushEventOnUi("move", OrielEventPayload.Position(x, y));
        }
    }

    /// <summary>上报客户区尺寸；与上次相同则跳过。</summary>
    /// <remarks>
    /// 去重是必须的：拖边框时 <c>windowDidResize:</c> 每个像素来一次，
    /// 每次都触发托管事件 + 页面 IPC 会把一次拖动变成流量风暴。
    /// </remarks>
    private void ReportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_lastReportedSize is { } previous
            && Math.Abs(previous.Width - width) < 0.5
            && Math.Abs(previous.Height - height) < 0.5)
        {
            return;
        }

        _lastReportedSize = (width, height);
        Resized?.Invoke(new OrielSizeChangedEventArgs(width, height));

        if ((EventForwarding & OrielWindowEventKind.Resize) != 0)
        {
            PushEventOnUi("resize", OrielEventPayload.Size(width, height));
        }
    }

    /// <summary>上报焦点变化（与上次相同则跳过；首个事件一定会报）。</summary>
    internal void ReportFocus(bool focused)
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

    public void Resize(int width, int height)
    {
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setContentSize:"), width, height);
    }

    public void Center() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 取值是"取不到就返回 0"的形式（框架未加载时类查不到），由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底。注意 <c>+[NSEvent doubleClickInterval]</c>
    /// 给的是**秒**，注入给页面的是毫秒。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
    {
        nint nsEventClass = ObjCRuntime.GetClass("NSEvent");
        double intervalSeconds = nsEventClass == 0
            ? 0
            : ObjCRuntime.SendDouble(nsEventClass, ObjCRuntime.Sel("doubleClickInterval"));

        return OrielSystemSnapshot.Normalize((int)Math.Round(intervalSeconds * 1000));
    }

    public void BeginDrag()
    {
        // macOS 无边框拖动由 JS 流式坐标驱动（win.dragStart 提供起点，DragTo 应用增量）。
    }

    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
    {
        // 屏幕 CSS 点 == cocoa 点（1:1）。窗口左上角 (wx, wy) → cocoa 左下角：
        _dragPointerStart = (px, py);
        _cocoaX = wx;
        _cocoaY = sh - wy - wh;
        _dragWindowOrigin = (_cocoaX, _cocoaY);
    }

    public void DragTo(double pointerDx, double pointerDy)
    {
        if (_dragPointerStart is null || _dragWindowOrigin is null)
        {
            return;
        }
        _cocoaX = _dragWindowOrigin.Value.X + pointerDx;
        _cocoaY = _dragWindowOrigin.Value.Y - pointerDy; // 屏幕 y 向下 = cocoa y 向上
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setFrameOrigin:"), _cocoaX, _cocoaY);
    }

    public void EndDrag()
    {
        _dragPointerStart = null;
        _dragWindowOrigin = null;
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _evalSeq);
        _messageHandler.RegisterEval(id, completion);

        // 页内求值并把结果经消息处理器回传（避免 ObjC block 完成回调）
        var scriptJson = JsonText.EncodeString(script);
        var js = "window.oriel._evalScriptDone(" + id + ", JSON.stringify((function(){try{return eval(" + scriptJson +
                 ")}catch(e){return 'E:'+String(e)}})()))";
        PostToMainThread(() =>
        {
            try
            {
                ObjCRuntime.SendVoidObjNint(
                    _webview,
                    ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                    ObjCRuntime.MakeNSString(js),
                    0);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    public void PostMessageAsJson(string json) => PostWebMessageOnUi(json);

    internal void PostWebMessageOnUi(string json)
    {
        try
        {
            if (_webview == 0)
            {
                return;
            }
            // 把回执**原样**求值进页面：`_onResult` 现在收整个回执对象。
            //
            // 早先是宿主侧把回执拆成 `_onResult(id, ok, payload)`——成功取值、失败取文案。
            // 改成整对象传是为了让 code / data 自然流过：拆的话三平台各要抽一遍、
            // 各可能漏一个字段，而漏掉的表现是"某个平台拿不到错误码"。
            //
            // 直接把 JSON 嵌进 JS 表达式是安全的：JSON 字面量本身就是合法的 JS 表达式，
            // 而这份 JSON 由宿主自己的 Utf8JsonWriter 产出（不是拼接字符串）。
            var js = $"window.oriel._onResult({json})";
            ObjCRuntime.SendVoidObjNint(
                _webview,
                ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                ObjCRuntime.MakeNSString(js),
                0);
        }
        catch
        {
            // 窗口销毁后的迟到回执，忽略
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        TitleChanged?.Invoke(title);
    }

    internal void RaiseNavigationStarting(string url)
    {
        // 记下当前文档：入站 IPC 要做来源校验，而消息本身不带来源，
        // 只能由"这个窗口现在停在哪个 URL"来回答。
        _currentUrl = url;
        NavigationStarting?.Invoke(url);
        PushEventOnUi("navigation.starting", $"{{\"url\":{JsonText.EncodeString(url)}}}");
    }

    /// <summary>当前文档的 URL（导航开始时更新）。入站 IPC 的来源校验用它。</summary>
    internal string? CurrentUrl => _currentUrl;

    private string? _currentUrl;

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

    /// <summary>当前文档 URL（NSURL 的 absoluteString）；销毁后或取不到时返回空串。</summary>
    private string CurrentUri()
    {
        if (_webview == 0)
        {
            return string.Empty;
        }

        nint url = ObjCRuntime.SendId(_webview, ObjCRuntime.Sel("URL"));
        if (url == 0)
        {
            return string.Empty;
        }

        return ObjCRuntime.ToManagedString(ObjCRuntime.SendId(url, ObjCRuntime.Sel("absoluteString"))) ?? string.Empty;
    }

    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        // NSAlert：alert = [[NSAlert alloc] init]; setAlertStyle; setMessageText; setInformativeText; runModal
        var alert = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSAlert"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));
        var style = icon switch
        {
            OrielMessageBoxIcon.Warning => 1, // NSAlertStyleWarning
            OrielMessageBoxIcon.Error => 2,   // NSAlertStyleCritical
            _ => 0,                            // NSAlertStyleInformational
        };
        ObjCRuntime.SendVoidNint(alert, ObjCRuntime.Sel("setAlertStyle:"), style);
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setMessageText:"), ObjCRuntime.MakeNSString(title ?? _title));
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setInformativeText:"), ObjCRuntime.MakeNSString(text));
        _ = ObjCRuntime.SendId(alert, ObjCRuntime.Sel("runModal"));
    }

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cocoa 这边**支持自定义按钮文案**（<c>addButtonWithTitle:</c> 直接收文本），
    /// 所以 Windows 的限制在 macOS 上不存在。
    /// </para>
    /// <para>
    /// <c>addButtonWithTitle:</c> 的返回值是<b>按钮索引 + 1</b>（1 = 第一个按钮），
    /// 而 <c>runModal</c> 返回的 <c>NSModalResponse</c> 用的就是同一套编号。
    /// 所以顺序必须与下面的映射一致，而"哪个是肯定性按钮"由**添加顺序**决定
    /// （第一个添加的也是回车默认触发的那个）。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        nint alert = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSAlert"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));

        if (alert == 0)
        {
            return null;
        }

        int style = kind switch
        {
            "warning" => 1,  // NSAlertStyleWarning
            "error" => 2,    // NSAlertStyleCritical
            _ => 0,          // NSAlertStyleInformational
        };

        ObjCRuntime.SendVoidNint(alert, ObjCRuntime.Sel("setAlertStyle:"), style);
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setMessageText:"), ObjCRuntime.MakeNSString(title ?? _title));
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setInformativeText:"), ObjCRuntime.MakeNSString(message));

        AddButton(alert, okLabel);

        if (buttons is MessageButtons.OkCancel or MessageButtons.YesNo or MessageButtons.YesNoCancel)
        {
            AddButton(alert, cancelLabel);
        }

        if (buttons is MessageButtons.YesNoCancel)
        {
            // 第三个按钮没有独立文案参数（Tauri 的 yes/no/cancel 也没有）——用惯例的"取消"
            AddButton(alert, "取消");
        }

        nint response = ObjCRuntime.SendId(alert, ObjCRuntime.Sel("runModal"));

        // 响应码 = 按钮索引 + 1。第 1 个按钮的语义取决于按钮集合：
        // Ok / OkCancel 是"确认"，YesNo / YesNoCancel 是"是"。
        bool firstIsOk = buttons is MessageButtons.Ok or MessageButtons.OkCancel;

        return response switch
        {
            1 => firstIsOk ? MessageResult.Ok : MessageResult.Yes,
            // 第 2 个按钮：OkCancel 是"取消"，YesNo 与 YesNoCancel 都是"否"。
            // **不能把 YesNoCancel 的第 2 个当成取消**：调用方要能区分"用户选了否"
            // 与"用户放弃了"，而那正是三按钮的用途。
            2 when buttons is MessageButtons.OkCancel => MessageResult.Cancel,
            2 => MessageResult.No,
            3 => MessageResult.Cancel,
            _ => null,                    // 被外部关掉（Esc / 关窗）时没有结果
        };
    }

    private static void AddButton(nint alert, string title)
        => _ = ObjCRuntime.SendIdObj(alert, ObjCRuntime.Sel("addButtonWithTitle:"), ObjCRuntime.MakeNSString(title));

    // ---- 文件对话框（NSOpenPanel / NSSavePanel）----

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
    {
        nint panel = CreatePanel(isSave: false, options.Title, options.Filters, options.InitialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseFiles:"), true);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseDirectories:"), false);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setAllowsMultipleSelection:"), options.AllowMultiple);

        return RunPanel(panel);
    }

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        nint panel = CreatePanel(isSave: true, options.Title, options.Filters, options.InitialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setExtensionHidden:"), true);

        if (!string.IsNullOrWhiteSpace(options.DefaultFileName))
        {
            // 预填文件名；扩展名不在这里补——Cocoa 由 allowedFileTypes 与系统行为共同决定
            ObjCRuntime.SendVoidObj(
                panel,
                ObjCRuntime.Sel("setNameFieldStringValue:"),
                ObjCRuntime.MakeNSString(options.DefaultFileName));
        }

        string[] paths = RunPanel(panel);
        return paths.Length > 0 ? paths[0] : null;
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        nint panel = CreatePanel(isSave: false, title, filters: null, initialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseFiles:"), false);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseDirectories:"), true);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setAllowsMultipleSelection:"), false);

        string[] paths = RunPanel(panel);
        return paths.Length > 0 ? paths[0] : null;
    }

    /// <summary>建面板并做三平台共通的配置（标题、过滤器、初始目录）。</summary>
    private static nint CreatePanel(bool isSave, string? title, IReadOnlyList<OrielFileFilter>? filters, string? initialDirectory)
    {
        var clsName = isSave ? "NSSavePanel" : "NSOpenPanel";
        nint panel = ObjCRuntime.SendId(ObjCRuntime.GetClass(clsName), ObjCRuntime.Sel(isSave ? "savePanel" : "openPanel"));
        if (panel == 0)
        {
            return 0;
        }

        if (title is not null)
        {
            ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setMessage:"), ObjCRuntime.MakeNSString(title));
        }

        // 空列表意味着"任意文件"，此时**不设置** allowedFileTypes（塞一个 "*" 进去会把面板
        // 锁成只能选无扩展名文件——见 OrielFileFilter.CocoaExtensions 的说明）
        IReadOnlyList<string> extensions = OrielFileFilter.CocoaExtensions(filters ?? []);
        if (extensions.Count > 0)
        {
            // allowedFileTypes 已在 macOS 12 起标废弃，但仍是最省事的路径：
            // 现代替代是 allowedContentTypes + UTType，需要额外框架与 AOT 友好的绑定，记在 ROADMAP
            ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setAllowedFileTypes:"), MakeStringArray(extensions));
        }

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            nint url = ObjCRuntime.SendIdObj(
                ObjCRuntime.GetClass("NSURL"),
                ObjCRuntime.Sel("fileURLWithPath:"),
                ObjCRuntime.MakeNSString(initialDirectory));
            if (url != 0)
            {
                ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setDirectoryURL:"), url);
            }
        }

        return panel;
    }

    /// <summary>托管字符串数组 → NSArray（经 NSMutableArray 逐项 <c>addObject:</c>）。</summary>
    private static nint MakeStringArray(IReadOnlyList<string> items)
    {
        nint array = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSMutableArray"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));

        foreach (string item in items)
        {
            ObjCRuntime.SendVoidObj(array, ObjCRuntime.Sel("addObject:"), ObjCRuntime.MakeNSString(item));
        }

        return array;
    }

    /// <summary>
    /// 运行面板并收集**全部**选中路径，取消返回空数组。
    /// </summary>
    /// <remarks>
    /// 走 <c>URLs</c> 数组而不是 <c>URL</c>：<c>URL</c> 只反映"最后/唯一选中项"，
    /// 多选时拿不全。逐项 <c>objectAtIndex:</c> 与单选共用同一条路径，因此单选也走这里。
    /// </remarks>
    private static string[] RunPanel(nint panel)
    {
        if (panel == 0 || ObjCRuntime.SendId(panel, ObjCRuntime.Sel("runModal")) != 1) // NSModalResponseOK
        {
            return [];
        }

        nint urls = ObjCRuntime.SendId(panel, ObjCRuntime.Sel("URLs"));
        if (urls == 0)
        {
            return [];
        }

        int count = (int)ObjCRuntime.SendId(urls, ObjCRuntime.Sel("count"));
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            nint url = ObjCRuntime.SendIdNint(urls, ObjCRuntime.Sel("objectAtIndex:"), i);
            if (url == 0)
            {
                continue;
            }

            nint nsPath = ObjCRuntime.SendId(url, ObjCRuntime.Sel("path"));
            if (nsPath != 0 && ObjCRuntime.ToManagedString(nsPath) is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return [.. paths];
    }
}
