using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 窗口宿主：NSWindow + WKWebView，全部经 objc_msgSend。
/// 窗口原点/尺寸由 JS 侧提供 + 宿主跟踪增量（不做 struct 返回的 msgSend）。
/// </summary>
internal sealed class MacOSWindowHost : IWindowBackend
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
    private readonly MacOSPlatformBackend _backend;
    private readonly string? _assetDirectory;
    private readonly MacOSWebMessageHandler _messageHandler;

    private nint _nsWindow;
    private nint _nsWindowDelegate;
    private nint _webview;
    private nint _navigationDelegate;
    private nint _scriptHandler;
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

    internal MacOSWindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory, MacOSPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app;
        _backend = backend;
        _assetDirectory = assetDirectory;
        _title = options.Title;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        _messageHandler = new MacOSWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _nsWindow;

    internal OrielApp App => _app;
    internal bool IsOnUiThread() => _backend.IsOnUiThread();
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }
    event Action<bool>? IWindowBackend.MaximizedChanged { add => MaximizedChanged += value; remove => MaximizedChanged -= value; }

    public bool IsMaximized => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed"));

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
        var userScript = ObjCRuntime.SendIdUtf8NintBool(
            ObjCRuntime.SendId(clsWKUserScript, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            MacOSBridgeJs.Script,
            0, // WKUserScriptInjectionTimeAtDocumentStart
            true);
        ObjCRuntime.SendVoidObj(userContentController, ObjCRuntime.Sel("addUserScript:"), userScript);

        _webview = ObjCRuntime.SendIdDouble4Obj(
            ObjCRuntime.SendId(clsWKWebView, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithFrame:configuration:"),
            0, 0, width, height,
            config);
        ObjCRuntime.objc_retain(_webview);

        _navigationDelegate = MacOSObjCClasses.CreateNavigationDelegate(this);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setNavigationDelegate:"), _navigationDelegate);

        // WebView 填满窗口内容区，并随窗口尺寸自动调整
        var contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        ObjCRuntime.SendVoidDouble4(_webview, ObjCRuntime.Sel("setFrame:"), 0, 0, width, height);
        ObjCRuntime.SendVoidObj(contentView, ObjCRuntime.Sel("addSubview:"), _webview);
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

        Navigate();
    }

    private static nint SharedApplication()
        => ObjCRuntime.SendId(ObjCRuntime.GetClass("NSApplication"), ObjCRuntime.Sel("sharedApplication"));

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            var url = ObjCRuntime.SendIdUtf8(ObjCRuntime.GetClass("NSURL"), ObjCRuntime.Sel("URLWithString:"), externalUrl);
            var request = ObjCRuntime.SendIdObj(ObjCRuntime.GetClass("NSURLRequest"), ObjCRuntime.Sel("requestWithURL:"), url);
            ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("loadRequest:"), request);
            return;
        }

        // 内嵌资产：loadFileURL（读权限限定在 www 目录内）
        if (_assetDirectory is not null)
        {
            var indexHtml = Path.Combine(_assetDirectory, "index.html");
            var fileUrl = ObjCRuntime.SendIdObjBool(
                ObjCRuntime.GetClass("NSURL"),
                ObjCRuntime.Sel("fileURLWithPath:isDirectory:"),
                ObjCRuntime.MakeNSString(indexHtml),
                false);
            var wwwUrl = ObjCRuntime.SendIdObjBool(
                ObjCRuntime.GetClass("NSURL"),
                ObjCRuntime.Sel("fileURLWithPath:isDirectory:"),
                ObjCRuntime.MakeNSString(_assetDirectory),
                true);
            ObjCRuntime.SendVoidObjObj(_webview, ObjCRuntime.Sel("loadFileURL:allowingReadAccessToURL:"), fileUrl, wwwUrl);
        }
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
        _nsWindowDelegate = 0;
        _navigationDelegate = 0;
        _scriptHandler = 0;

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    internal void OnNavigationCompleted(bool success)
    {
        if (success)
        {
            // 新文档不知道当前最大化状态，且下面的 Loaded 只触发首次——所以补推放在早退之前，
            // 每次导航完成都无条件补推一次当前值（与 Windows 的 OnNavigationCompleted 对齐）。
            _pageReady = true;
            SyncMaximizedState(force: true);
        }

        if (success && !_loadedRaised)
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

    public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory)
        => ShowPanel(isSave: false, title);

    public string? ShowSaveFileDialog(string? title, string? filter, string? defaultExtension)
        => ShowPanel(isSave: true, title);

    private string? ShowPanel(bool isSave, string? title)
    {
        var clsName = isSave ? "NSSavePanel" : "NSOpenPanel";
        var panel = ObjCRuntime.SendId(ObjCRuntime.GetClass(clsName), ObjCRuntime.Sel(isSave ? "savePanel" : "openPanel"));
        if (panel == 0)
        {
            return null;
        }

        if (isSave)
        {
            ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setExtensionHidden:"), true);
        }
        else
        {
            ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseFiles:"), true);
            ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseDirectories:"), false);
        }
        if (title is not null)
        {
            ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setMessage:"), ObjCRuntime.MakeNSString(title));
        }

        var response = ObjCRuntime.SendId(panel, ObjCRuntime.Sel("runModal"));
        if (response != 1) // NSModalResponseOK
        {
            return null;
        }

        var urls = ObjCRuntime.SendId(panel, ObjCRuntime.Sel("URLs"));
        var first = ObjCRuntime.SendId(urls, ObjCRuntime.Sel("firstObject"));
        if (first == 0)
        {
            return null;
        }
        var path = ObjCRuntime.SendId(first, ObjCRuntime.Sel("path"));
        return path == 0 ? null : ObjCRuntime.ToManagedString(path);
    }
}
