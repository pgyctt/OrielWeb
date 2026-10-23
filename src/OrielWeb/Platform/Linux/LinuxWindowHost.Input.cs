using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>输入面：光标、鼠标、拖放、右键菜单、拖动窗口。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
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

        // setCursorIcon / setCursorVisible 造的光标也归本对象所有
        if (_iconCursor != 0)
        {
            GtkNative.GObjectUnref(_iconCursor);
            _iconCursor = 0;
        }

        if (_blankCursor != 0)
        {
            GtkNative.GObjectUnref(_blankCursor);
            _blankCursor = 0;
        }

        _activeCursorEdge = -1;
        _pointerInResizeBorder = false;
    }

    /// <inheritdoc />
    public void SetCursorGrab(bool grab)
        => throw new NotSupportedException(
            "Linux 不支持 setCursorGrab：X11 要 XGrabPointer（grab 的生命周期与窗口销毁强耦合，"
            + "异常退出容易把整个桌面的指针卡住），Wayland 协议则根本没有这个能力。Tauri 在 Linux 上同样不支持。");

    /// <inheritdoc />
    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible)
        {
            return;
        }

        _cursorVisible = visible;
        ApplyCursor();
    }

    /// <inheritdoc />
    public void SetCursorIcon(OrielCursorIcon icon)
    {
        if (_iconCursor != 0)
        {
            GtkNative.GObjectUnref(_iconCursor);
            _iconCursor = 0;
        }

        _cursorIcon = icon;

        nint display = GtkNative.GdkDisplayGetDefault();
        if (display != 0)
        {
            _iconCursor = GtkNative.GdkCursorNewFromName(display, OrielCursorNames.ToGdkName(icon));
            if (_iconCursor == 0)
            {
                // 主题里没有这个名字（精简主题常见）。退到 default，并记一条诊断——
                // 这不是错误（用户换了主题），但"设了 copy 却看到箭头"必须能被解释
                _iconCursor = GtkNative.GdkCursorNewFromName(display, "default");
                OrielDiagnostics.Report(
                    $"[OrielWeb] setCursorIcon：当前光标主题没有「{OrielCursorNames.ToGdkName(icon)}」，已退回 default。");
            }
        }

        ApplyCursor();
    }

    /// <summary>按当前状态把光标应用到 webview（隐藏优先于自定义形状）。</summary>
    private void ApplyCursor()
    {
        nint cursor = 0;

        if (!_cursorVisible)
        {
            if (_blankCursor == 0)
            {
                nint display = GtkNative.GdkDisplayGetDefault();
                if (display != 0)
                {
                    // "none" 是 GTK 约定的空白光标名
                    _blankCursor = GtkNative.GdkCursorNewFromName(display, "none");
                }
            }

            cursor = _blankCursor;
            if (cursor == 0)
            {
                OrielDiagnostics.Report("[OrielWeb] setCursorVisible：拿不到空白光标，光标不会被隐藏。");
            }
        }
        else if (_iconCursor != 0)
        {
            cursor = _iconCursor;
        }

        SetWebviewCursor(cursor);
    }

    /// <inheritdoc />
    public void SetCursorPosition(double x, double y)
    {
        if (IsWaylandBackend())
        {
            throw new NotSupportedException(
                "Wayland 不允许客户端移动指针（协议没有这个请求），setCursorPosition 在 Wayland 上不可用。");
        }

        nint gdkWindow = GtkNative.GtkWidgetGetWindow(_webview);
        if (gdkWindow == 0 || GtkNative.GdkWindowGetOrigin(gdkWindow, out int originX, out int originY) == 0)
        {
            return;
        }

        if (!TryGetPointerDevice(out nint device, out nint screen))
        {
            return;
        }

        // GDK 的坐标是应用像素（逻辑），与入参同口径，不用乘缩放比
        GtkNative.GdkDeviceWarp(
            device,
            screen,
            originX + (int)Math.Round(x),
            originY + (int)Math.Round(y));
    }

    /// <inheritdoc />
    public void SetIgnoreCursorEvents(bool ignore)
    {
        if (IsWaylandBackend())
        {
            throw new NotSupportedException(
                "Wayland 不支持输入形状（gtk_widget_input_shape_combine_region 在 Wayland 下是 no-op），"
                + "setIgnoreCursorEvents 在 Wayland 上不可用。");
        }

        if (ignore == _ignoreCursorEvents)
        {
            return;
        }

        _ignoreCursorEvents = ignore;

        // 空区域 = 该部件不再接收任何输入。**顶层与 webview 都要设**：
        // X11 的输入形状按窗口生效，顶层设了空区域不会自动让子窗口一起穿透。
        nint region = ignore ? GtkNative.CairoRegionCreate() : 0;
        try
        {
            GtkNative.GtkWidgetInputShapeCombineRegion(_gtkWindow, region);
            if (_webview != 0)
            {
                GtkNative.GtkWidgetInputShapeCombineRegion(_webview, region);
            }
        }
        finally
        {
            if (region != 0)
            {
                GtkNative.CairoRegionDestroy(region);
            }
        }
    }

    /// <inheritdoc />
    public void StartResizeDragging(OrielResizeDirection direction)
    {
        if (IsWaylandBackend())
        {
            // Wayland 的 xdg_toplevel.resize 要一个来自"当前输入事件"的 serial。
            // 命令层收到这条命令时，那次按下早已处理完，serial 已经失效——
            // 调用会静默无效。与其假装成功，不如说清楚。
            throw new NotSupportedException(
                "Wayland 下 startResizeDragging 需要一个『当前输入事件』的 serial，"
                + "而命令是在按下事件处理完之后才到达的——协议层拿不到有效 serial。"
                + "Wayland 请给窗口画可拖动的边角（宿主自己的边缘 resize 就是这么做的）。");
        }

        if (_gtkWindow == 0 || !TryGetPointerDevice(out nint device, out _))
        {
            return;
        }

        // 这个函数的返回类型是 void（GTK 的约定）：拿不到时坐标给 -1
        GtkNative.GdkDeviceGetPosition(device, out _, out int rootX, out int rootY);
        if (rootX < 0 || rootY < 0)
        {
            return;
        }

        // button 固定左键、timestamp 用 GDK_CURRENT_TIME（0）：X11 下 GTK 走 _NET_WM_MOVERESIZE，
        // 时间戳只是提示，不影响结果
        GtkNative.GtkWindowBeginResizeDrag(
            _gtkWindow,
            (int)LinuxMappings.EdgeFor(direction),
            1,
            rootX,
            rootY,
            0);
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
}
