using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 窗口宿主：GTK3 窗口 + WebKitGTK WebView。
/// IPC 回执/ExecuteScript 走页面回环消息（与 macOS 同一模式）。
/// 注意：GDK 给的坐标**已经是应用像素**（与页面 CSS 像素同一套坐标系），不要自行乘缩放；
/// Wayland 下窗口移动必须交给合成器（见 API.md §2 的拖动与 HiDPI 说明）。
/// </summary>
internal sealed partial class LinuxWindowHost : IWindowBackend
{
    private const int GtkWinPosCenter = 1;
    private const int GtkResponseOk = -5;
    private const int GtkResponseCancel = -6;
    private const int GtkResponseYes = -8;
    private const int GtkResponseNo = -9;
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

    /// <summary>窗口销毁后句柄已清零；门面在 Closed 之后迟到的调用经各 IWindowBackend 入口的这个守卫直接放弃。</summary>
    private bool WindowAlive => _gtkWindow != 0;
    private nint _webview;

    /// <summary>运行期设过的窗口底色（<c>setBackgroundColor</c>）；<c>null</c> 表示没设过，用于回落创建期选项。</summary>
    private uint? _runtimeBackgroundArgb;
    private nint _userContentManager;
    private volatile bool _loadedRaised;

    private int _minWidth;
    private int _minHeight;

    /// <summary>最大尺寸（逻辑像素，0 = 不限制）。与 <c>_minWidth</c> / <c>_minHeight</c> 配对。</summary>
    private int _maxWidth;
    private int _maxHeight;

    private bool _isFullscreen;
    private bool _isOnTop;
    private string _title;

    /// <summary>
    /// 是否由 API（<see cref="SetTitle"/>）显式设过标题。
    /// </summary>
    /// <remarks>
    /// 置位后不再让页面 <c>&lt;title&gt;</c> 覆盖窗口标题。这不是洁癖：WebKitGTK 在窗口
    /// 加载完成后才发 <c>notify::title</c>，它可能**晚于** <c>window.setTitle</c>——
    /// 不挡住的话，刚设的标题会被页面标题覆盖回去（<c>--selftest window</c> 在 Linux 上
    /// 正是这样红的：设完读回还是原值）。
    /// </remarks>
    private bool _titleSetExplicitly;
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

    // 上一次已上报的客户区尺寸 / 窗口位置（逻辑像素）。configure-event 在拖动时每个像素来一次，
    // 不去重就是把每秒几百次原生回调变成同样数量的托管事件 + 页面 IPC。null 表示"还没报过"。
    private (double Width, double Height)? _lastReportedSize;
    private (double X, double Y)? _lastReportedPosition;
    // 上一次已上报的焦点状态；null 表示"还没报过"（首个 focus-in 必须报，哪怕值相同）
    private bool? _lastReportedFocus;

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
    private double? _lastReportedScaleFactor;
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
        CloseInterceptEnabled = options.InterceptClose;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        _messageHandler = new LinuxWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _gtkWindow;

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

    public bool IsMaximized => WindowAlive && GtkNative.GtkWindowIsMaximized(_gtkWindow);

    /// <summary>
    /// 窗口当前是否"显示中"（<see cref="Hide"/> 之后为 false；最小化仍算显示中）。
    /// </summary>
    /// <remarks>
    /// 问顶层部件自己的可见位：webview 是它的子部件，<c>gtk_widget_hide</c> 藏的是整个窗口，
    /// 不存在 Windows 组合宿主那种"渲染面是独立顶层窗口"的残余（见 <c>Win32WindowHost.Hide</c> 的不变量）。
    /// </remarks>
    public bool IsVisible => WindowAlive && GtkNative.GtkWidgetGetVisible(_gtkWindow);

    /// <summary>是否最小化（见 <see cref="IWindowBackend.IsMinimized"/>）。</summary>
    /// <remarks>
    /// 走 GDK 的状态位而不是 <c>gtk_window_is_active</c> 之类的间接判据：
    /// <c>GDK_WINDOW_STATE_ICONIFIED</c>（位 1）就是"最小化"这个事实本身。
    /// </remarks>
    public bool IsMinimized
    {
        get
        {
            const int GdkWindowStateIconified = 1 << 1;

            if (!WindowAlive)
            {
                return false;
            }

            nint gdkWindow = GtkNative.GtkWidgetGetWindow(_gtkWindow);
            return gdkWindow != 0 && (GtkNative.GdkWindowGetState(gdkWindow) & GdkWindowStateIconified) != 0;
        }
    }

    /// <summary>是否有键盘焦点（见 <see cref="IWindowBackend.IsFocused"/>）。</summary>
    public bool IsFocused => WindowAlive && GtkNative.GtkWindowIsActive(_gtkWindow);

    /// <summary>是否全屏（取库记住的状态，见 <see cref="IWindowBackend.IsFullscreen"/>）。</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>是否置顶（取库记住的状态，见 <see cref="IWindowBackend.IsAlwaysOnTop"/>）。</summary>
    public bool IsAlwaysOnTop => _isOnTop;

    /// <summary>是否有系统装饰（取创建时的选项，见 <see cref="IWindowBackend.IsDecorated"/>）。</summary>
    public bool IsDecorated => !_options.Frameless;

    /// <summary>是否可缩放（原生读，见 <see cref="IWindowBackend.IsResizable"/>）。</summary>
    public bool IsResizable => WindowAlive && GtkNative.GtkWindowGetResizable(_gtkWindow);

    /// <summary>
    /// 是否可最大化（见 <see cref="IWindowBackend.IsMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>回落到 <see cref="IsResizable"/></b>：GTK 没有"可最大化"这个独立概念——
    /// 最大化跟着 resizable 走。<c>gtk_window_set_resizable(false)</c> 之后窗口确实最大化不了。
    /// </remarks>
    public bool IsMaximizable => IsResizable;

    /// <summary>
    /// 是否可最小化（见 <see cref="IWindowBackend.IsMinimizable"/>）。
    /// </summary>
    /// <remarks>
    /// 同样回落到 <see cref="IsResizable"/>，理由同 <see cref="IsMaximizable"/>。
    /// </remarks>
    public bool IsMinimizable => IsResizable;

    /// <summary>
    /// 是否可关闭（见 <see cref="IWindowBackend.IsClosable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>恒为 true</b>：GTK 没有"可关闭"这个概念——窗口总能收到 delete-event，
    /// 而本库的关闭接管是在那之上做的**拦截**（页面可以不同意关）。
    /// 所以"能不能关"在这里不是一个可以被关掉的属性。给恒真比给一个猜测值诚实。
    /// </remarks>
    public bool IsClosable => true;

    /// <summary>是否接收输入（见 <see cref="IWindowBackend.IsEnabled"/>）。</summary>
    public bool IsEnabled => WindowAlive && GtkNative.GtkWidgetGetSensitive(_gtkWindow);

    /// <summary>启用/禁用输入（见 <see cref="IWindowBackend.SetEnabled"/>）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (WindowAlive)
        {
            GtkNative.GtkWidgetSetSensitive(_gtkWindow, enabled ? 1 : 0);
        }
    }

    /// <summary>设置最大尺寸（见 <see cref="IWindowBackend.SetMaxSize"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 走 <c>gtk_window_set_geometry_hints</c>，而且**只声明 <c>GDK_HINT_MAX_SIZE</c>**：
    /// 这样既加上限，又不动 <c>SetMinSize</c> 已有的 <c>set_size_request</c> 机制
    /// （两条路同时生效，各管一端，不会互相覆盖）。
    /// </para>
    /// <para>
    /// 传 0 表示取消上限——此时掩码里不带 <c>GDK_HINT_MAX_SIZE</c>，
    /// 而不是"声明一个 0 的上限"（后者会把窗口卡成 0 宽）。
    /// </para>
    /// </remarks>
    public void SetMaxSize(int width, int height)
    {
        _maxWidth = width;
        _maxHeight = height;

        if (!WindowAlive)
        {
            return;
        }

        const int GdkHintMaxSize = 1 << 2;

        var geometry = default(GtkNative.GdkGeometry);
        int hints = 0;

        if (width > 0 || height > 0)
        {
            geometry.max_width = Math.Max(width, 0);
            geometry.max_height = Math.Max(height, 0);
            hints = GdkHintMaxSize;
        }

        GtkNative.GtkWindowSetGeometryHints(_gtkWindow, 0, ref geometry, hints);
    }

    /// <summary>请求用户注意（见 <see cref="IWindowBackend.RequestUserAttention"/>）。</summary>
    /// <remarks>
    /// <b>两档在这里没有区别</b>：GTK 只有 <c>gtk_window_set_urgency_hint</c> 这个开/关，
    /// 没有紧急程度的分级。传 null 表示取消（置为 false）。
    /// </remarks>
    public void RequestUserAttention(OrielUserAttention? type)
    {
        if (WindowAlive)
        {
            GtkNative.GtkWindowSetUrgencyHint(_gtkWindow, type is not null);
        }
    }

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

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

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
    /// 不能像 Windows 那样"弹完即抛"。窗口销毁时由 <see cref="OnWindowDestroyed"/> 释放：
    /// GtkMenu 的 GCHandle 根住的是捕获了本宿主的闭包，漏掉它整棵宿主对象树都回收不了
    /// （评审 P2-8 修的就是这条链）。
    /// </remarks>
    public void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
    {
        _contextMenu?.Dispose();
        _contextMenu = GtkMenu.Build(items, item => ActivateMenuItem(item));

        // 指定位置时要给 GDK 一个定位基准的 GdkWindow。取 webview 的而不是顶层窗口的：
        // 页面给的坐标是**客户区**坐标，webview 的 GdkWindow 原点正是客户区左上角。
        // 拿不到（未 realize）时传 0，GtkMenu.Popup 会退回指针定位。
        nint relativeTo = position is null || _webview == 0
            ? 0
            : GtkNative.GtkWidgetGetWindow(_webview);

        _contextMenu?.Popup(0, position, relativeTo);

        // GTK 的弹出也不阻塞；自动关闭要回到 GTK 主循环上做（gtk_menu_popdown 不是线程安全的）。
        if (autoDismissAfter is { } delay && _contextMenu is { } menu)
        {
            _ = Task.Delay(delay).ContinueWith(
                _ => PostToUiThread(menu.Popdown), TaskScheduler.Default);
        }
    }

    /// <summary>收起正在显示的上下文菜单（<c>gtk_menu_popdown</c>）；没有在显示时是空操作。</summary>
    public void CloseContextMenu() => _contextMenu?.Popdown();

    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window, TryExecuteNativeEditing))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 Linux 上未被处理。");
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

        ApplyBackgroundColor();

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
                _options.Zoom,
                App.Guard.Token,
                App.Guard.TrustedPrefixes,
                ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label),
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

        // 底色（WithBackgroundColor）在创建期就应用过了：见上面那处 ApplyBackgroundColor()。

        if (_options.X is not null || _options.Y is not null)
        {
            // At(x,y)（与 Windows/macOS 对齐）：map 之前发出的 move 请求会被窗口管理器
            // 当作初始位置。只给一维时另一维传 -1（GTK 的"保持现状"值）。
            GtkNative.GtkWindowMove(_gtkWindow, _options.X ?? -1, _options.Y ?? -1);
        }
        else if (_options.Center)
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
    /// 应用"内核就绪前的底色"（见 <see cref="OrielWindowOptions.BackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// WebKitGTK 的 <c>webkit_web_view_set_background_color</c>：页面 CSS 生效之前那一帧只有它能管。
    /// 该 API 在 2.40 起被标记 deprecated（推荐 CSS），但没有移除——已在 WSL 上确认符号存在，
    /// 行为待真机确认（见 ROADMAP 的真机清单）。
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        // 运行期设过就以它为准（后设的赢）；没设过才回落到创建期选项。
        uint? effective = _runtimeBackgroundArgb;
        if (effective is null && _options.TryGetBackgroundArgb(out byte ca, out byte cr, out byte cg, out byte cb))
        {
            effective = ((uint)ca << 24) | ((uint)cr << 16) | ((uint)cg << 8) | cb;
        }

        if (effective is not { } argb || _webview == 0)
        {
            return;
        }

        OrielWindowOptions.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);

        var rgba = new GdkRGBA
        {
            Red = r / 255.0,
            Green = g / 255.0,
            Blue = b / 255.0,
            Alpha = a / 255.0,
        };

        GtkNative.WebkitWebViewSetBackgroundColor(_webview, in rgba);
    }

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 取的是 <c>gtk-double-click-time</c>；取不到时返回 0，由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底。快照里**没有**缩放——GDK 的坐标已经是
    /// 应用像素（见 API.md §2 的 HiDPI 说明），拖动不需要再折一次。
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

    /// <summary>
    /// GTK <c>delete-event</c>：返回 true 表示**吞掉**该事件（窗口不销毁）。
    /// </summary>
    /// <remarks>
    /// GTK 的返回值语义与另外两个平台相反，这里是唯一的翻译点：
    /// <c>true</c> = 阻止关闭，<c>false</c> = 放行。
    /// </remarks>
    internal bool OnWindowShouldClose()
    {
        OrielCloseDecision decision = DecideClose();

        // Proceed → false（放行，GTK 继续销毁窗口）
        // Cancel / Suspend → true（吞掉事件，窗口留在打开状态）
        return decision != OrielCloseDecision.Proceed;
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

        // GTK 对象多半已随 destroy 链 finalize：句柄清零后，门面迟到的调用走
        // IWindowBackend 各入口的 WindowAlive 守卫（no-op），而不是对悬空指针发消息。
        _gtkWindow = 0;

        // 保留的上下文菜单（编辑菜单）一并释放：GtkMenu._bindings 的 GCHandle 根住捕获
        // host 的闭包，不释放的话上面的注册表清理白做——整棵 host 对象树跟着菜单漏掉。
        _contextMenu?.Dispose();
        _contextMenu = null;

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
        // 显式设过标题就不再跟随页面标题（理由见 _titleSetExplicitly 的说明）
        if (_titleSetExplicitly)
        {
            return;
        }

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
    public void Show()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWidgetShowAll(_gtkWindow);
    }

    public void Hide()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWidgetHide(_gtkWindow);
    }

    public void Close()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowClose(_gtkWindow);
    }

    /// <summary>强制销毁窗口：绕过 <c>Closing</c> 与页面接管（对标 Tauri 2 的 <c>destroy</c>）。</summary>
    /// <remarks>
    /// 用 <c>gtk_widget_destroy</c> 而不是 <c>gtk_window_close</c>：后者会先发 <c>delete-event</c>
    /// （正是可被拦下的那条路），前者直接销毁。这正是 Destroy 要的"不受拦"语义。
    /// </remarks>
    public void Destroy()
    {
        if (!WindowAlive) { return; }
        _forceDestroy = true;
        GtkNative.GtkWidgetDestroy(_gtkWindow);
    }

    /// <summary>是否让页面接管关闭；初值取自 <see cref="OrielWindowOptions.InterceptClose"/>。</summary>
    public bool CloseInterceptEnabled { get; set; }

    /// <summary>哪几类宿主事件转发给页面；默认全关（见 <see cref="OrielWindowEventKind"/>）。</summary>
    public OrielWindowEventKind EventForwarding { get; set; }

    /// <summary>窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。</summary>
    public double ScaleFactor => CurrentScaleFactor();

    /// <summary>客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。</summary>
    /// <remarks>
    /// <c>gtk_window_get_size</c> 给的是**客户区**（不含 WM 装饰）——GTK 拿不到装饰尺寸，
    /// 这也是 <see cref="WebviewWindow.Resize"/> 在 Linux 上本就是客户区语义的原因。
    /// </remarks>
    public OrielSize InnerSize
    {
        get
        {
            if (!WindowAlive)
            {
                return default;
            }

            GtkNative.GtkWindowGetSize(_gtkWindow, out int width, out int height);
            double scale = CurrentScaleFactor();
            return new OrielSize(width / scale, height / scale);
        }
    }

    /// <summary>
    /// 整窗尺寸（逻辑像素）；见 <see cref="IWindowBackend.OuterSize"/>。
    /// </summary>
    /// <remarks>
    /// 走 <c>gdk_window_get_frame_extents</c>。**Wayland 下它可能只返回客户区**
    /// （合成器不给装饰尺寸），这时外框与客户区相等——调用方据此判断该平台能不能区分内外。
    /// </remarks>
    public OrielSize OuterSize
    {
        get
        {
            if (!WindowAlive)
            {
                return default;
            }

            nint gdkWindow = GtkNative.GtkWidgetGetWindow(_gtkWindow);
            if (gdkWindow == 0)
            {
                return InnerSize;
            }

            GtkNative.GdkWindowGetFrameExtents(gdkWindow, out GdkRectangle rect);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                // 取不到装饰矩形时退回客户区，而不是报 0x0：0x0 会让调用方以为窗口没了
                return InnerSize;
            }

            double scale = CurrentScaleFactor();
            return new OrielSize(rect.Width / scale, rect.Height / scale);
        }
    }

    /// <summary>窗口左上角（逻辑像素）；见 <see cref="IWindowBackend.OuterPosition"/>。</summary>
    /// <remarks>
    /// <c>gtk_window_get_position</c> 在 **Wayland 下恒返回 (0,0)**——合成器不告诉客户端
    /// 窗口在哪（这是 Wayland 的隐私设计，不是缺陷）。X11 下可用。
    /// </remarks>
    public OrielPosition OuterPosition
    {
        get
        {
            if (!WindowAlive)
            {
                return default;
            }

            GtkNative.GtkWindowGetPosition(_gtkWindow, out int x, out int y);
            double scale = CurrentScaleFactor();
            return new OrielPosition(x / scale, y / scale);
        }
    }

    /// <summary>
    /// 客户区左上角（逻辑像素）；见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </summary>
    /// <remarks>
    /// <b>目前恒等于 <see cref="OuterPosition"/></b>：GTK 不暴露"客户区相对窗口原点的偏移"
    /// （装饰由 WM 画，客户端看不到尺寸）。与其编一个偏移值，不如如实相等。
    /// </remarks>
    public OrielPosition InnerPosition => OuterPosition;

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

    public void Focus()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowPresent(_gtkWindow);
    }

    public void Maximize()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowMaximize(_gtkWindow);
    }

    public void Minimize()
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowIconify(_gtkWindow);
    }

    public void Restore()
    {
        if (!WindowAlive) { return; }
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
        if (!WindowAlive) { return false; }
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
    /// 全屏状态回灌：WM 侧的原生全屏路径（快捷键/系统菜单）不经过 <see cref="SetFullscreen"/>，
    /// 不回灌的话 <c>_isFullscreen</c> 会与真实状态相反，下一次 Toggle/SetFullscreen 行为颠倒。
    /// 与 maximize 共用 window-state-event 这个时机（macOS 由窗口委托的 didEnter/ExitFullScreen
    /// 回灌；Windows 全屏只经 API 发起，无此问题）。
    /// </summary>
    internal void SyncFullscreenState()
    {
        if (WindowAlive)
        {
            _isFullscreen = GtkNative.GtkWindowIsFullscreen(_gtkWindow);
        }
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

    // ------------------------------------------------------------------
    // 尺寸 / 位置 / 焦点上报（对标 Tauri 2 的 onResized / onMoved / onFocusChanged）
    // ------------------------------------------------------------------

    /// <summary>
    /// GTK 的 <c>configure-event</c>：尺寸或位置变了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一个信号覆盖两件事</b>：GTK3 的 <c>configure-event</c> 在窗口被移动**或**调整大小时都会发，
    /// 所以尺寸与位置都在这里上报。这也是 GTK 上唯一可靠的双向来源——
    /// <c>size-allocate</c> 只管尺寸（且合成器发起的移动不触发），
    /// 而 <c>gtk_window_get_position</c> 在 Wayland 下拿不到真实位置。
    /// </para>
    /// <para>
    /// 从 <c>GdkEventConfigure</c> 的字段直接读，而不是回头调 <c>gtk_window_get_size</c>：
    /// 后者在 Wayland 下可能给不出合成器侧的真实尺寸（窗口被平铺、被缩放时）。
    /// GdkEventConfigure 的结构是 { GdkEventType type; GdkWindow* window; gint8 send_event;
    /// guint32 x, y, width, height; } —— 前 4 个字段固定，之后是三个 32 位整数。
    /// </para>
    /// <para>
    /// 坐标单位是**设备像素**（GDK 在 HiDPI 下不折算），所以除以缩放比换成逻辑像素，
    /// 与 <see cref="MoveTo"/> / <see cref="Resize"/> 的入参语义对齐。
    /// </para>
    /// </remarks>
    internal void OnConfigureEvent(nint gdkEvent)
    {
        if (gdkEvent == 0)
        {
            return;
        }

        // 用结构体映射而不是手算偏移：GdkEventConfigure 的字段对齐很微妙
        // （gint8 send_event 后有 3 字节 padding、GdkWindow* 前有 4 字节 padding），
        // 手算写错 4 字节会让 x 读到 y、width 读到 height 之后的数据——症状是
        // "上报的尺寸和位置全都不对"，而那种错误看起来像是逻辑问题，极难定位到偏移。
        // 结构定义与踩坑说明见 GtkNative.GdkEventConfigure。
        GdkEventConfigure configure = Marshal.PtrToStructure<GdkEventConfigure>(gdkEvent);

        // 尺寸为 0 是 GTK 在 realize/unrealize 之间的中间态，不是真实尺寸
        if (configure.Width <= 0 || configure.Height <= 0)
        {
            return;
        }

        double scale = CurrentScaleFactor();
        ReportSize(configure.Width / scale, configure.Height / scale);
        ReportPosition(configure.X / scale, configure.Y / scale);
    }

    /// <summary>GTK 的 <c>focus-in-event</c> / <c>focus-out-event</c>。</summary>
    internal void OnFocusChanged(bool focused) => ReportFocus(focused);

    /// <summary>
    /// 当前窗口所在显示器的缩放比（1.0 = 100%）。
    /// </summary>
    /// <remarks>
    /// <b>每个窗口各取各的</b>：GTK 的 <c>gdk_monitor_get_scale_factor</c> 是**显示器级**的，
    /// 同一进程的两个窗口可能在不同缩放的显示器上。用 <c>gtk_widget_get_scale_factor</c>
    /// 拿到的是控件所在显示器（未 realize 时回落到默认显示器）。
    /// </remarks>
    private double CurrentScaleFactor()
    {
        if (!WindowAlive)
        {
            return 1.0;
        }

        int factor = GtkNative.GtkWidgetGetScaleFactor(_gtkWindow);
        return factor > 0 ? factor : 1.0;
    }

    /// <summary>上报客户区尺寸（逻辑像素）；与上次相同则跳过。</summary>
    /// <remarks>
    /// 去重是必须的：拖动边框时 <c>configure-event</c> 每个像素来一次，
    /// 每次都触发托管事件 + 页面 IPC 会把一次拖动变成流量风暴，而其中 99% 是重复值。
    /// </remarks>
    private void ReportSize(double width, double height)
    {
        if (_lastReportedSize is { } previous
            && Math.Abs(previous.Width - width) < 0.5
            && Math.Abs(previous.Height - height) < 0.5)
        {
            return;
        }

        _lastReportedSize = (width, height);
        Resized?.Invoke(new OrielSizeChangedEventArgs(width, height));

        // 页面侧转发是按需的（默认关）：拖动时这条路径每个像素走一次，
        // 而这里的推送是一次 JS 求值。见 OrielWindowEventKind。
        if ((EventForwarding & OrielWindowEventKind.Resize) != 0)
        {
            PushEventOnUi("resize", OrielEventPayload.Size(width, height));
        }
    }

    /// <summary>上报窗口左上角位置（逻辑像素）；与上次相同则跳过。</summary>
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

        if ((EventForwarding & OrielWindowEventKind.Move) != 0)
        {
            PushEventOnUi("move", OrielEventPayload.Position(x, y));
        }
    }

    /// <summary>
    /// 缩放比变了就上报（由 GDK 的 <c>notify::scale-factor</c> 调用；见 <see cref="IWindowBackend.ScaleFactorChanged"/>）。
    /// </summary>
    /// <remarks>
    /// payload 里的 <c>size</c> 是**物理像素**客户区（与 Tauri 的 <c>PhysicalSize</c> 对齐）。
    /// </remarks>
    internal void ReportScaleFactorIfChanged()
    {
        double scale = CurrentScaleFactor();
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
        if (!WindowAlive || enabled == _isFullscreen)
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
        if (!WindowAlive) { return; }
        _isOnTop = enabled;
        GtkNative.GtkWindowSetKeepAbove(_gtkWindow, enabled);
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
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowSetTitle(_gtkWindow, title);
    }

    public void SetResizable(bool enabled)
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowSetResizable(_gtkWindow, enabled);
    }

    /// <summary>
    /// 设置是否可最大化——<b>空操作</b>（见 <see cref="IWindowBackend.SetMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// GTK3 没有控制最大化按钮的 API。窗口管理器允许哪些动作是由它自己依据
    /// <c>_NET_WM_ALLOWED_ACTIONS</c> 算出来的，客户端改不了（Tauri/tao 在 Linux 上
    /// 同样是空操作）。这里**刻意不记状态**：一旦记了，<see cref="IsMaximizable"/>
    /// 就会给出一个"用户看到的按钮其实还在"的假值。要真的禁止最大化，
    /// 现有手段是 <see cref="SetResizable"/>(false)——GTK 上两者本来就是同一件事。
    /// </remarks>
    public void SetMaximizable(bool enabled)
    {
        _ = enabled;
    }

    /// <summary>
    /// 设置是否可最小化——<b>空操作</b>（见 <see cref="IWindowBackend.SetMinimizable"/>）。
    /// </summary>
    /// <remarks>理由同 <see cref="SetMaximizable"/>：GTK 没有对应入口，且不记状态以免读取撒谎。</remarks>
    public void SetMinimizable(bool enabled)
    {
        _ = enabled;
    }

    /// <summary>
    /// 设置窗口图标（见 <see cref="IWindowBackend.SetIcon"/>）。
    /// </summary>
    /// <remarks>
    /// 复用创建期那条路（<c>gtk_window_set_icon_from_file</c>）——它在运行期同样有效，
    /// GTK 会重写 <c>_NET_WM_ICON</c>。
    /// <para>
    /// <b>Wayland 下通常无效</b>：那边窗口图标由合成器决定（多数合成器直接用 .desktop
    /// 文件的图标），应用改不了。这与创建期图标是同一个限制，不是这里新引入的。
    /// </para>
    /// </remarks>
    public void SetIcon(string path)
    {
        if (!WindowAlive || string.IsNullOrEmpty(path))
        {
            return;
        }

        _ = GtkNative.GtkWindowSetIconFromFile(_gtkWindow, path, 0);
    }

    /// <summary>从内存里的图像字节设置窗口图标（见 <see cref="IWindowBackend.SetIconFromBytes"/>）。</summary>
    /// <remarks>
    /// 与托盘图标共用 <see cref="GtkPixbufFactory"/>（RGBA → GdkPixbuf）。
    /// 注意 <c>gtk_window_set_icon</c> 会 ref 一次，所以这里仍要释放**自己**那份 pixbuf。
    /// </remarks>
    public void SetIconFromBytes(byte[] bytes)
    {
        if (!WindowAlive || bytes is null || bytes.Length == 0)
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

        nint pixbuf = GtkPixbufFactory.CreatePixbuf(image);
        if (pixbuf == 0)
        {
            return;
        }

        try
        {
            GtkNative.GtkWindowSetIcon(_gtkWindow, pixbuf);
        }
        finally
        {
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>从裸 RGBA 设置窗口图标（见 <see cref="IWindowBackend.SetIconFromRgba"/>）。</summary>
    /// <remarks>与 <see cref="SetIconFromBytes"/> 共用后半段，只是跳过解码。</remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
    {
        if (!WindowAlive || rgba is null || rgba.Length == 0)
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

        nint pixbuf = GtkPixbufFactory.CreatePixbuf(new OrielImage(rgba, width, height));
        if (pixbuf == 0)
        {
            return;
        }

        try
        {
            GtkNative.GtkWindowSetIcon(_gtkWindow, pixbuf);
        }
        finally
        {
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>
    /// 设置窗口底色（见 <see cref="IWindowBackend.SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 只记下运行期的值再走一遍 <see cref="ApplyBackgroundColor"/>——创建期与运行期
    /// 在 WebKitGTK 上是同一件事，没有理由写两份。
    /// </remarks>
    public void SetBackgroundColor(uint argb)
    {
        _runtimeBackgroundArgb = argb;
        ApplyBackgroundColor();
    }

    /// <summary>
    /// 设置是否可关闭——<b>空操作</b>（见 <see cref="IWindowBackend.SetClosable"/>）。
    /// </summary>
    /// <remarks>
    /// GTK 没有"可关闭"这个属性：窗口总能收到 delete-event，本库的关闭接管
    /// （页面可以不同意关）是在那之上做的**拦截**，而"拦截"和"禁掉关闭按钮"不是一回事
    /// （Tauri/tao 在 Linux 上同样没实现这一属性）。
    /// <see cref="IsClosable"/> 在这里恒为 <c>true</c>，不会因为调过本方法而变。
    /// <para>
    /// 真要"关不掉"，现有手段是拦 <c>close-requested</c> 事件——那是应用层策略，
    /// 而且能做到"按条件拒绝"，比一个静态属性更有用。
    /// </para>
    /// </remarks>
    public void SetClosable(bool enabled)
    {
        _ = enabled;
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        if (!WindowAlive) { return; }
        GtkNative.GtkWidgetSetSizeRequest(_gtkWindow, Math.Max(width, 0), Math.Max(height, 0));
    }

    public void MoveTo(int x, int y)
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowMove(_gtkWindow, x, y);
    }

    public void Resize(int width, int height)
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowResize(_gtkWindow, width, height);
    }

    public void Center()
    {
        if (!WindowAlive) { return; }
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
            // 投递与执行之间窗口可能已被销毁：带着 0 句柄去调 GTK 只会得到 critical 警告
            // （GTK 的函数都要求非空对象）。与 PushEventOnUi / PostWebMessageOnUi 同款判空——
            // 挂起的 eval 已由销毁/导航路径清掉，调用方的 Task 不会因此永久挂起。
            if (_webview == 0)
            {
                return;
            }

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
            // 把回执**原样**求值进页面：`_onResult` 现在收整个回执对象。
            //
            // 早先是宿主侧把回执拆成 `_onResult(id, ok, payload)`——成功取值、失败取文案。
            // 改成整对象传是为了让 code / data 自然流过：拆的话三平台各要抽一遍、
            // 各可能漏一个字段，而漏掉的表现是"某个平台拿不到错误码"。
            //
            // 直接把 JSON 嵌进 JS 表达式是安全的：JSON 字面量本身就是合法的 JS 表达式，
            // 而这份 JSON 由宿主自己的 Utf8JsonWriter 产出（不是拼接字符串）。
            var js = $"window.oriel._onResult({json})";
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

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// GTK 这边**支持自定义按钮文案**（<c>gtk_dialog_add_button</c> 直接收文本），
    /// 所以 Windows 的限制在 Linux 上不存在。
    /// </para>
    /// <para>
    /// 按钮顺序按**添加顺序**排（GTK 的惯例）：肯定性的在前、取消性的在后，
    /// 所以 <c>YesNoCancel</c> 的添加顺序是 是 → 否 → 取消。响应 ID 用自定义值
    /// （<c>GtkResponseOk</c>/<c>Cancel</c>/<c>Yes</c>/<c>No</c>）以便回读时区分——
    /// 全用 <c>GTK_RESPONSE_OK</c> 的话三个按钮就分不出来了。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        int type = kind switch
        {
            "warning" => 1,  // GTK_MESSAGE_WARNING
            "error" => 3,    // GTK_MESSAGE_ERROR
            "question" => 2, // GTK_MESSAGE_QUESTION
            _ => 0,          // GTK_MESSAGE_INFO
        };

        nint dialog = GtkNative.GtkMessageDialogNew(_gtkWindow, 0, type, 0 /*GTK_BUTTONS_NONE*/, "%s", message);
        if (dialog == 0)
        {
            return null;
        }

        try
        {
            if (!string.IsNullOrEmpty(title))
            {
                GtkNative.GtkWindowSetTitle(dialog, title);
            }

            switch (buttons)
            {
                case MessageButtons.OkCancel:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseOk);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseCancel);
                    break;

                case MessageButtons.YesNo:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseYes);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseNo);
                    break;

                case MessageButtons.YesNoCancel:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseYes);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseNo);
                    // 第三个按钮没有独立文案参数（Tauri 的 yes/no/cancel 也没有）——
                    // 用系统惯例的"取消"。
                    GtkNative.GtkDialogAddButton(dialog, "取消", GtkResponseCancel);
                    break;

                default:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseOk);
                    break;
            }

            int response = GtkNative.GtkDialogRun(dialog);

            return response switch
            {
                GtkResponseOk => MessageResult.Ok,
                GtkResponseCancel => MessageResult.Cancel,
                GtkResponseYes => MessageResult.Yes,
                GtkResponseNo => MessageResult.No,
                _ => null,
            };
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
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
