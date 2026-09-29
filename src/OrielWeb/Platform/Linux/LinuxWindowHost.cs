using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 窗口宿主：GTK3 窗口 + WebKitGTK WebView。
/// IPC 回执/ExecuteScript 走页面回环消息（与 macOS 同一模式）。
/// 注意：GTK3 坐标为设备像素；HiDPI 缩放下的拖动偏移为已知限制（M4）。
/// </summary>
internal sealed partial class LinuxWindowHost : IWindowBackend
{
    private const int GtkWinPosCenter = 1;
    private const int GtkResponseOk = -5;
    private const int WebkitLoadFinished = 3;

    private readonly WebviewWindow _window;
    private readonly OrielWindowOptions _options;
    private readonly OrielApp _app;
    private readonly LinuxPlatformBackend _backend;
    private readonly string? _assetDirectory;
    private readonly LinuxWebMessageHandler _messageHandler;

    private nint _gtkWindow;
    private nint _webview;
    private nint _userContentManager;
    private volatile bool _loadedRaised;

    private int _minWidth;
    private int _minHeight;
    private bool _isFullscreen;
    private bool _isOnTop;
    private string _title;
    private int _evalSeq;

    // 上一次已上报（托管事件 + 页面）的最大化状态。GTK 的最大化经窗口管理器异步生效：
    // 调用 gtk_window_maximize 后立刻读 is_maximized 拿到的仍是旧值，因此状态只能由
    // window-state-event 信号确认后再上报——见 SyncMaximizedState。
    private bool _wasMaximized;
    // 页面是否已加载完成（桥接脚本就绪）。就绪前不推事件；加载完成时会强制补推一次当前状态。
    private bool _pageReady;
    // 本次加载是否已经失败过。WebKit 加载失败后会渲染错误页并再发一次 load-changed(FINISHED)，
    // 这个标记用来避免把那一趟当成"又一次成功导航"上报。
    private bool _failedSinceLoadStart;

    // 流式拖动状态（GTK 设备像素坐标）
    private (int X, int Y)? _dragPointerStart;
    private (int X, int Y)? _dragWindowOrigin;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;
    private event Action<bool>? MaximizedChanged;
    private event Action<string>? NavigationStarting;
    private event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted;
    private event Action<OrielConsoleMessageEventArgs>? ConsoleMessage;
    private event Action<OrielMessageReceivedEventArgs>? MessageReceived;

    internal LinuxWindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory, LinuxPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app;
        _backend = backend;
        _assetDirectory = assetDirectory;
        _title = options.Title;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        _messageHandler = new LinuxWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _gtkWindow;

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

    public bool IsMaximized => GtkNative.GtkWindowIsMaximized(_gtkWindow);

    // ---- 导航操作（WebKitGTK 内建历史；无历史时调用是空操作）----
    // _webview 在 destroy 之后被置 0，判空是必需的：这些调用不能对已销毁的 webview 下手。

    public bool CanGoBack => _webview != 0 && GtkNative.WebkitWebViewCanGoBack(_webview) != 0;
    public bool CanGoForward => _webview != 0 && GtkNative.WebkitWebViewCanGoForward(_webview) != 0;

    public void GoBack()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewGoBack(_webview);
        }
    }

    public void GoForward()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewGoForward(_webview);
        }
    }

    public void Reload()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewReload(_webview);
        }
    }

    public void PostToUiThread(Action action) => _backend.PostToMainThread(action);

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    internal void Create()
    {
        _gtkWindow = GtkNative.GtkWindowNew(0); // GTK_WINDOW_TOPLEVEL
        GtkNative.GtkWindowSetDefaultSize(_gtkWindow, _options.Width, _options.Height);
        GtkNative.GtkWindowSetTitle(_gtkWindow, _options.Title);
        if (_options.Frameless)
        {
            GtkNative.GtkWindowSetDecorated(_gtkWindow, false);
        }

        if (_minWidth > 0 || _minHeight > 0)
        {
            GtkNative.GtkWidgetSetSizeRequest(_gtkWindow, Math.Max(_minWidth, 1), Math.Max(_minHeight, 1));
        }

        _userContentManager = GtkNative.WebkitUserContentManagerNew();
        GtkNative.WebkitUserContentManagerRegisterScriptMessageHandler(_userContentManager, "oriel");
        _webview = GtkNative.WebkitWebViewNewWithUserContentManager(_userContentManager);

        // DevTools：与 Windows 的 AreDevToolsEnabled 语义一致——只"允许"检查，不自动打开面板
        // （打开 enable_developer_extras 后，WebKitGTK 的右键菜单会出现「检查元素」）。
        // settings 交给 webview 后由它持有引用，所以本地这一份引用即刻释放。
        var webkitSettings = GtkNative.WebkitSettingsNew();
        GtkNative.WebkitSettingsSetEnableDeveloperExtras(webkitSettings, _options.Debug);
        GtkNative.WebkitWebViewSetSettings(_webview, webkitSettings);
        GtkNative.GObjectUnref(webkitSettings);

        var userScript = GtkNative.WebkitUserScriptNew(
            LinuxBridgeJs.Build(_options.ConsoleForwarding),
            1, // WEBKIT_USER_CONTENT_INJECT_TOP_FRAME（主帧）
            0, // WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START（文档开始处注入，与 Windows/macOS 后端一致）
            0,
            0);
        GtkNative.WebkitUserContentManagerAddScript(_userContentManager, userScript);

        // 信号连接（先注册状态，再连接信号）
        LinuxSignalHandlers.RegisterWindow(_gtkWindow, this);
        LinuxSignalHandlers.RegisterWebview(_webview, this);
        LinuxSignalHandlers.RegisterManager(_userContentManager, _messageHandler);
        LinuxSignalHandlers.ConnectSignals(_gtkWindow, _webview, _userContentManager);

        GtkNative.GtkContainerAdd(_gtkWindow, _webview);

        // 窗口图标：X11 下写入 _NET_WM_ICON（可用 xprop 验证）；Wayland 下由合成器决定，通常忽略。
        if (!string.IsNullOrEmpty(_options.Icon))
        {
            _ = GtkNative.GtkWindowSetIconFromFile(_gtkWindow, _options.Icon, 0);
        }

        if (_options.Center)
        {
            GtkNative.GtkWindowSetPosition(_gtkWindow, GtkWinPosCenter);
        }

        // 隐藏启动：先 show_all 把窗口与 webview 都 realize 出来（GTK 的 realize 是从顶层向下
        // 传播的，不 show 的话 webview 不会被 realize，页面可能推迟到窗口可见才开始加载），
        // 再立刻 hide 顶层窗口——与 Windows 的 SW_HIDE（窗口存在但不可见）、macOS 的不
        // orderFront 语义一致。代价是 X11 下理论上有 map→unmap 的一帧，换来的是"隐藏状态下
        // 页面照常加载、IPC 照常往返"。
        GtkNative.GtkWidgetShowAll(_gtkWindow);
        if (_options.Hidden)
        {
            GtkNative.GtkWidgetHide(_gtkWindow);
        }

        if (_options.OnTop)
        {
            SetOnTop(true);
        }
        if (_options.Maximized)
        {
            GtkNative.GtkWindowMaximize(_gtkWindow);
        }
        if (_options.Fullscreen)
        {
            GtkNative.GtkWindowFullscreen(_gtkWindow);
        }

        Navigate();
    }

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            GtkNative.WebkitWebViewLoadUri(_webview, externalUrl);
            return;
        }

        if (_assetDirectory is not null)
        {
            var indexHtml = Path.Combine(_assetDirectory, "index.html");
            GtkNative.WebkitWebViewLoadUri(_webview, new Uri(indexHtml).AbsoluteUri);
        }
    }

    // ------------------------------------------------------------------
    // 生命周期回调（由 LinuxSignalHandlers 转发，UI 线程）
    // ------------------------------------------------------------------

    internal bool OnWindowShouldClose()
    {
        var args = new OrielCloseRequestEventArgs();
        Closing?.Invoke(args);
        return !args.Cancel;
    }

    internal void OnWindowDestroyed()
    {
        // 先清理状态注册表与指针，再触发 Closed——避免用户在回调里发起 IPC 时
        // 走到已销毁的宿主，也避免 GTK 释放 webview 后同地址被新窗口复用造成
        // 陈旧映射（ABA）。注册表持有托管 host 的强引用，不清理则对象树永不释放。
        // 时机由 GTK 的 destroy 信号回调（OnDestroyTrampoline）保证，正是真实销毁点。
        LinuxSignalHandlers.UnregisterWebview(_webview);
        LinuxSignalHandlers.UnregisterManager(_userContentManager);
        _webview = 0;
        _userContentManager = 0;

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    internal void OnLoadStarted()
    {
        _failedSinceLoadStart = false;
        RaiseNavigationStarting(CurrentUri());
    }

    /// <summary>
    /// 整页加载失败（WebKitGTK 的 load-failed）。失败之后 WebKit 仍会发一次 load-changed 的
    /// FINISHED（加载的是错误页），所以这里只上报"失败"这一次，不把它也算成一次成功导航。
    /// </summary>
    internal void OnLoadFailed(string failingUri, string? error)
    {
        _failedSinceLoadStart = true;
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(false, failingUri, error));
    }

    internal void OnLoadFinished()
    {
        // 新文档不知道当前最大化状态，且下面的 Loaded 只触发首次——所以补推放在早退之前，
        // 每次加载完成都无条件补推一次当前值（与 Windows 的 OnNavigationCompleted 对齐）。
        _pageReady = true;
        SyncMaximizedState(force: true);

        // 失败之后这一趟 FINISHED 加载的是错误页：失败已经上报过，不再重复报成功（否则调用方
        // 会先收到"失败"、再收到一条矛盾的"成功"）。
        if (!_failedSinceLoadStart)
        {
            RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(true, CurrentUri(), null));
        }

        if (_loadedRaised)
        {
            return;
        }
        _loadedRaised = true;
        Loaded?.Invoke();

        var titlePtr = GtkNative.WebkitWebViewGetTitle(_webview);
        if (titlePtr != 0)
        {
            var title = Marshal.PtrToStringUTF8(titlePtr);
            if (!string.IsNullOrEmpty(title))
            {
                RaiseTitleChanged(title);
            }
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        TitleChanged?.Invoke(title);
        GtkNative.GtkWindowSetTitle(_gtkWindow, title);
    }

    internal void RaiseNavigationStarting(string url)
    {
        NavigationStarting?.Invoke(url);
        PushEventOnUi("navigation.starting", $"{{\"url\":{JsonText.EncodeString(url)}}}");
    }

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

    /// <summary>当前文档 URI；加载中/销毁后可能取不到，返回空串。</summary>
    private string CurrentUri()
    {
        if (_webview == 0)
        {
            return string.Empty;
        }

        nint uriPtr = GtkNative.WebkitWebViewGetUri(_webview);
        return uriPtr == 0 ? string.Empty : Marshal.PtrToStringUTF8(uriPtr) ?? string.Empty;
    }

    // ------------------------------------------------------------------
    // IWindowBackend
    // ------------------------------------------------------------------
    public void Show() => GtkNative.GtkWidgetShowAll(_gtkWindow);
    public void Hide() => GtkNative.GtkWidgetHide(_gtkWindow);
    public void Close() => GtkNative.GtkWindowClose(_gtkWindow);
    public void Focus() => GtkNative.GtkWindowPresent(_gtkWindow);
    public void Maximize() => GtkNative.GtkWindowMaximize(_gtkWindow);
    public void Minimize() => GtkNative.GtkWindowIconify(_gtkWindow);

    public void Restore()
    {
        if (GtkNative.GtkWindowIsMaximized(_gtkWindow))
        {
            GtkNative.GtkWindowUnmaximize(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowPresent(_gtkWindow);
        }
    }

    public bool ToggleMaximize()
    {
        bool willBeMaximized = !GtkNative.GtkWindowIsMaximized(_gtkWindow);
        if (willBeMaximized)
        {
            GtkNative.GtkWindowMaximize(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowUnmaximize(_gtkWindow);
        }

        // 返回的是"意图值"，不是读回的真实状态：maximize/unmaximize 只是向窗口管理器发请求，
        // 此处立刻读 gtk_window_is_maximized 拿到的仍是切换前的旧值（这正是"图标反了"的根因，
        // 页面用返回值驱动图标，于是显示成相反状态）。真实状态由 window-state-event 确认后经
        // SyncMaximizedState 上报；万一 WM 拒绝请求，那次上报会把页面纠正回真实值。
        return willBeMaximized;
    }

    /// <summary>
    /// 读取当前最大化状态，与上次上报值比对，变化时（或 <paramref name="force"/> 时）上报给托管事件与页面。
    /// 调用时机：GTK 的 window-state-event（WM 确认状态变化后）、每次页面加载完成。
    /// </summary>
    internal bool SyncMaximizedState(bool force = false)
    {
        bool isMaximized = GtkNative.GtkWindowIsMaximized(_gtkWindow);
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
            GtkNative.WebkitWebViewEvaluateJavaScript(_webview, js, -1, 0, 0, 0, 0, 0);
        }
        catch
        {
            // 窗口销毁后的迟到事件，忽略
        }
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled == _isFullscreen)
        {
            return;
        }
        if (enabled)
        {
            GtkNative.GtkWindowFullscreen(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowUnfullscreen(_gtkWindow);
        }
        _isFullscreen = enabled;
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        GtkNative.GtkWindowSetKeepAbove(_gtkWindow, enabled);
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    public void SetTitle(string title)
    {
        _title = title;
        GtkNative.GtkWindowSetTitle(_gtkWindow, title);
    }

    public void SetResizable(bool enabled) => GtkNative.GtkWindowSetResizable(_gtkWindow, enabled);

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        GtkNative.GtkWidgetSetSizeRequest(_gtkWindow, Math.Max(width, 0), Math.Max(height, 0));
    }

    public void MoveTo(int x, int y) => GtkNative.GtkWindowMove(_gtkWindow, x, y);
    public void Resize(int width, int height) => GtkNative.GtkWindowResize(_gtkWindow, width, height);

    public void Center()
    {
        var screen = GtkNative.GtkWindowGetScreen(_gtkWindow);
        var screenW = GtkNative.GdkScreenGetWidth(screen);
        var screenH = GtkNative.GdkScreenGetHeight(screen);
        GtkNative.GtkWindowGetSize(_gtkWindow, out var w, out var h);
        GtkNative.GtkWindowMove(_gtkWindow, Math.Max((screenW - w) / 2, 0), Math.Max((screenH - h) / 2, 0));
    }

    public void BeginDrag()
    {
        // Linux 无边框拖动由 JS 流式坐标驱动（win.dragStart 提供起点，DragTo 应用增量）。
    }

    public void BeginDragStreaming(double px, double py, double winX, double winY, double winW, double winH, double screenH)
    {
        _dragPointerStart = ((int)px, (int)py);
        GtkNative.GtkWindowGetPosition(_gtkWindow, out var curX, out var curY);
        _dragWindowOrigin = (curX, curY);
    }

    public void DragTo(double pointerDx, double pointerDy)
    {
        if (_dragPointerStart is null || _dragWindowOrigin is null)
        {
            return;
        }
        GtkNative.GtkWindowMove(
            _gtkWindow,
            _dragWindowOrigin.Value.X + (int)pointerDx,
            _dragWindowOrigin.Value.Y + (int)pointerDy);
    }

    public void EndDrag() => _dragPointerStart = null;

    public Task<string> ExecuteScriptAsync(string script)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _evalSeq);
        _messageHandler.RegisterEval(id, completion);

        // 页内求值并把结果经消息处理器回传（避免 GAsyncReadyCallback）
        var scriptJson = JsonText.EncodeString(script);
        var js = "window.oriel._evalScriptDone(" + id + ", JSON.stringify((function(){try{return eval(" + scriptJson +
                 ")}catch(e){return 'E:'+String(e)}})()))";
        PostToMainThread(() =>
        {
            GtkNative.WebkitWebViewEvaluateJavaScript(
                _webview, js, -1, 0, 0, 0, 0, 0);
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
            GtkNative.WebkitWebViewEvaluateJavaScript(
                _webview, js, -1, 0, 0, 0, 0, 0);
        }
        catch
        {
            // 窗口销毁后的迟到回执，忽略
        }
    }

    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        var type = icon switch
        {
            OrielMessageBoxIcon.Warning => 1,  // GTK_MESSAGE_WARNING
            OrielMessageBoxIcon.Error => 3,    // GTK_MESSAGE_ERROR
            OrielMessageBoxIcon.Question => 2, // GTK_MESSAGE_QUESTION
            _ => 0,                             // GTK_MESSAGE_INFO
        };
        var dialog = GtkNative.GtkMessageDialogNew(_gtkWindow, 0, type, 0 /*GTK_BUTTONS_NONE*/, "%s", text);
        GtkNative.GtkDialogAddButton(dialog, "确定", GtkResponseOk);
        GtkNative.GtkDialogRun(dialog);
        GtkNative.GtkWidgetDestroy(dialog);
    }

    // ---- 文件对话框（GtkFileChooserDialog）----

    public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory)
        => ShowPanel(isSave: false, title, filter, defaultExtension: null);

    public string? ShowSaveFileDialog(string? title, string? filter, string? defaultExtension)
        => ShowPanel(isSave: true, title, filter, defaultExtension);

    private string? ShowPanel(bool isSave, string? title, string? filter, string? defaultExtension)
    {
        const int FileChooserActionOpen = 0;
        const int FileChooserActionSave = 1;
        var action = isSave ? FileChooserActionSave : FileChooserActionOpen;

        var dialog = GtkNative.GtkFileChooserDialogNew(
            title ?? (isSave ? "保存" : "打开"),
            _gtkWindow,
            action,
            0); // varargs 终止：无内置按钮
        GtkNative.GtkDialogAddButton(dialog, isSave ? "保存" : "打开", GtkResponseOk);

        if (isSave && !string.IsNullOrEmpty(defaultExtension))
        {
            GtkNative.GtkFileChooserSetCurrentName(dialog, "未命名." + defaultExtension);
            GtkNative.GtkFileChooserSetDoOverwriteConfirmation(dialog, true);
        }

        var response = GtkNative.GtkDialogRun(dialog);
        string? path = null;
        if (response == GtkResponseOk)
        {
            var filename = GtkNative.GtkFileChooserGetFilename(dialog);
            if (filename != 0)
            {
                path = Marshal.PtrToStringUTF8(filename);
                GtkNative.GFree(filename);
            }
        }
        GtkNative.GtkWidgetDestroy(dialog);
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
