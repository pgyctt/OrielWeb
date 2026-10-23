using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>尺寸、位置、DPI 换算与变化上报。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    public bool IsMaximized => WindowAlive && GtkNative.GtkWindowIsMaximized(_gtkWindow);

    /// <summary>设置缩放倍率（见 <see cref="IWindowBackend.SetZoom"/>）。</summary>
    /// <remarks>
    /// 落点是 <c>webkit_web_view_set_zoom_level</c>（1.0 = 100%），与 WebView2 的
    /// ZoomFactor、WKWebView 的 pageZoom 语义一致，无平台差异要登记。
    /// 必须在 GTK 主线程调——命令层本来就在主线程上执行。
    /// </remarks>
    public void SetZoom(double scale)
    {
        if (!WindowAlive || _webview == 0)
        {
            return;
        }

        GtkNative.WebkitWebViewSetZoomLevel(_webview, scale);
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

    /// <summary>菜单栏高度（布局盒里那一条的高度）；没有菜单栏时是 0。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Resize"/> 与 <see cref="ApplyMinSizeRequest"/> 要用它把"页面要的客户区"
    /// 补回"窗口要的总高"——不补的话调一次 <c>Resize(800, 600)</c>，webview 只拿到
    /// 600 - 菜单栏高。
    /// </para>
    /// <para>
    /// <b>读期望高度而不是分配高度</b>：分配要等一次布局走完，而我们是"刚塞进盒就要换算"
    /// （<c>SetWindowMenuBar</c> 里装完立刻重算最小尺寸），那时分配还没发生、读出来恒为 0。
    /// 菜单栏是 <c>expand=false</c> 装进去的，实际高度就等于自然高度，所以期望高度是准的。
    /// </para>
    /// </remarks>
    private int MenuBarHeight
    {
        get
        {
            if (_menuBar is null || _menuBar.Handle == 0)
            {
                return 0;
            }

            GtkNative.GtkWidgetGetPreferredHeight(_menuBar.Handle, out _, out int natural);
            return natural > 0 ? natural : 0;
        }
    }

    /// <summary>窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。</summary>
    public double ScaleFactor => CurrentScaleFactor();

    /// <summary>客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。</summary>
    /// <remarks>
    /// <para>
    /// 读的是**webview 的分配尺寸**，不是 <c>gtk_window_get_size</c>。两者在没有菜单栏时相等，
    /// 但挂上菜单栏之后窗口的客户区包含菜单栏那一条，而页面的视口只在 webview 里——
    /// 读窗口尺寸会把菜单栏算进 <c>innerSize</c>，与"innerSize 是页面看到的区域"这条契约冲突。
    /// </para>
    /// <para>
    /// 选 webview 而不是窗口还有一个理由：<b>与 Windows 对齐</b>。Windows 上
    /// <c>GetClientRect</c> 在挂菜单栏后自动排除菜单栏，读到的正是 webview 那块。
    /// 两个平台用同一个口径，迁移代码不用按平台分支。
    /// </para>
    /// </remarks>
    public OrielSize InnerSize
    {
        get
        {
            if (!WindowAlive)
            {
                return default;
            }

            int width = GtkNative.GtkWidgetGetAllocatedWidth(_webview);
            int height = GtkNative.GtkWidgetGetAllocatedHeight(_webview);
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

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        ApplyMinSizeRequest();
    }

    /// <summary>
    /// 把 <see cref="_minWidth"/> / <see cref="_minHeight"/>（**页面**要的下限）写成窗口的 size request。
    /// </summary>
    /// <remarks>
    /// 要补回菜单栏高度：这两个字段的语义是"webview 最小能缩到多少"，而
    /// <c>gtk_widget_set_size_request</c> 约束的是**窗口内容区**（含菜单栏）。
    /// 不补的话挂了菜单栏的窗口最小尺寸会退化成"页面最少能缩到 min - 菜单栏"，
    /// 与 Windows 的 <c>WM_GETMINMAXINFO</c> 换算不一致。
    /// <para>
    /// 0 表示"这一维不设下限"（GTK 的 <c>set_size_request</c> 也是这个约定），
    /// 所以这里用 <c>Math.Max(..., 0)</c> 而不是 <c>..., 1</c>——后者会把
    /// "没设最小宽度"变成"最小宽度 1"。
    /// </para>
    /// </remarks>
    private void ApplyMinSizeRequest()
    {
        if (!WindowAlive)
        {
            return;
        }

        GtkNative.GtkWidgetSetSizeRequest(
            _gtkWindow,
            Math.Max(_minWidth, 0),
            Math.Max(_minHeight, 0) + MenuBarHeight);
    }

    public void MoveTo(int x, int y)
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowMove(_gtkWindow, x, y);
    }

    public void Resize(int width, int height)
    {
        if (!WindowAlive) { return; }

        // 页面要的是 **webview** 的尺寸，而 gtk_window_resize 吃的是**整个窗口内容区**
        // （含菜单栏）。不补回菜单栏高度的话，Resize(800, 600) 会让 webview 只拿到
        // 600 - 菜单栏高，页面少一行——而这正是"客户区尺寸契约"要防的那类偏差。
        GtkNative.GtkWindowResize(_gtkWindow, width, height + MenuBarHeight);
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
}
