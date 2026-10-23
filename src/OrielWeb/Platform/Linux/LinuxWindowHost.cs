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

    /// <summary>
    /// 窗口的根布局：<c>GtkBox{menubar?, webview}</c>。
    /// </summary>
    /// <remarks>
    /// 即使没有菜单栏也走这个盒（不直接 <c>gtk_container_add(window, webview)</c>）：
    /// 结构固定成一种，"加/去菜单栏"就只是往盒里插/摘一个部件，不必在两条布局路径之间
    /// 来回搬家。搬家要重挂 webview，那会把页面状态一起弄丢。
    /// </remarks>
    private nint _rootBox;

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

    /// <summary>
    /// 接管式菜单的内容：只含剪切 / 复制 / 粘贴（<see cref="OrielContextMenuPolicy.Editing"/> 的语义）。
    /// </summary>
    private void ShowEditingMenu(nint gdkEvent)
    {
        _contextMenu?.Dispose();
        _contextMenu = GtkMenu.Build(OrielMenuRoles.EditingMenuItems(), ActivateMenuItem);
        _contextMenu?.Popup(gdkEvent);
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
        // 运行期可改（SetDecorations），初值来自创建选项
        _decorations = !options.Frameless;
        _messageHandler = new LinuxWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _gtkWindow;

    public OrielApp App => _app;

    /// <summary>本窗口的 label（见 <see cref="OrielWindowOptions.Label"/>）。</summary>
    public string Label => _options.Label;
    /// <remarks>本类为 internal，public 成员不增加对外 API 面；它实现 <see cref="IWindowBackend.IsOnUiThread"/>。</remarks>
    public bool IsOnUiThread() => _backend.IsOnUiThread();

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

    /// <summary>是否有键盘焦点（见 <see cref="IWindowBackend.IsFocused"/>）。</summary>
    public bool IsFocused => WindowAlive && GtkNative.GtkWindowIsActive(_gtkWindow);

    /// <summary>
    /// <c>WebKitWebsiteDataTypes</c> 的全部 12 类（MEMORY_CACHE 到 PLUGIN_DATA，2.16 定义全集）按位或。
    /// </summary>
    private const uint WebkitWebsiteDataAll = 0xFFF;

    // ------------------------------------------------------------------
    // 上下文菜单
    // ------------------------------------------------------------------

    private GtkMenu? _contextMenu;

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

    // ---- 光标（B21）----

    /// <summary>应用显式设置的光标形状（<c>null</c> = 没设过）。</summary>
    private OrielCursorIcon? _cursorIcon;

    /// <summary>上面那个形状对应的 GdkCursor（本对象持有引用）；<c>0</c> = 没设或建不出来。</summary>
    private nint _iconCursor;

    /// <summary>隐藏光标用的空白光标（懒建、缓存）。</summary>
    private nint _blankCursor;

    /// <summary>光标可见性意图。</summary>
    private bool _cursorVisible = true;

    /// <summary>是否设置了忽略鼠标事件。</summary>
    private bool _ignoreCursorEvents;

    // ------------------------------------------------------------------
    // 平台开关 / 特效 / 任务栏（B22）
    // ------------------------------------------------------------------
    //
    // Linux 是这一批里"能做的最多、能验证的最少"的那一端：GTK 把大部分平台开关都
    // 表达成**窗口管理器提示**（skip-taskbar / stick / keep-below / accept-focus），
    // X11 下主流 WM 都尊重，Wayland 下由合成器决定（文档写明）。
    // 特效与任务栏徽标则整组不存在（GTK3 没有对应 API，libunity 本库不链接），
    // 一律抛 NotSupportedException，不做静默 no-op。

    /// <summary>当前是否显示装饰（<see cref="SetDecorations"/> 会改它）。</summary>
    private bool _decorations;

    /// <summary>取默认 display 上的指针设备与屏幕（拿不到时返回 false）。</summary>
    private static bool TryGetPointerDevice(out nint device, out nint screen)
    {
        device = 0;
        screen = 0;

        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0)
        {
            return false;
        }

        nint seat = GtkNative.GdkDisplayGetDefaultSeat(display);
        if (seat != 0)
        {
            device = GtkNative.GdkSeatGetPointer(seat);
        }

        screen = GtkNative.GdkDisplayGetDefaultScreen(display);
        return device != 0 && screen != 0;
    }

    /// <summary>公共的 resize 方向 → GTK 的窗口边缘。</summary>
    private static GdkWindowEdge EdgeFor(OrielResizeDirection direction) => direction switch
    {
        OrielResizeDirection.East => GdkWindowEdge.East,
        OrielResizeDirection.North => GdkWindowEdge.North,
        OrielResizeDirection.NorthEast => GdkWindowEdge.NorthEast,
        OrielResizeDirection.NorthWest => GdkWindowEdge.NorthWest,
        OrielResizeDirection.South => GdkWindowEdge.South,
        OrielResizeDirection.SouthEast => GdkWindowEdge.SouthEast,
        OrielResizeDirection.SouthWest => GdkWindowEdge.SouthWest,
        _ => GdkWindowEdge.West,
    };

    private string? _currentUrl;

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
}
