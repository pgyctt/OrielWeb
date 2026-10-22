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

    /// <summary>
    /// 是否由 API（<see cref="SetTitle"/>）显式设过标题。
    /// </summary>
    /// <remarks>
    /// 置位后不再让页面 <c>&lt;title&gt;</c> 覆盖窗口标题——显式设置优先于自动跟随。
    /// 三平台同一语义（否则"页面标题何时能覆盖"会变成平台相关的隐式规则）。
    /// </remarks>
    private bool _titleSetExplicitly;
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
        // 运行期可改（SetDecorations）；**注意 macOS 无边框窗口的 styleMask 里仍然带 Titled**
        // （无边框是"Titled + 透明标题栏 + 隐藏按钮 + fullSizeContentView"），
        // 所以不能用 styleMask 推断"有没有装饰"——那会把无边框窗口读成有装饰。
        _decorations = !options.Frameless;
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
    // B10 第二批：本平台目前只上报 drop（见 OnFilesDropped）。enter/over/leave 要各自的坐标系换算
    // （macOS 左下原点 + backingScaleFactor、GTK 逻辑像素 + scaleFactor），与页面事件面同批上线——
    // 只在 Windows 收到 enter 的平台分裂比三平台都没有更难查。
    event Action<OrielDragEventArgs>? IWindowBackend.DragDrop { add => DragDrop += value; remove => DragDrop -= value; }

    private event Action<OrielDragEventArgs>? DragDrop;

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

    /// <summary>当前是否显示系统装饰；<see cref="SetDecorations"/> 会改它。</summary>
    /// <remarks>
    /// 记库里的状态而不是读 <c>styleMask</c>：本库的无边框窗口**仍然带 <c>Titled</c>**
    /// （无边框靠"透明标题栏 + 隐藏按钮 + fullSizeContentView"实现），
    /// 读样式位会把无边框窗口读成"有装饰"。
    /// </remarks>
    public bool IsDecorated => _decorations;

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

    /// <summary>覆写这个窗口的主题（见 <see cref="IWindowBackend.SetTheme"/>）。</summary>
    /// <remarks>
    /// <para>
    /// macOS 落点是 <c>NSWindow.appearance</c>（**按窗口**）：设了它的窗口按这个
    /// appearance 画，没设的跟应用走（<c>NSApp.appearance</c>）。所以 macOS 上
    /// "这个窗口"与"整个应用"是两层，语义比 Linux（只有一层）清楚。
    /// </para>
    /// <para>
    /// <c>null</c> = 跟随系统：把 <c>appearance</c> 还原成 <c>nil</c>，
    /// 那表示"让 NSWindow 自己按系统/应用决定"。
    /// </para>
    /// </remarks>
    public void SetTheme(OrielTheme? theme)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        if (theme is null)
        {
            // 还原：让 NSWindow 按系统/应用主题画
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setAppearance:"), 0);
            return;
        }

        nint appearanceName = ObjCRuntime.MakeNSString(
            theme == OrielTheme.Dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
        nint appearance = ObjCRuntime.SendIdObj(
            ObjCRuntime.GetClassOrThrow("NSAppearance"),
            ObjCRuntime.Sel("appearanceNamed:"),
            appearanceName);
        if (appearance != 0)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setAppearance:"), appearance);
        }
    }

    /// <summary>设置缩放倍率（见 <see cref="IWindowBackend.SetZoom"/>）。</summary>
    /// <remarks>
    /// 落点是 <c>WKWebView.pageZoom</c>（1.0 = 100%，macOS 10.14+），与 WebView2 的
    /// ZoomFactor、WebKitGTK 的 zoom_level 语义一致。必须在主线程调——命令层本来就在。
    /// </remarks>
    public void SetZoom(double scale)
    {
        if (_webview == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidDouble(_webview, ObjCRuntime.Sel("setPageZoom:"), scale);
    }

    /// <summary>清除全部浏览数据（见 <see cref="IWindowBackend.ClearAllBrowsingData"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 落点是 <c>[WKWebsiteDataStore.defaultDataStore removeDataOfTypes:
    /// allWebsiteDataTypes modifiedSince:[NSDate distantPast] completionHandler:nil]</c>：
    /// 默认（持久）数据存储里的全部类型、全部时间的数据。
    /// </para>
    /// <para>
    /// completionHandler 传 nil 与本文件 <c>evaluateJavaScript:completionHandler:</c>
    /// 的既有做法同一姿态（三处都在传 0）：完成通知拿不到，清除照常执行。
    /// 该 API 本身自 macOS 10.13 起可用，无版本顾虑。
    /// </para>
    /// </remarks>
    public void ClearAllBrowsingData()
    {
        if (_webview == 0)
        {
            return;
        }

        nint storeType = ObjCRuntime.GetClassOrThrow("WKWebsiteDataStore");
        nint store = ObjCRuntime.SendId(storeType, ObjCRuntime.Sel("defaultDataStore"));
        nint types = ObjCRuntime.SendId(storeType, ObjCRuntime.Sel("allWebsiteDataTypes"));
        nint distantPast = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSDate"), ObjCRuntime.Sel("distantPast"));
        if (store == 0 || types == 0 || distantPast == 0)
        {
            // 类或对象没拿到（理论上不会——都是 Foundation/WebKit 的稳定 API）。
            // 静默返回会不可观测，走诊断通道。
            OrielDiagnostics.Report("[OrielWeb] clearAllBrowsingData：WKWebsiteDataStore 对象获取失败，未清除。");
            return;
        }

        ObjCRuntime.SendVoidObjObjObj(
            store,
            ObjCRuntime.Sel("removeDataOfTypes:modifiedSince:completionHandler:"),
            types, distantPast, 0);
    }

    // ---- 光标（B21）----

    /// <summary>应用显式设置的光标形状（<c>null</c> = 没设过）。</summary>
    /// <remarks>
    /// 记它只为诊断与"设过之后不再假装能跟着 CSS 走"的文档一致性；
    /// macOS 没有"恢复默认"的入口，只能再设一个形状。
    /// </remarks>
    private OrielCursorIcon? _cursorIcon;

    /// <summary>光标可见性意图（与 <c>NSCursor hide/unhide</c> 的全局计数配对）。</summary>
    private bool _cursorVisible = true;

    /// <summary>是否已断开"鼠标 → 光标"的关联（<see cref="SetCursorGrab"/>）。</summary>
    private bool _cursorGrabbed;

    /// <summary>是否设置了忽略鼠标事件。</summary>
    private bool _ignoreCursorEvents;

    /// <inheritdoc />
    public void SetCursorGrab(bool grab)
    {
        if (grab == _cursorGrabbed)
        {
            return;
        }

        _cursorGrabbed = grab;

        // connected: 0 = 断开（光标锁死），非 0 = 恢复。
        // 这是**进程级**状态，所以窗口销毁时必须在 OnWindowWillClose 里恢复。
        _ = CoreGraphicsNative.CGAssociateMouseAndMouseCursorPosition(grab ? 0 : 1);
    }

    /// <inheritdoc />
    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible)
        {
            return;
        }

        _cursorVisible = visible;

        // NSCursor 的 hide/unhide 也是**全局计数**：多调一次 hide 就要多调一次 unhide
        nint cursorClass = ObjCRuntime.GetClassOrThrow("NSCursor");
        ObjCRuntime.SendVoid(cursorClass, ObjCRuntime.Sel(visible ? "unhide" : "hide"));
    }

    /// <inheritdoc />
    public void SetCursorIcon(OrielCursorIcon icon)
    {
        _cursorIcon = icon;

        nint cursor = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSCursor"),
            ObjCRuntime.Sel(SelectorFor(icon)));

        if (cursor != 0)
        {
            ObjCRuntime.SendVoid(cursor, ObjCRuntime.Sel("set"));
        }
    }

    /// <summary>枚举 → <c>NSCursor</c> 的类方法选择器（没有对应形状时用最接近的一个）。</summary>
    /// <remarks>
    /// <b>AppKit 只有 18 个公开光标</b>，<c>copy</c> / <c>zoomIn</c> / 对角线 resize 都没有对应形状。
    /// 降级表与 docs/API.md 逐条对应——改这里要同步改文档。
    /// 对角 resize 落到 <c>resizeUpDownCursor</c>/<c>resizeLeftRightCursor</c>：
    /// 这两个至少给出"能拖"的视觉暗示，而箭头什么都不给。
    /// </remarks>
    private static string SelectorFor(OrielCursorIcon icon) => icon switch
    {
        OrielCursorIcon.Crosshair or OrielCursorIcon.Cell => "crosshairCursor",
        OrielCursorIcon.Hand => "pointingHandCursor",
        OrielCursorIcon.Move or OrielCursorIcon.AllScroll or OrielCursorIcon.Grab => "openHandCursor",
        OrielCursorIcon.Grabbing => "closedHandCursor",
        OrielCursorIcon.Text => "IBeamCursor",
        OrielCursorIcon.VerticalText => "IBeamCursorForVerticalLayout",
        OrielCursorIcon.NotAllowed or OrielCursorIcon.NoDrop => "operationNotAllowedCursor",
        OrielCursorIcon.ContextMenu => "contextualMenuCursor",
        OrielCursorIcon.Alias => "dragLinkCursor",
        OrielCursorIcon.Copy => "dragCopyCursor",
        OrielCursorIcon.EResize or OrielCursorIcon.WResize or OrielCursorIcon.EwResize
            or OrielCursorIcon.ColResize or OrielCursorIcon.NwResize or OrielCursorIcon.SeResize
            or OrielCursorIcon.NwseResize => "resizeLeftRightCursor",
        OrielCursorIcon.NResize or OrielCursorIcon.SResize or OrielCursorIcon.NsResize
            or OrielCursorIcon.RowResize or OrielCursorIcon.NeResize or OrielCursorIcon.SwResize
            or OrielCursorIcon.NeswResize => "resizeUpDownCursor",

        // default / arrow / wait / progress / zoomIn / zoomOut：AppKit 没有对应形状
        _ => "arrowCursor",
    };

    /// <inheritdoc />
    public void SetCursorPosition(double x, double y)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        // CG 的全局坐标是**点**（逻辑像素），原点在主屏左上角——与 InnerPosition 同口径，
        // 所以直接相加，不需要乘缩放比
        OrielPosition inner = InnerPosition;
        _ = CoreGraphicsNative.CGWarpMouseCursorPosition(new CGPoint(inner.X + x, inner.Y + y));
    }

    /// <inheritdoc />
    public void SetIgnoreCursorEvents(bool ignore)
    {
        if (_nsWindow == 0 || ignore == _ignoreCursorEvents)
        {
            return;
        }

        _ignoreCursorEvents = ignore;
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), ignore);
    }

    /// <inheritdoc />
    public void StartResizeDragging(OrielResizeDirection direction)
        => throw new NotSupportedException(
            "macOS 不支持 startResizeDragging：AppKit 没有公开的" +
            "\"以编程方式开始 resize 拖动\"入口（performWindowDragWithEvent: 只能移动窗口）。" +
            "需要该能力时请用可拖动的自绘边框配合 window.dragStart 之类的做法。");

    // ------------------------------------------------------------------
    // 平台开关 / 特效 / 任务栏（B22）
    // ------------------------------------------------------------------

    /// <summary>进简化全屏前的 <c>styleMask</c>（退出时还原）。</summary>
    private nuint _styleMaskBeforeSimpleFullscreen;

    /// <summary>进简化全屏前的窗口矩形（Cocoa 点），退出时还原。</summary>
    private (double X, double Y, double Width, double Height)? _frameBeforeSimpleFullscreen;

    /// <summary>当前是否处于简化全屏（见 <see cref="SetSimpleFullscreen"/>）。</summary>
    private bool _isSimpleFullscreen;

    /// <summary>B22 的材质视图（<c>NSVisualEffectView</c>）；0 = 没有。</summary>
    private nint _effectView;

    /// <summary>当前是否显示系统装饰（见 <see cref="IsDecorated"/>）。</summary>
    private bool _decorations;

    /// <inheritdoc />
    public void SetSkipTaskbar(bool skip)
        => throw new NotSupportedException(
            "macOS 不支持 setSkipTaskbar：Dock 里显示的是整个应用，没有\"按窗口隐藏\"这回事"
            + "（要从 Dock 消失得改 NSApplication 的 activationPolicy，那是应用级决定）。"
            + "Tauri 在 macOS 上同样不支持。");

    /// <inheritdoc />
    /// <remarks>
    /// 只切 <c>NSWindowCollectionBehaviorCanJoinAllSpaces</c> 这一位，**保留**窗口已有的其它
    /// collectionBehavior（全屏窗口的 <c>FullScreenPrimary</c> 就在同一字段里）：整段覆盖会把
    /// 它们一起清掉，"刚设过全工作区"的全屏窗口于是行为变了。
    /// </remarks>
    public void SetVisibleOnAllWorkspaces(bool visible)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        const nuint CanJoinAllSpaces = 1 << 0;
        nuint behavior = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("collectionBehavior"));
        behavior = visible ? behavior | CanJoinAllSpaces : behavior & ~CanJoinAllSpaces;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setCollectionBehavior:"), (nint)behavior);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>NSWindowSharingNone = 0</c>（不可被截取）、<c>NSWindowSharingReadOnly = 1</c>（默认）。
    /// </remarks>
    public void SetContentProtected(bool contentProtected)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setSharingType:"), contentProtected ? 0 : 1);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 与原生全屏的区别：**不创建新的 Space**，靠"去掉标题栏 + 窗口铺满所在屏幕"实现
    /// （Lion 之前的全屏形态）。所以用户不会被带到另一张桌面、别的窗口也不会被推走。
    /// </para>
    /// <para>
    /// 进出都保存/还原 <c>styleMask</c> 与窗口矩形——退出后窗口回到原来的位置与大小。
    /// 与原生全屏互斥：已经处于原生全屏时先退出它，否则两个模式打架、窗口卡在半屏。
    /// </para>
    /// </remarks>
    public void SetSimpleFullscreen(bool fullscreen)
    {
        if (_nsWindow == 0 || fullscreen == _isSimpleFullscreen)
        {
            return;
        }

        if (fullscreen)
        {
            if (IsFullscreen)
            {
                SetFullscreen(false);
            }

            _styleMaskBeforeSimpleFullscreen =
                (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
            _frameBeforeSimpleFullscreen = ReadFrame();

            nint screen = ScreenOfWindow();
            (double sx, double sy, double sw, double sh) = ScreenFrame(screen);

            // 去掉标题栏：borderless 才能铺满到菜单栏下方（原生保留圆角与阴影）
            ObjCRuntime.SendVoidNint(
                _nsWindow,
                ObjCRuntime.Sel("setStyleMask:"),
                (nint)(_styleMaskBeforeSimpleFullscreen & ~StyleTitled));

            ObjCRuntime.SendVoidDouble4Bool(
                _nsWindow, ObjCRuntime.Sel("setFrame:display:"), sx, sy, sw, sh, true);

            _isSimpleFullscreen = true;
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)_styleMaskBeforeSimpleFullscreen);

        if (_frameBeforeSimpleFullscreen is { } f)
        {
            ObjCRuntime.SendVoidDouble4Bool(
                _nsWindow, ObjCRuntime.Sel("setFrame:display:"), f.X, f.Y, f.Width, f.Height, true);
        }

        _frameBeforeSimpleFullscreen = null;
        _isSimpleFullscreen = false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 参数是**屏幕物理像素**（与 <c>Monitor.position</c> 同口径）。Cocoa 的屏幕坐标是
    /// "点 + 左下原点"，所以先按主屏缩放比与主屏高度把它翻过来再比对——不翻的话副屏上
    /// 会挑错屏幕（上下颠倒的那一块）。点不在任何屏幕上时什么都不做（与 Tauri 一致）。
    /// </remarks>
    public void SetFullscreenOnMonitor(int x, int y)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        nint target = ScreenForPhysicalPoint(x, y);
        if (target == 0)
        {
            return;
        }

        (double sx, double sy, double sw, double sh) = ScreenFrame(target);

        bool wasFullscreen = IsFullscreen;
        bool wasSimple = _isSimpleFullscreen;
        if (wasFullscreen)
        {
            SetFullscreen(false);
        }

        if (wasSimple)
        {
            SetSimpleFullscreen(false);
        }

        // 全屏切换本身会把窗口铺满所在屏幕，这里只需要"把它搬到那台屏幕上"
        ObjCRuntime.SendVoidDouble4Bool(
            _nsWindow, ObjCRuntime.Sel("setFrame:display:"), sx, sy, sw, sh, true);

        if (wasSimple)
        {
            SetSimpleFullscreen(true);
        }
        else if (wasFullscreen)
        {
            SetFullscreen(true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 三档的落点：
    /// <list type="bullet">
    ///   <item><c>visible</c>：<c>titlebarAppearsTransparent = NO</c>、标题可见、
    ///     去掉 <c>fullSizeContentView</c>（内容从标题栏下方开始）。</item>
    ///   <item><c>transparent</c>：标题栏透明，其余同上。</item>
    ///   <item><c>overlay</c>：透明 + 标题隐藏 + <c>fullSizeContentView</c>——
    ///     页面延伸到标题栏下方，只剩红绿灯浮在上面（与 Tauri 的说明一致）。</item>
    /// </list>
    /// </remarks>
    public void SetTitleBarStyle(OrielTitleBarStyle style)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        bool transparent = style != OrielTitleBarStyle.Visible;
        bool overlay = style == OrielTitleBarStyle.Overlay;

        ObjCRuntime.SendVoidBool(
            _nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), transparent);
        ObjCRuntime.SendVoidNint(
            _nsWindow,
            ObjCRuntime.Sel("setTitleVisibility:"),
            overlay ? NSWindowTitleHidden : 0);

        // 改 styleMask 会让 AppKit 重画标题栏；先记再改，避免把别的位一起抹掉
        nuint mask = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
        mask = overlay ? mask | StyleFullSizeContentView : mask & ~StyleFullSizeContentView;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)mask);
    }

    /// <inheritdoc />
    public void SetShadow(bool enable)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setHasShadow:"), enable);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 落点是 <c>window.level</c>：普通窗口是 0，这里降到 <b>-1</b>——低于所有普通窗口、
    /// 但远高于桌面（<c>kCGDesktopWindowLevel</c> 在 -20 亿那一档，降到那里窗口会跑到壁纸
    /// 后面、而且通常再也拿不到焦点）。所以本库的"置底"是"比普通窗口都低、仍可交互"。
    /// </remarks>
    public void SetAlwaysOnBottom(bool alwaysOnBottom)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setLevel:"), alwaysOnBottom ? -1 : 0);
    }

    /// <inheritdoc />
    public void SetFocusable(bool focusable)
        => throw new NotSupportedException(
            "macOS 不支持 setFocusable：AppKit 没有公开的\"这个窗口不能成为 key window\"入口"
            + "（只有私有的 _setPreventsActivation，用它等于把自己绑到未公开 API 上）。"
            + "需要\"点了不抢焦点\"的窗口得用 nonactivating 的 NSPanel，本库不提供那种窗口类型。");

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 两件事一起做：切 <c>NSWindowStyleMaskTitled</c>，并把标题栏的透明/可见性设回去。
    /// 只切样式位不够——AppKit 里"无装饰"是**borderless**（没有标题栏、没有红绿灯），
    /// 而 Titled 位还在但标题栏透明的窗口看起来仍然是"有装饰"的（那是本库的 Frameless 形态）。
    /// </para>
    /// <para>
    /// 它**不会**动 <c>fullSizeContentView</c> 或标题可见性——那两件是
    /// <see cref="SetTitleBarStyle"/> 的事，两个方法各管一段。
    /// </para>
    /// </remarks>
    public void SetDecorations(bool decorations)
    {
        if (_nsWindow == 0 || decorations == _decorations)
        {
            return;
        }

        nuint mask = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
        mask = decorations ? mask | StyleTitled : mask & ~StyleTitled;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)mask);

        ObjCRuntime.SendVoidBool(
            _nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), !decorations);
        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setTitleVisibility:"), decorations ? 0 : NSWindowTitleHidden);

        _decorations = decorations;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 落点是 <c>NSVisualEffectView</c>：一个插在 contentView 里、**位于 webview 之下**的
    /// 材质视图。材质编号见 <see cref="ToVisualEffectMaterial"/>（<c>NSVisualEffectMaterial</c>）。
    /// </para>
    /// <para>
    /// <b>必须把窗口与 webview 的背景放开</b>，否则材质永远看不到：这里同时做三件事——
    /// 窗口 <c>opaque = NO</c> + 背景清空、webview 的 <c>drawsBackground</c> 置 NO
    /// （<c>WKWebView</c> 上那个属性是私有的，走 KVC；Tauri/tao 用的也是这条路）。
    /// 页面自己仍需要有透明背景——本库不改页面的 CSS。
    /// </para>
    /// <para>
    /// <c>liquidGlassRegular / liquidGlassClear</c> 要 macOS 26 的 NSGlassEffectView，
    /// 本库不实现：列表里只有它们时抛 <see cref="NotSupportedException"/>。
    /// </para>
    /// </remarks>
    public void SetEffects(OrielEffects effects)
    {
        if (_nsWindow == 0 || _webview == 0)
        {
            return;
        }

        int material = -1;
        foreach (OrielEffect effect in effects.Effects)
        {
            int candidate = ToVisualEffectMaterial(effect);
            if (candidate >= 0)
            {
                material = candidate;
                break;
            }
        }

        if (material < 0)
        {
            throw new NotSupportedException(
                "macOS 没有可用的材质：支持 sidebar / headerView / sheet / windowBackground / "
                + "hudWindow / fullScreenUI / tooltip / contentBackground / underWindowBackground / "
                + "underPageBackground / titlebar / selection / menu / popover / appearanceBased / "
                + "light / dark / mediumLight / ultraDark；"
                + "liquidGlassRegular / liquidGlassClear 需要 macOS 26 的 NSGlassEffectView，本库未实现"
                + "（mica / acrylic / blur / tabbed 是 Windows 材质）。");
        }

        RemoveEffectView();

        // 1) 窗口与 webview 放开背景：不透明的话材质被挡在下面，什么都看不到
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setOpaque:"), false);
        nint clear = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSColor"), ObjCRuntime.Sel("clearColor"));
        if (clear != 0)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setBackgroundColor:"), clear);
        }

        SetWebviewDrawsBackground(false);

        // 2) 造材质视图，插到 contentView 的最底层（webview 与 dropView 之下）
        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        (double _, double _, double width, double height) = ReadRect(contentView, "bounds");

        nint effectClass = ObjCRuntime.GetClassOrThrow("NSVisualEffectView");
        _effectView = ObjCRuntime.SendIdDouble4(
            ObjCRuntime.SendId(effectClass, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithFrame:"),
            0, 0, width, height);
        ObjCRuntime.objc_retain(_effectView);

        ObjCRuntime.SendVoidNint(_effectView, ObjCRuntime.Sel("setMaterial:"), material);
        ObjCRuntime.SendVoidNint(
            _effectView,
            ObjCRuntime.Sel("setState:"),
            effects.State switch
            {
                OrielEffectState.Active => 1,   // NSVisualEffectStateActive
                OrielEffectState.Inactive => 2, // NSVisualEffectStateInactive
                _ => 0,                          // NSVisualEffectStateFollowsWindowActiveState
            });

        // NSVisualEffectBlendingModeBehindWindow = 0：材质取窗口**后面**的内容
        ObjCRuntime.SendVoidNint(_effectView, ObjCRuntime.Sel("setBlendingMode:"), 0);
        ObjCRuntime.SendVoidNint(
            _effectView,
            ObjCRuntime.Sel("setAutoresizingMask:"),
            NSViewWidthSizable | NSViewHeightSizable);

        // NSWindowBelow = -1：插在参照视图（dropView）之下；dropView 不存在时插到 contentView 底下
        ObjCRuntime.SendVoidObjNintObj(
            contentView,
            ObjCRuntime.Sel("addSubview:positioned:relativeTo:"),
            _effectView,
            -1,
            _dropView);

        // 3) 圆角（Tauri 的 radius，macOS only）：走 layer.cornerRadius
        if (effects.Radius is { } radius && radius > 0)
        {
            ObjCRuntime.SendVoidBool(_effectView, ObjCRuntime.Sel("setWantsLayer:"), true);
            nint layer = ObjCRuntime.SendId(_effectView, ObjCRuntime.Sel("layer"));
            if (layer != 0)
            {
                ObjCRuntime.SendVoidDouble(layer, ObjCRuntime.Sel("setCornerRadius:"), radius);
                ObjCRuntime.SendVoidBool(layer, ObjCRuntime.Sel("setMasksToBounds:"), true);
            }
        }

        if (effects.Interactive)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] setEffects：interactive 需要 macOS 26 的液态玻璃，本库未实现，已忽略。");
        }
    }

    /// <inheritdoc />
    public void ClearEffects()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        RemoveEffectView();

        // 还原窗口与 webview 的背景：材质没了之后，不透明窗口比"漏底"更接近默认观感。
        // **注意不能只调 ApplyBackgroundColor**：它在"没设过底色"时直接返回，
        // 而 setEffects 刚刚把窗口底色改成了 clear——那样窗口会一直透明下去
        // （表现为"清掉材质后整个窗口是黑的/透的"）。所以没显式底色时要显式还原成
        // 系统的窗口底色。
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setOpaque:"), true);
        _runtimeBackgroundArgb = null;
        if (_options.TryGetBackgroundArgb(out _, out _, out _, out _))
        {
            ApplyBackgroundColor();
        }
        else
        {
            RestoreSystemBackgroundColor();
        }

        SetWebviewDrawsBackground(true);
    }

    /// <summary>把窗口与 webview 的底色还原成系统的窗口底色（<c>NSColor.windowBackgroundColor</c>）。</summary>
    private void RestoreSystemBackgroundColor()
    {
        nint color = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSColor"), ObjCRuntime.Sel("windowBackgroundColor"));
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

    /// <summary>把材质视图摘掉并释放（幂等）。</summary>
    private void RemoveEffectView()
    {
        if (_effectView == 0)
        {
            return;
        }

        ObjCRuntime.SendVoid(_effectView, ObjCRuntime.Sel("removeFromSuperview"));
        ObjCRuntime.objc_release(_effectView);
        _effectView = 0;
    }

    /// <summary>
    /// 开关 webview 的背景绘制（<c>drawsBackground</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是私有属性，走 KVC。</b>WKWebView 没有公开的"透明背景"入口，而材质要透出来
    /// 就必须让它别画那层不透明的底——macOS 上除了这条路没有别的办法（Tauri/tao 同样如此）。
    /// </para>
    /// <para>
    /// <b>调之前必须确认 KVC 不会抛。</b>KVC 对"既没有 setter、也没有同名 ivar"的键会抛
    /// <c>NSUndefinedKeyException</c>，而那是 ObjC 异常、托管侧接不住——一次 setEffects
    /// 就能让整个应用消失。所以先用运行时 API 问两个问题：类上有没有
    /// <c>setDrawsBackground:</c>、有没有 <c>_drawsBackground</c> ivar。任一存在，KVC 就
    /// 走得到；都不存在（将来的 WebKit 改了实现）就跳过并记一条诊断——
    /// 那种情况下材质看不见，但应用不会挂。
    /// </para>
    /// </remarks>
    private void SetWebviewDrawsBackground(bool draws)
    {
        if (_webview == 0)
        {
            return;
        }

        nint cls = ObjCRuntime.object_getClass(_webview);
        bool kvcSafe = ObjCRuntime.class_getInstanceMethod(cls, "setDrawsBackground:") != 0
            || ObjCRuntime.class_getInstanceVariable(cls, "_drawsBackground") != 0;

        if (!kvcSafe)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] setEffects：这个系统上的 WKWebView 没有 drawsBackground，"
                + "webview 仍会画不透明底色——材质可能看不见（页面背景透不出来）。");
            return;
        }

        nint key = ObjCRuntime.MakeNSString("drawsBackground");
        nint boxed = ObjCRuntime.SendIdNint(
            ObjCRuntime.GetClassOrThrow("NSNumber"), ObjCRuntime.Sel("numberWithBool:"), draws ? 1 : 0);
        ObjCRuntime.SendVoidObjObj(_webview, ObjCRuntime.Sel("setValue:forKey:"), boxed, key);
    }

    /// <summary>把 Tauri 的 <c>Effect</c> 映射到 <c>NSVisualEffectMaterial</c>；不支持的返回 -1。</summary>
    /// <remarks>
    /// 编号来自 AppKit 的 <c>NSVisualEffectMaterial</c>（10.14 的取值集合）。前五个是 10.14
    /// 起废弃的旧材质，但仍然是合法取值——老系统上它们才是能用的那几个。
    /// </remarks>
    private static int ToVisualEffectMaterial(OrielEffect effect) => effect switch
    {
        OrielEffect.AppearanceBased => 0,
        OrielEffect.Light => 1,
        OrielEffect.Dark => 2,
        OrielEffect.Titlebar => 3,
        OrielEffect.Selection => 4,
        OrielEffect.Menu => 5,
        OrielEffect.Popover => 6,
        OrielEffect.Sidebar => 7,
        OrielEffect.MediumLight => 8,
        OrielEffect.UltraDark => 9,
        OrielEffect.HeaderView => 10,
        OrielEffect.Sheet => 11,
        OrielEffect.WindowBackground => 12,
        OrielEffect.HudWindow => 13,
        OrielEffect.FullScreenUi => 15,
        OrielEffect.Tooltip => 17,
        OrielEffect.ContentBackground => 18,
        OrielEffect.UnderWindowBackground => 21,
        OrielEffect.UnderPageBackground => 22,

        // Windows 材质与 macOS 26 的液态玻璃：本平台没有对应物
        _ => -1,
    };

    /// <inheritdoc />
    public void SetProgressBar(OrielProgressBarStatus status, double? progress)
        => throw new NotSupportedException(
            "macOS 不支持 setProgressBar：NSDockTile 没有进度条 API（要塞进度得把整个磁贴内容"
            + "接管成自绘视图）。Tauri 在 macOS 上同样不实现。");

    /// <inheritdoc />
    public void SetOverlayIcon(OrielImage? icon)
        => throw new NotSupportedException(
            "macOS 没有任务栏覆盖图标：用 setBadgeCount / setBadgeLabel 在 Dock 图标上显示数字或短文本。");

    /// <inheritdoc />
    /// <remarks>
    /// <c>NSApp.dockTile.badgeLabel</c> 收字符串：<c>null</c> 或 0 清空，正数写十进制文本。
    /// **应用级**——所有窗口调它都是改同一个 Dock 图标（AppKit 的事实）。
    /// </remarks>
    public void SetBadgeCount(int? count)
    {
        if (count is null or <= 0)
        {
            SetBadgeLabel(null);
            return;
        }

        SetBadgeLabel(count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public void SetBadgeLabel(string? label)
    {
        nint app = SharedApplication();
        if (app == 0)
        {
            return;
        }

        nint dockTile = ObjCRuntime.SendId(app, ObjCRuntime.Sel("dockTile"));
        if (dockTile == 0)
        {
            return;
        }

        nint text = label is null ? 0 : ObjCRuntime.MakeNSString(label);
        ObjCRuntime.SendVoidObj(dockTile, ObjCRuntime.Sel("setBadgeLabel:"), text);
    }

    /// <summary>取窗口所在的 <c>NSScreen</c>（取不到时退回主屏）。</summary>
    private nint ScreenOfWindow()
    {
        nint screen = _nsWindow == 0 ? 0 : ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("screen"));
        return screen != 0
            ? screen
            : ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSScreen"), ObjCRuntime.Sel("mainScreen"));
    }

    /// <summary>取某个 <c>NSScreen</c> 的矩形（Cocoa 点，左下原点）。</summary>
    private static (double X, double Y, double Width, double Height) ScreenFrame(nint screen)
        => screen == 0 ? (0, 0, 0, 0) : ReadRect(screen, "frame");

    /// <summary>
    /// 按 KVC + <c>getValue:</c> 读一个 NSRect 属性（key 形如 <c>frame</c> / <c>bounds</c>）。
    /// </summary>
    /// <remarks>
    /// <b>不直取返回值</b>：NSRect 是结构体，x86_64 上要走 <c>objc_msgSend_stret</c>、
    /// arm64 上是普通返回——一个 P/Invoke 表达不了两套 ABI。本库读窗口 frame 的既有做法
    /// 就是这个（见 <c>ReadFrame</c>），这里抽成通用版本给 NSScreen / NSView 复用。
    /// </remarks>
    private static (double X, double Y, double Width, double Height) ReadRect(nint obj, string key)
    {
        if (obj == 0)
        {
            return (0, 0, 0, 0);
        }

        nint boxed = ObjCRuntime.SendIdObj(obj, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString(key));
        if (boxed == 0)
        {
            return (0, 0, 0, 0);
        }

        nint buffer = Marshal.AllocHGlobal(4 * sizeof(double));
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 0)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 8)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 16)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 24)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>按屏幕**物理像素**坐标找屏幕；找不到返回 0。</summary>
    private static nint ScreenForPhysicalPoint(int x, int y)
    {
        nint cls = ObjCRuntime.GetClassOrThrow("NSScreen");
        nint main = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen"));
        if (main == 0)
        {
            return 0;
        }

        // 物理像素 → 点：主屏的缩放比；y 还要相对主屏高度翻转（Cocoa 是左下原点）
        double scale = ObjCRuntime.SendDouble(main, ObjCRuntime.Sel("backingScaleFactor"));
        if (scale <= 0)
        {
            scale = 1;
        }

        (double _, double _, double _, double mainHeight) = ScreenFrame(main);
        double px = x / scale;
        double py = mainHeight - (y / scale);

        nint screens = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("screens"));
        if (screens == 0)
        {
            return main;
        }

        nint count = ObjCRuntime.SendNint(screens, ObjCRuntime.Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            nint screen = ObjCRuntime.SendIdNint(screens, ObjCRuntime.Sel("objectAtIndex:"), i);
            (double sx, double sy, double sw, double sh) = ScreenFrame(screen);
            if (px >= sx && px < sx + sw && py >= sy && py < sy + sh)
            {
                return screen;
            }
        }

        return 0;
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
    /// <para>
    /// <c>requestUserAttention:</c> 的取值是 <c>NSCriticalRequest = 0</c> /
    /// <c>NSInformationalRequest = 10</c>（**不是** 0/1，容易记错），
    /// 返回值是一个请求 id，取消时要交给 <c>cancelUserAttentionRequest:</c>。
    /// </para>
    /// <para>
    /// <b>两条消息都必须发给 <c>NSApplication</c> 的实例，不是 <c>NSWindow</c>，也不是类对象。</b>
    /// 它们都是 <c>NSApplication</c> 的**实例方法**：发给窗口会因 unrecognized selector 抛
    /// <c>NSException</c>（进程直接 SIGABRT），发给类对象同理。这也是"应用级注意力"而非
    /// "窗口级"的语义来源——Dock 图标弹跳本来就是整个应用的行为。
    /// </para>
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
                    SharedApplication(),
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
            SharedApplication(), ObjCRuntime.Sel("requestUserAttention:"), request);
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
    /// 上下文菜单：构建 → 弹出。
    /// </summary>
    /// <remarks>
    /// 弹出是异步的，菜单对象因此留到下一次弹出前才释放（不能弹完即抛）。
    /// <paramref name="position"/> 为 null 时位置由当前 <c>NSEvent</c> 决定；
    /// 没有当前事件时 <see cref="MacOSMenu.Popup"/> 会明确跳过并记日志，而不是弹到随机位置。
    /// </remarks>
    public void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
    {
        _contextMenu?.Dispose();
        _contextMenu = MacOSMenu.Build(items, ActivateMenuItem);
        if (_contextMenu is not null && _webview != 0)
        {
            // 自动关闭交给 MacOSMenu：它要在**弹出之前**用
            // `performSelector:afterDelay:inModes:` 把定时器排进 NSEventTrackingRunLoopMode。
            // 在这里用 Task.Delay 不行——弹出会阻塞主线程直到菜单关掉，
            // 而投回主线程的动作注册在 default mode，等不到。
            _contextMenu.Popup(_webview, position, autoDismissAfter);
        }
    }

    /// <summary>
    /// 取消正在跟踪的菜单（<c>cancelTracking</c>）。
    /// </summary>
    /// <remarks>
    /// macOS 的弹出本来就不阻塞调用方，所以这条主要用于"页面要求关掉菜单"
    /// （例如快捷键 Esc 由页面处理时）。没有菜单在屏上是空操作。
    /// </remarks>
    public void CloseContextMenu() => _contextMenu?.CancelTracking();

    // ------------------------------------------------------------------
    // 菜单栏
    // ------------------------------------------------------------------

    /// <summary>
    /// 应用菜单（<c>[NSApp setMainMenu:]</c> 装的那一份）。
    /// </summary>
    /// <remarks>
    /// <b>静态</b>而不是挂在窗口宿主上：macOS 的菜单栏是**应用级**的，一个进程只有一份
    /// （一个 NSApp）。挂实例上的话，第二个窗口装应用菜单会把第一个窗口那份 Dispose 掉，
    /// 而它们本来装的是同一件事——表现是"第一个窗口的菜单还能看见，但点了没反应"。
    /// </remarks>
    private static MacOSMenu? s_appMenuBar;

    /// <summary>
    /// macOS **不支持**"某个窗口的菜单栏"。
    /// </summary>
    /// <exception cref="NotSupportedException">总是抛出。</exception>
    /// <remarks>
    /// 与 Tauri 的行为一致（它的文档明说 <c>setAsWindowMenu</c> 在 macOS 上 "Unsupported"）。
    /// 这不是本库的缺口——是 AppKit 的模型里没有这个概念，菜单只能是应用级的。
    /// </remarks>
    public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        throw new NotSupportedException(
            "macOS 没有「某个窗口的菜单栏」：菜单在 macOS 是应用级的，请改用 setAsAppMenu。");
    }

    /// <summary>
    /// 设为应用菜单（<c>[NSApp setMainMenu:]</c>）。
    /// </summary>
    /// <param name="items">应用菜单的顶层项；空集合表示移除。</param>
    /// <remarks>
    /// <para>
    /// 顶层项在 macOS 的主菜单里是**子菜单**（第一个是应用菜单，其余是"文件/编辑/窗口/帮助"）。
    /// <see cref="MacOSMenu.Build"/> 建出来的结构正好是这个形状：带子项的顶层项会成为
    /// <c>NSMenuItem</c> + <c>NSMenu</c> 子菜单。
    /// </para>
    /// <para>
    /// 反复设置是幂等的：同一份内容会重建一次原生菜单（本库没有资源表，每次都是整棵发），
    /// 旧的那棵 Dispose 掉。<b>注意这会让旧菜单的点击回调失效</b>——所以自定义项的 action
    /// 要挂在**当前**那份菜单上，别把回调存起来跨设置复用。
    /// </para>
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        nint app = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        if (app == 0)
        {
            OrielDiagnostics.Report("[OrielWeb] 拿不到 NSApplication.sharedApplication，应用菜单未设置。");
            return;
        }

        MacOSMenu? previous = s_appMenuBar;
        s_appMenuBar = null;

        if (items.Count == 0)
        {
            ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), 0);
            previous?.Dispose();
            return;
        }

        MacOSMenu? bar = MacOSMenu.Build(items, ActivateMenuItem);
        if (bar is null)
        {
            ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), 0);
            previous?.Dispose();
            return;
        }

        s_appMenuBar = bar;
        ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), bar.Handle);

        // 新的装上去了才放旧的——反过来会有一瞬间没有主菜单。
        previous?.Dispose();
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
    internal void OnFilesDropped(IReadOnlyList<string> paths, int x, int y)
    {
        // FileDropped 是 B8 已发布的公开事件，drop 时两者都发；DragDrop 是完整版本
        DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Drop, paths, x, y));
        FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
    }

    /// <summary>拖动进入窗口（<c>draggingEntered:</c>）。</summary>
    internal void OnDragEntered(IReadOnlyList<string> paths, int x, int y)
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Enter, paths, x, y));

    /// <summary>在窗口内移动（<c>draggingUpdated:</c>，频率可达每秒几十次）。</summary>
    internal void OnDragUpdated(int x, int y)
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Over, [], x, y));

    /// <summary>离开窗口（<c>draggingExited:</c>）。</summary>
    internal void OnDragExited()
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Leave, [], 0, 0));

    /// <summary>
    /// 取当前拖动位置（客户区物理像素）。
    /// </summary>
    /// <remarks>
    /// <b>不用 <c>-draggingLocation</c></b>：它返回 <c>NSPoint</c> 结构体，而本库的约定是不做
    /// struct 返回的 <c>objc_msgSend</c>（x86_64 要走 <c>objc_msgSend_stret</c>、arm64 是隐式 sret，
    /// 绑定里没有那条路）。拖动期间鼠标就在拖动点上，所以用 <c>CGEventGetLocation</c> 的全局位置
    /// 减去客户区原点，结果一致，且完全绕开结构体返回的 ABI。
    /// </remarks>
    internal (int X, int Y) DragPosition()
    {
        try
        {
            OrielPosition cursor = App.MonitorBackend.GetCursorPosition(); // 物理像素、全局左上原点
            OrielPosition inner = InnerPosition;                            // 逻辑像素、客户区左上原点
            double scale = ScaleFactor;

            return ((int)Math.Round(cursor.X - (inner.X * scale)),
                    (int)Math.Round(cursor.Y - (inner.Y * scale)));
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 读取拖动坐标失败：{ex}");
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    /// <summary>
    /// 按 <see cref="OrielWindowOptions.DataStoreIdentifier"/> 给 webview 绑定一份隔离的浏览数据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>+[WKWebsiteDataStore dataStoreForIdentifier:]</c> 是 macOS 12+ 的**类方法**
    /// （返回一个按 UUID 隔离的存储）。同一个 UUID 的多个窗口共享 cookie / localStorage，
    /// 不同 UUID 彼此隔离——这就是 Tauri 的 <c>data_store_identifier</c> 在 macOS 上的全部含义。
    /// </para>
    /// <para>
    /// <b>先探测再发消息</b>：旧系统上这个类方法不存在，向它发消息会抛
    /// <c>NSInvalidArgumentException</c>（ObjC 异常，托管侧接不住）。探测不到时记一条诊断并
    /// 沿用默认存储——窗口照常可用，只是没有隔离。
    /// </para>
    /// </remarks>
    private void ApplyDataStoreIdentifier(nint config)
    {
        if (_options.DataStoreIdentifier is not Guid identifier)
        {
            return;
        }

        nint storeClass = ObjCRuntime.GetClassOrThrow("WKWebsiteDataStore");
        if (ObjCRuntime.class_getClassMethod(storeClass, "dataStoreForIdentifier:") == 0)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] DataStoreIdentifier 需要 macOS 12 或更高版本："
                + "本窗口沿用默认数据存储（没有隔离）。");
            return;
        }

        nint store = ObjCRuntime.SendIdObj(
            storeClass,
            ObjCRuntime.Sel("dataStoreForIdentifier:"),
            ObjCRuntime.MakeNSUUID(identifier));

        if (store == 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] DataStoreIdentifier 绑定失败（{identifier:D}），本窗口沿用默认数据存储。");
            return;
        }

        ObjCRuntime.SendVoidObj(config, ObjCRuntime.Sel("setWebsiteDataStore:"), store);
    }

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

        // 数据存储（B23）：必须在创建 WKWebView **之前**挂到 config 上——
        // webview 一旦建出来，它的 dataStore 就固定了，之后改 config 不生效。
        ApplyDataStoreIdentifier(config);

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
        // 光标状态必须在这里恢复：grab 是**进程级**的（不清会让整个应用的光标不再跟随鼠标，
        // 而应用可能还在跑），隐藏与穿透也会残留到别的窗口上。
        if (_cursorGrabbed)
        {
            _cursorGrabbed = false;
            _ = CoreGraphicsNative.CGAssociateMouseAndMouseCursorPosition(1);
        }

        if (!_cursorVisible)
        {
            _cursorVisible = true;
            ObjCRuntime.SendVoid(ObjCRuntime.GetClassOrThrow("NSCursor"), ObjCRuntime.Sel("unhide"));
        }

        if (_ignoreCursorEvents && _nsWindow != 0)
        {
            _ignoreCursorEvents = false;
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), false);
        }

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
        // **必须切回 UI 线程**：事件可能从任意线程发出（B9 的通道结束事件就来自
        // ConfigureAwait(false) 之后的线程池线程）。WebKit 的求值调用在 UI 线程之外
        // 行为未定义——不能因为"这次碰巧能跑"就省掉这一步。回执通道一直有这条守卫。
        if (!IsOnUiThread())
        {
            PostToUiThread(() => PushEventOnUi(name, jsonValue));
            return;
        }

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
        // 显式设置之后，页面标题不再覆盖它（见 _titleSetExplicitly）
        _titleSetExplicitly = true;
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

    /// <summary>从内存里的图像字节设置窗口图标（见 <see cref="IWindowBackend.SetIconFromBytes"/>）。</summary>
    /// <remarks>
    /// 与托盘图标共用 <see cref="MacOSImageFactory.CreateNSImage"/>（RGBA → CGImage → NSImage）。
    /// 落点仍是**应用（Dock）图标**——macOS 没有窗口级图标（见 <see cref="SetIcon"/>）。
    /// </remarks>
    public void SetIconFromBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        OrielImage image;
        try
        {
            image = OrielImage.FromBytes(bytes);
        }
        catch (OrielImageFormatException ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] setIcon 的图像数据无法解码：{ex.Message}");
            return;
        }

        nint nsImage = MacOSImageFactory.CreateNSImage(image);
        if (nsImage != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), nsImage);
        }
    }

    /// <summary>从裸 RGBA 设置窗口图标（见 <see cref="IWindowBackend.SetIconFromRgba"/>）。</summary>
    /// <remarks>
    /// 与 <see cref="SetIconFromBytes"/> 共用后半段，只是跳过解码。
    /// 落点仍是**应用（Dock）图标**——macOS 没有窗口级图标。
    /// </remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
    {
        if (rgba is null || rgba.Length == 0)
        {
            return;
        }

        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] setIcon 的 RGBA 长度不符：{width}x{height} 需要 {width * height * 4} 字节，" +
                $"收到 {rgba.Length} 字节。");
            return;
        }

        nint nsImage = MacOSImageFactory.CreateNSImage(new OrielImage(rgba, width, height));
        if (nsImage != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), nsImage);
        }
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
    /// <b>触发点有三个</b>：<c>windowDidResize:</c>（经 <see cref="ReportGeometry"/>）、
    /// <c>windowDidMove:</c>（经 <see cref="ReportPosition()"/>），以及
    /// <c>windowDidChangeBackingProperties:</c>（直接调本方法）。
    /// <para>
    /// 前两个覆盖"窗口被拖到不同缩放的显示器"；第三个覆盖"只改系统缩放、窗口不动"
    /// ——那时前两条都不发。三个都是 <c>NSWindowDelegate</c> 的方法，
    /// 所以不需要额外的通知观察者（早先记的"已知漏报"就是这么补掉的）。
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
        // 显式设过标题就不再跟随页面标题（理由见 _titleSetExplicitly 的说明）
        if (_titleSetExplicitly)
        {
            return;
        }

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
