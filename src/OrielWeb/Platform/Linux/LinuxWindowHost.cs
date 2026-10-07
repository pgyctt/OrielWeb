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
    private const int GtkResponseCancel = -6;
    private const int WebkitLoadFinished = 3;

    private readonly WebviewWindow _window;
    private readonly OrielWindowOptions _options;
    private readonly OrielApp _app;

    /// <summary>
    /// 本窗口的门面对象。内建 <c>win.*</c> 命令要靠它作用到**发起调用的那个窗口**上
    /// （多窗口下不能用一个全局的"当前窗口"代替）。
    /// </summary>
    internal WebviewWindow Window => _window;
    private readonly LinuxPlatformBackend _backend;
    private readonly EmbeddedAssetStore? _assets;
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

    // 拖动状态（GTK 设备像素坐标）。两种形态互斥：
    //  * X11：页面送来起点，之后按增量调 gtk_window_move（流式跟随）；
    //  * Wayland：把移动一次性交给合成器（gtk_window_begin_move_drag），宿主不再插手。
    private (int X, int Y)? _dragPointerStart;
    private (int X, int Y)? _dragWindowOrigin;
    // 当前 GDK 后端是否 Wayland；首次拖动时判定一次（拖动路径上不必每次都问 GDK）。
    private bool? _isWayland;
    // 本次拖动是否已经交给合成器——决定 DragTo 该不该插手。
    private bool _waylandDrag;
    // Wayland 下"按下了、但还没真交给合成器"：等指针动过阈值再交，否则双击会被吞掉（见 LinuxDragSupport）。
    private bool _waylandDragPending;
    // 已安排过一次"下一轮主循环再读最大化状态"，避免连续事件重复排队。
    private bool _maximizedSyncScheduled;

    // 无边框窗口的边缘 resize。窗口一旦 set_decorated(false)，WM 就不再提供 resize 边框，
    // 只能自己判边缘命中、再把 resize 交回给 WM/合成器（见 LinuxResizeSupport）。
    private bool _handleResizeEdges;
    // 指针当前是否停在边缘热区上（用于"离开时把光标交回去"这件事只做一次）。
    private bool _pointerInResizeBorder;
    // 按 GdkWindowEdge 索引缓存光标：motion 事件很密，每次现造就太浪费了。
    // 句柄归本对象所有（gdk_cursor_new_for_display 返回新引用），销毁时统一 unref。
    private readonly nint[] _resizeCursors = new nint[8];
    // 当前已设上去的是哪条边的光标；-1 = 没设。
    private int _activeCursorEdge = -1;

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

    /// <summary>渲染引擎内建右键菜单的策略；见 <see cref="OrielContextMenuPolicy"/>。</summary>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>
    /// 内建右键菜单即将弹出（由 <c>context-menu</c> 信号的 trampoline 调进来）。
    /// </summary>
    /// <param name="menu">WebKit 构造好的菜单；接管式用不上它，但要保留参数以便对照调用点。</param>
    /// <param name="gdkEvent">触发右键的 <c>GdkEvent</c>，交给自建菜单做定位。</param>
    /// <returns>
    /// <c>true</c> = 我们接管，WebKit 什么都不弹；<c>false</c> = 让 WebKit 弹它自己的菜单。
    /// </returns>
    /// <remarks>
    /// <b>不碰</b> WebKit 传进来的那个菜单对象。早先的做法是就地增删它的项，但那要经
    /// <c>webkit_context_menu_get_items</c> 配合 <c>webkit_context_menu_remove</c> / <c>g_list_free</c>，
    /// 在本环境的 WebKitGTK 4.1 上会破坏菜单内部结构——连点几次右键就 double free / 栈保护被破坏。
    /// 现在改为自己弹菜单，编辑命令走渲染引擎的 API（见 <see cref="TryExecuteNativeEditing"/>）。
    /// </remarks>
    internal bool FilterContextMenu(nint menu, nint gdkEvent)
    {
        _ = menu; // 接管式用不上 WebKit 的菜单对象

        switch (ContextMenuPolicy)
        {
            case OrielContextMenuPolicy.Native:
                return false; // 平台原样，交给 WebKit 弹

            case OrielContextMenuPolicy.Disabled:
                return true; // 接管且什么都不弹 = 右键无反应

            default:
                ShowEditingMenu(gdkEvent);
                return true;
        }
    }

    /// <summary>
    /// 接管式菜单的内容：只含剪切 / 复制 / 粘贴（<see cref="OrielContextMenuPolicy.Editing"/> 的语义）。
    /// </summary>
    private void ShowEditingMenu(nint gdkEvent)
    {
        _contextMenu?.Dispose();
        _contextMenu = GtkMenu.Build(OrielMenuRoles.EditingMenuItems(), ActivateMenuItem);
        _contextMenu?.Popup(gdkEvent);
    }

    /// <summary>
    /// 把编辑类 role 落到渲染引擎自己的编辑命令上（<c>WEBKIT_EDITING_COMMAND_*</c>）。
    /// </summary>
    /// <remarks>
    /// 接管式菜单的剪切/复制/粘贴必须走这里：<c>document.execCommand('cut'/'paste')</c> 会被
    /// webview 的安全策略拦下，而粘贴还要把系统剪贴板送进页面，只有原生侧做得到。
    /// </remarks>
    private bool TryExecuteNativeEditing(string role)
    {
        if (_webview == 0)
        {
            return false;
        }

        string? command = role switch
        {
            OrielMenuRole.Cut => "Cut",
            OrielMenuRole.Copy => "Copy",
            OrielMenuRole.Paste => "Paste",
            OrielMenuRole.Undo => "Undo",
            OrielMenuRole.Redo => "Redo",
            OrielMenuRole.SelectAll => "SelectAll",
            OrielMenuRole.Delete => "Delete",
            _ => null,
        };

        if (command is null)
        {
            return false;
        }

        GtkNative.WebkitWebViewExecuteEditingCommand(_webview, command);
        return true;
    }

    internal LinuxWindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, LinuxPlatformBackend backend)
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
    event Action<string>? IWindowBackend.ContextMenuItemClicked { add => ContextMenuItemClicked += value; remove => ContextMenuItemClicked -= value; }

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
    // 上下文菜单
    // ------------------------------------------------------------------

    private GtkMenu? _contextMenu;

    /// <summary>
    /// 上下文菜单：构建 → 在指针位置弹出。
    /// </summary>
    /// <remarks>
    /// <b>弹出是异步的</b>（GTK 立即返回，用户之后才选），所以菜单对象留到下一次弹出前才释放——
    /// 不能像 Windows 那样"弹完即抛"。窗口销毁时随进程一并回收（一个菜单对象的开销可忽略）。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
    {
        _contextMenu?.Dispose();
        _contextMenu = GtkMenu.Build(items, item => ActivateMenuItem(item));
        _contextMenu?.Popup();
    }

    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window, TryExecuteNativeEditing))
            {
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 菜单 role「{role}」在 Linux 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }

    // ------------------------------------------------------------------
    // 无边框窗口的边缘 resize
    // ------------------------------------------------------------------

    /// <summary>
    /// 指针在 webview 上移动（<c>motion-notify-event</c>）：进到边缘热区就换成对应的 resize 光标。
    /// </summary>
    /// <returns>TRUE = 已处理并**吞掉**该事件（不再交给 WebKit）。</returns>
    /// <remarks>
    /// 必须吞掉：WebKit 的默认处理紧接着会按"指针下面是链接还是文本"重设光标，我们刚设的那个会被盖掉，
    /// 表现为光标闪烁。代价是热区那几像素里页面收不到 <c>mousemove</c>——这与 Windows 上
    /// <c>WM_NCHITTEST</c> 把边缘像素交给 WM 是同一件事，不是 Linux 特有的损失。
    /// </remarks>
    internal bool OnPointerMotionForResize(nint gdkEvent)
    {
        if (!_handleResizeEdges || gdkEvent == 0)
        {
            return false;
        }

        GdkWindowEdge? edge = ResolvePointerEdge(gdkEvent);
        if (edge is null)
        {
            if (_pointerInResizeBorder)
            {
                _pointerInResizeBorder = false;
                _activeCursorEdge = -1;
                SetWebviewCursor(0); // 交回默认；WebKit 下一次 motion 会按内容重设
            }

            return false;
        }

        _pointerInResizeBorder = true;
        if (_activeCursorEdge != (int)edge.Value)
        {
            _activeCursorEdge = (int)edge.Value;
            SetWebviewCursor(ResizeCursor(edge.Value));
        }

        return true;
    }

    /// <summary>
    /// 在 webview 上按下鼠标（<c>button-press-event</c>）：在边缘热区按下左键时，把 resize 交给
    /// 窗口管理器/合成器（X11 走 WM、Wayland 转成 <c>xdg_toplevel.resize</c>）。
    /// </summary>
    /// <returns>TRUE = 已接管这次按下（不再交给 WebKit）。</returns>
    internal bool OnButtonPressForResize(nint gdkEvent)
    {
        if (!_handleResizeEdges || gdkEvent == 0)
        {
            return false;
        }

        GdkWindowEdge? edge = ResolvePointerEdge(gdkEvent);
        if (edge is null)
        {
            return false;
        }

        // 只接管左键：右键要留给上下文菜单
        if (GtkNative.GdkEventGetButton(gdkEvent, out uint button) == 0 || button != 1)
        {
            return false;
        }

        _ = GtkNative.GdkEventGetRootCoords(gdkEvent, out double rootX, out double rootY);

        // 与 gtk_window_begin_move_drag 同理：Wayland 下必须在**按住期间**发出——协议只吃
        // seat + serial，而 GTK3 取的是最近一次隐式抓取的 serial。这里就在 button-press 里同步调用，
        // 天然满足该时序；挪到别处（例如异步的页面命令里）就会被合成器直接忽略。
        GtkNative.GtkWindowBeginResizeDrag(
            _gtkWindow,
            (int)edge.Value,
            (int)button,
            (int)rootX,
            (int)rootY,
            GtkNative.GdkEventGetTime(gdkEvent));

        return true;
    }

    /// <summary>指针落在窗口的哪条边/哪个角上；不在边缘（或事件没有坐标）时返回 null。</summary>
    private GdkWindowEdge? ResolvePointerEdge(nint gdkEvent)
    {
        if (GtkNative.GdkEventGetCoords(gdkEvent, out double x, out double y) == 0)
        {
            return null;
        }

        return LinuxResizeSupport.ResolveEdge(
            x,
            y,
            GtkNative.GtkWidgetGetAllocatedWidth(_webview),
            GtkNative.GtkWidgetGetAllocatedHeight(_webview),
            LinuxResizeSupport.BorderThickness);
    }

    /// <summary>取（并按需创建）某条边对应的光标句柄。</summary>
    private nint ResizeCursor(GdkWindowEdge edge)
    {
        int index = (int)edge;
        if (_resizeCursors[index] == 0)
        {
            nint display = GtkNative.GdkDisplayGetDefault();
            if (display != 0)
            {
                _resizeCursors[index] = GtkNative.GdkCursorNewForDisplay(
                    display, (int)LinuxResizeSupport.CursorFor(edge));
            }
        }

        return _resizeCursors[index];
    }

    /// <summary>把光标设到 **webview 自己的** GdkWindow 上（设到顶层窗口上会被子窗口盖掉）。</summary>
    private void SetWebviewCursor(nint cursor)
    {
        nint gdkWindow = GtkNative.GtkWidgetGetWindow(_webview);
        if (gdkWindow != 0)
        {
            GtkNative.GdkWindowSetCursor(gdkWindow, cursor);
        }
    }

    /// <summary>释放缓存的光标。<c>gdk_cursor_new_for_display</c> 返回的是新引用，归本对象所有。</summary>
    private void ReleaseResizeCursors()
    {
        for (int i = 0; i < _resizeCursors.Length; i++)
        {
            if (_resizeCursors[i] != 0)
            {
                GtkNative.GObjectUnref(_resizeCursors[i]);
                _resizeCursors[i] = 0;
            }
        }

        _activeCursorEdge = -1;
        _pointerInResizeBorder = false;
    }

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
            // 去掉装饰后 WM 就不再提供 resize 边框，"拖边缘改大小"会整个失效（Ubuntu 22.04 实测）。
            // 这里记下要不要自己接管；**不可调整大小的窗口不接管**——接管了也只是白白吃掉边缘那几像素的点击。
            _handleResizeEdges = _options.Resizable;
        }

        if (_minWidth > 0 || _minHeight > 0)
        {
            GtkNative.GtkWidgetSetSizeRequest(_gtkWindow, Math.Max(_minWidth, 1), Math.Max(_minHeight, 1));
        }

        // oriel:// 的注册必须是"任何 webview 创建之前"——这里就是那一刻（本文件唯一建 view 的地方）。
        // 放在导航时注册就晚了：首次导航已经在解析 URL，scheme 还没登记，引擎会当成未知协议。
        if (_assets is not null)
        {
            LinuxAssetScheme.Register(_assets, App.AssetHost);
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

        // 信号连接（先注册状态，再连接信号）
        LinuxSignalHandlers.RegisterWindow(_gtkWindow, this);
        LinuxSignalHandlers.RegisterWebview(_webview, this);
        LinuxSignalHandlers.RegisterManager(_userContentManager, _messageHandler);
        LinuxSignalHandlers.ConnectSignals(_gtkWindow, _webview, _userContentManager);

        // 拖放落点：注册在 webview 上（信号连接在上一条里，载荷解析在 trampoline 里）
        EnableFileDrop(_webview);

        // 边缘 resize：motion 事件默认不投递，要显式加进事件掩码，热区才收得到指针移动
        if (_handleResizeEdges)
        {
            GtkNative.GtkWidgetAddEvents(_webview, GtkNative.GdkPointerMotionMask);
        }

        // 桥接脚本（document 创建时注入）。宿主事实快照在这里取一次：
        // 它读的是显示器与 GtkSettings，都与窗口是否 realize 无关（见 ReadSystemSnapshot）。
        var userScript = GtkNative.WebkitUserScriptNew(
            LinuxBridgeJs.Build(
                _options.ConsoleForwarding,
                App.Guard.Token,
                App.Guard.TrustedPrefixes,
                ReadSystemSnapshot(), _options.DragRegionSelector),
            1, // WEBKIT_USER_CONTENT_INJECT_TOP_FRAME（主帧）
            0, // WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START（文档开始处注入，与 Windows/macOS 后端一致）
            0,
            0);
        GtkNative.WebkitUserContentManagerAddScript(_userContentManager, userScript);

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

    /// <summary>
    /// 注入给页面的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔与缩放。
    /// </summary>
    /// <remarks>
    /// 缩放读**默认显示器**的（而不是窗口的）：脚本在导航之前注册，那一刻窗口可能还没 realize。
    /// 取不到时返回 0，由 <see cref="OrielSystemSnapshot.Normalize"/> 兜底成 1.0。
    /// 多屏不同缩放下这是"按主屏算"的近似值——页面侧有自己的 <c>window.devicePixelRatio</c> 可自校。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
        => OrielSystemSnapshot.Normalize(LinuxPlatformBackend.ReadDoubleClickTimeMs());

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            // 内嵌资源的 URL 交给引擎里的 oriel:// 处理器（见 LinuxAssetScheme），宿主不做"改写成
            // 本地文件"那一步：那种改写要求资源落盘，而且会把页面来源变成 file://（不透明来源）。
            // 兼容别名 https://<host>/… 也在这里被归一成 oriel://，于是页面来源三平台一致。
            if (AssetUrl.TryResolve(externalUrl, _app.AssetHost, out string relative))
            {
                GtkNative.WebkitWebViewLoadUri(_webview, AssetUrl.ForHost(_app.AssetHost, relative));
                return;
            }

            GtkNative.WebkitWebViewLoadUri(_webview, externalUrl);
            return;
        }

        if (_assets is not null)
        {
            GtkNative.WebkitWebViewLoadUri(_webview, AssetUrl.DefaultDocument(_app.AssetHost));
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
        ReleaseResizeCursors();
        _webview = 0;
        _userContentManager = 0;

        // 页面回环随文档一起消失：挂起的 ExecuteScriptAsync 在这里立刻失败，
        // 而不是让调用方的 await 永不返回。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：窗口已销毁，页面回环不会再返回结果。");

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    internal void OnLoadStarted()
    {
        _failedSinceLoadStart = false;

        // 导航开始即原文档开始卸载：它注入的回环脚本不会再回执，先清掉挂起的 eval。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：页面正在离开原文档，回环结果不会再返回。");

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
    /// 安排一次"下一轮主循环再读最大化状态"。由 <c>window-state-event</c> 的 trampoline 调用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不能在信号 handler 里直接读 <c>gtk_window_is_maximized()</c>。</b>GTK 对最大化状态的更新
    /// 发生在 <c>GtkWindow</c> 自己的 class closure 里，而普通 handler（<c>g_signal_connect</c> 系）
    /// 排在 class closure **之前**——此刻读到的还是**旧值**。
    /// </para>
    /// <para>
    /// 症状非常具体，也正好对得上"双击/图标不对"的报法：<b>还原窗口后图标不变，直到窗口失去焦点
    /// 才变</b>。因为焦点变化本身也会带一个 <c>window-state-event</c>（<c>GDK_WINDOW_STATE_FOCUSED</c>
    /// 位变了），那一次才读到正确的值。最大化那一步之所以"看起来是对的"，是因为页面同时还会用
    /// <c>win.toggleMaximize</c> 的返回值（意图值）驱动图标，把问题盖住了。
    /// </para>
    /// <para>
    /// 推迟到下一轮 idle 再读，那时 class closure 已经跑完。连续多个事件合并成一次读
    /// （<see cref="SyncMaximizedState"/> 内部有状态比对去重）。
    /// </para>
    /// </remarks>
    internal void ScheduleMaximizedSync()
    {
        if (_maximizedSyncScheduled)
        {
            return;
        }

        _maximizedSyncScheduled = true;
        _backend.PostToMainThread(() =>
        {
            _maximizedSyncScheduled = false;
            SyncMaximizedState();
        });
    }

    /// <summary>
    /// 读取当前最大化状态，与上次上报值比对，变化时（或 <paramref name="force"/> 时）上报给托管事件与页面。
    /// 调用时机：window-state-event 之后（经 <see cref="ScheduleMaximizedSync"/> 推迟到下一轮主循环）、
    /// 每次页面加载完成。
    /// </summary>
    internal bool SyncMaximizedState(bool force = false)
    {
        // 窗口可能已经在"排队等下一次读"期间销毁了：对 0 句柄调 GTK 会报警告甚至崩
        if (_gtkWindow == 0)
        {
            return false;
        }

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
        // Linux 不走这个入口（它对应 Windows 的 WM_NCLBUTTONDOWN 模态循环）：拖动由页面在
        // mousedown 里经 win.dragStart 发起，宿主再按 GDK 后端决定形态。
    }

    public void BeginDragStreaming(double px, double py, double winX, double winY, double winW, double winH, double screenH)
    {
        if (IsWaylandBackend())
        {
            // Wayland 协议不允许客户端移动自己的窗口（连窗口位置都拿不到），增量这条路根本走不通，
            // 只能把移动交给合成器。**但不能在这里就交出去**——一旦交出去，指针就被合成器 grab，
            // 页面收不到后续事件，第二次点击被吞掉，双击永远凑不齐（"双击标题栏最大化"因此失效）。
            // 所以只记下"按下了"，等指针真的动过阈值（见 DragTo）再交；那时按键仍按着，serial 依然有效。
            _waylandDrag = false;
            _waylandDragPending = true;
            return;
        }

        _waylandDrag = false;
        _waylandDragPending = false;
        _dragPointerStart = ((int)px, (int)py);
        GtkNative.GtkWindowGetPosition(_gtkWindow, out var curX, out var curY);
        _dragWindowOrigin = (curX, curY);
    }

    public void DragTo(double pointerDx, double pointerDy)
    {
        if (_waylandDragPending)
        {
            // 指针还没动过阈值 → 什么都不做。这样"按下不动"（点击/双击）不会把指针交给合成器。
            if (!LinuxDragSupport.ExceedsMoveThreshold(pointerDx, pointerDy))
            {
                return;
            }

            // 动过了：现在交给合成器。左键 = 1；Wayland 下 root 坐标与时间戳都被忽略
            // （GTK3 取的是按钮按下时记录的隐式抓取 serial，此刻按键仍按着，因此有效）。
            GtkNative.GtkWindowBeginMoveDrag(_gtkWindow, 1, 0, 0, 0 /* GDK_CURRENT_TIME */);
            _waylandDrag = true;
            _waylandDragPending = false;
            return;
        }

        if (_waylandDrag)
        {
            // 合成器接管后指针被 grab，页面收不到 mousemove，这里本不该被调到；真被调到也不插手——
            // 双方同时移动同一个窗口没有意义。
            return;
        }

        if (_dragPointerStart is null || _dragWindowOrigin is null)
        {
            return;
        }
        GtkNative.GtkWindowMove(
            _gtkWindow,
            _dragWindowOrigin.Value.X + (int)pointerDx,
            _dragWindowOrigin.Value.Y + (int)pointerDy);
    }

    public void EndDrag()
    {
        _waylandDrag = false;
        _waylandDragPending = false;
        _dragPointerStart = null;
    }

    /// <summary>
    /// 当前 GDK 后端是否 Wayland（判定结果缓存）。拖动形态只在这里分叉：X11 允许客户端自己摆窗口，
    /// Wayland 只能请合成器代劳。
    /// </summary>
    private bool IsWaylandBackend()
    {
        if (_isWayland is null)
        {
            nint display = GtkNative.GdkDisplayGetDefault();
            // gdk_display_get_name 返回 GDK 拥有的 const gchar*：复制成托管字符串，指针本身不动。
            // 必须走"裸指针 + 手工复制"，不要让它封送成 string 返回——原因见 GtkNative 里的注释。
            nint namePtr = display == 0 ? 0 : GtkNative.GdkDisplayGetName(display);
            string? name = namePtr == 0 ? null : Marshal.PtrToStringUTF8(namePtr);
            _isWayland = display != 0 && LinuxDragSupport.IsWaylandDisplay(name);
        }

        return _isWayland.Value;
    }

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

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
    {
        const int FileChooserActionOpen = 0;
        nint dialog = CreateChooser(options.Title ?? "打开", FileChooserActionOpen, "打开");
        try
        {
            ApplyFilters(dialog, options.Filters);
            SetInitialFolder(dialog, options.InitialDirectory);
            GtkNative.GtkFileChooserSetSelectMultiple(dialog, options.AllowMultiple);

            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSelectedPaths(dialog) : [];
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        const int FileChooserActionSave = 1;
        nint dialog = CreateChooser(options.Title ?? "保存", FileChooserActionSave, "保存");
        try
        {
            ApplyFilters(dialog, options.Filters);
            SetInitialFolder(dialog, options.InitialDirectory);
            GtkNative.GtkFileChooserSetDoOverwriteConfirmation(dialog, true);

            string suggested = options.DefaultFileName
                ?? (string.IsNullOrWhiteSpace(options.DefaultExtension) ? "" : "未命名." + options.DefaultExtension);
            if (suggested.Length > 0)
            {
                GtkNative.GtkFileChooserSetCurrentName(dialog, suggested);
            }

            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSinglePath(dialog, options.DefaultExtension) : null;
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        // GTK 的文件夹选择就是同一个 chooser 换个 action，不需要另一个对话框类型
        const int FileChooserActionSelectFolder = 2;
        nint dialog = CreateChooser(title ?? "选择文件夹", FileChooserActionSelectFolder, "选择");
        try
        {
            SetInitialFolder(dialog, initialDirectory);
            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSinglePath(dialog, extension: null) : null;
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    /// <summary>建一个只有「取消 / 确认」两个按钮的 chooser（不加内置按钮，文案由调用方给）。</summary>
    private nint CreateChooser(string title, int action, string acceptLabel)
    {
        nint dialog = GtkNative.GtkFileChooserDialogNew(title, _gtkWindow, action, 0); // varargs 终止
        GtkNative.GtkDialogAddButton(dialog, "取消", GtkResponseCancel);
        GtkNative.GtkDialogAddButton(dialog, acceptLabel, GtkResponseOk);
        return dialog;
    }

    private static void SetInitialFolder(nint dialog, string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            GtkNative.GtkFileChooserSetCurrentFolder(dialog, directory);
        }
    }

    private static void ApplyFilters(nint dialog, IReadOnlyList<OrielFileFilter>? filters)
    {
        if (filters is not { Count: > 0 })
        {
            return;
        }

        foreach (OrielFileFilter filter in filters)
        {
            if (filter.Patterns.Count == 0)
            {
                continue;
            }

            // 一个 OrielFileFilter 对应一个 GtkFileFilter，模式逐条加进去；
            // "*.*" 的归一化在 OrielFileFilter.GtkPatterns 里（fnmatch 语义差异，那里有单测）
            nint gtkFilter = GtkNative.GtkFileFilterNew();
            GtkNative.GtkFileFilterSetName(gtkFilter, filter.Name);
            foreach (string pattern in OrielFileFilter.GtkPatterns([filter]))
            {
                GtkNative.GtkFileFilterAddPattern(gtkFilter, pattern);
            }

            GtkNative.GtkFileChooserAddFilter(dialog, gtkFilter);
        }
    }

    private static string? ReadSinglePath(nint dialog, string? extension)
    {
        nint filename = GtkNative.GtkFileChooserGetFilename(dialog);
        if (filename == 0)
        {
            return null;
        }

        string? path = Marshal.PtrToStringUTF8(filename);
        GtkNative.GFree(filename);

        // GTK 不像 Windows 的对话框那样自动补扩展名（见 OrielFileDialogSupport 的说明）
        return string.IsNullOrEmpty(path) ? null : OrielFileDialogSupport.EnsureExtension(path, extension);
    }

    private static string[] ReadSelectedPaths(nint dialog)
    {
        nint list = GtkNative.GtkFileChooserGetFilenames(dialog);
        if (list == 0)
        {
            return [];
        }

        try
        {
            uint count = GtkNative.GSListLength(list);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                nint item = GtkNative.GSListNthData(list, i);
                if (item == 0)
                {
                    continue;
                }

                string? path = Marshal.PtrToStringUTF8(item);
                GtkNative.GFree(item);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            return [.. paths];
        }
        finally
        {
            // 只释放链表节点；里面的字符串已在上面逐个 g_free
            GtkNative.GSListFree(list);
        }
    }
}
