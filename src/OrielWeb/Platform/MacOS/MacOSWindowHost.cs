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
    private bool _isFullscreen;
    private bool _isOnTop;
    private string _title;
    private int _evalSeq;

    // 上一次已上报（托管事件 + 页面）的最大化状态。NSWindow 的 zoom 是同步生效的，
    // 这个记忆值用于去重：windowDidResize / ToggleMaximize / 导航完成都可能触发同步。
    private bool _wasMaximized;
    // 页面是否已加载完成（桥接脚本就绪）。就绪前不推事件；加载完成时会强制补推一次当前状态。
    private bool _pageReady;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;
    private event Action<bool>? MaximizedChanged;
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
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        _messageHandler = new MacOSWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _nsWindow;

    public OrielApp App => _app;
    internal bool IsOnUiThread() => _backend.IsOnUiThread();
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }
    event Action<bool>? IWindowBackend.MaximizedChanged { add => MaximizedChanged += value; remove => MaximizedChanged -= value; }
    event Action<string>? IWindowBackend.NavigationStarting { add => NavigationStarting += value; remove => NavigationStarting -= value; }
    event Action<OrielNavigationCompletedEventArgs>? IWindowBackend.NavigationCompleted { add => NavigationCompleted += value; remove => NavigationCompleted -= value; }
    event Action<OrielConsoleMessageEventArgs>? IWindowBackend.ConsoleMessage { add => ConsoleMessage += value; remove => ConsoleMessage -= value; }
    event Action<OrielMessageReceivedEventArgs>? IWindowBackend.MessageReceived { add => MessageReceived += value; remove => MessageReceived -= value; }
    event Action<string>? IWindowBackend.ContextMenuItemClicked { add => ContextMenuItemClicked += value; remove => ContextMenuItemClicked -= value; }
    event Action<OrielFileDropEventArgs>? IWindowBackend.FileDropped { add => FileDropped += value; remove => FileDropped -= value; }

    public bool IsMaximized => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed"));

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

    public void PostToUiThread(Action action) => _backend.PostToMainThread(action);

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
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 菜单 role「{role}」在 macOS 上未被处理。");
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
                    App.Guard.Token,
                    App.Guard.TrustedPrefixes,
                    ReadSystemSnapshot(), _options.DragRegionSelector)),
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

        ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));

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
        // initWithContentsOfFile: 要的是 NSString*（不是 char*）：必须用 MakeNSString 造对象，
        // 直接传 C# 字符串会被 marshal 成 char*，ObjC 侧一问类型就 trap（见 ObjCRuntime 的注释）。
        if (!string.IsNullOrEmpty(_options.Icon))
        {
            nint image = ObjCRuntime.SendIdObj(
                ObjCRuntime.SendId(ObjCRuntime.GetClass("NSImage"), ObjCRuntime.Sel("alloc")),
                ObjCRuntime.Sel("initWithContentsOfFile:"),
                ObjCRuntime.MakeNSString(_options.Icon));
            if (image != 0)
            {
                ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), image);
            }
        }

        Navigate();
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

    internal bool OnWindowShouldClose()
    {
        var args = new OrielCloseRequestEventArgs();
        Closing?.Invoke(args);
        return !args.Cancel;
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

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    internal void OnNavigationStarted()
    {
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

    public void MoveTo(int x, int y)
    {
        // x/y 按 cocoa 左下角坐标处理（与 Windows/Linux 的左上角原点语义相反）
        _cocoaX = x;
        _cocoaY = y;
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setFrameOrigin:"), _cocoaX, _cocoaY);
    }

    public void Resize(int width, int height)
    {
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setContentSize:"), width, height);
    }

    public void Center() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));

    /// <summary>
    /// 注入给页面的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔与缩放。
    /// </summary>
    /// <remarks>
    /// 两者都是"取不到就返回 0"的形式（没有显示器时 <c>mainScreen</c> 是 nil、框架未加载时类查不到），
    /// 由 <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底。注意
    /// <c>+[NSEvent doubleClickInterval]</c> 给的是**秒**，注入给页面的是毫秒。
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
            // 把回执 JSON 转成 _onResult(id, ok, payload) 的页内求值
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt32();
            var ok = root.GetProperty("ok").GetBoolean();
            string payload = ok
                ? root.GetProperty("value").GetRawText()
                : JsonText.EncodeString(root.GetProperty("error").GetString());
            var js = $"window.oriel._onResult({id}, {(ok ? "true" : "false")}, {payload})";
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
