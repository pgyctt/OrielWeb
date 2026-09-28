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
internal sealed class LinuxWindowHost : IWindowBackend
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

    // 流式拖动状态（GTK 设备像素坐标）
    private (int X, int Y)? _dragPointerStart;
    private (int X, int Y)? _dragWindowOrigin;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;

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

    internal OrielApp App => _app;
    internal bool IsOnUiThread() => _backend.IsOnUiThread();
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }

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

        var userScript = GtkNative.WebkitUserScriptNew(
            LinuxBridgeJs.Script,
            0, // WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START
            1, // WEBKIT_USER_SCRIPT_INJECT_MAIN_FRAME
            0,
            0);
        GtkNative.WebkitUserContentManagerAddUserScript(_userContentManager, userScript);

        // 信号连接（先注册状态，再连接信号）
        LinuxSignalHandlers.RegisterWindow(_gtkWindow, this);
        LinuxSignalHandlers.RegisterWebview(_webview, this);
        LinuxSignalHandlers.RegisterManager(_userContentManager, _messageHandler);
        LinuxSignalHandlers.ConnectSignals(_gtkWindow, _webview, _userContentManager);

        GtkNative.GtkContainerAdd(_gtkWindow, _webview);

        if (_options.Center)
        {
            GtkNative.GtkWindowSetPosition(_gtkWindow, GtkWinPosCenter);
        }

        GtkNative.GtkWidgetShowAll(_gtkWindow);

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

    internal void OnLoadFinished()
    {
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

    public void ToggleMaximize()
    {
        if (GtkNative.GtkWindowIsMaximized(_gtkWindow))
        {
            GtkNative.GtkWindowUnmaximize(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowMaximize(_gtkWindow);
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
