using System.Runtime.InteropServices;

namespace OrielWeb.Platform.Linux.Interop;

// ============================================================================
// GTK3 + WebKitGTK 4.1 互操作核心（纯 P/Invoke，无 C 组件）。
//
// 关键约定：
//  * 库名：libgtk-3.so.0 / libgdk-3.so.0 / libgobject-2.0.so.0 /
//    libglib-2.0.so.0 / libwebkit2gtk-4.1.so.0 / libjavascriptcoregtk-4.1.so.0
//    （apt install libwebkit2gtk-4.1-dev 拉齐依赖）
//  * 信号回调经 g_signal_connect_data + [UnmanagedCallersOnly] trampoline，
//    状态经静态注册表（实例指针 → 托管对象）查找，与 macOS 同一模式；
//  * IPC 回执/ExecuteScript 结果走"页面回环消息"（同 macOS，避开 GAsyncReadyCallback）；
//  * 字符串：入参 StringMarshalling.Utf8；返回的 gchar* 立即复制后 g_free。
// ============================================================================

internal static unsafe partial class GtkNative
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string GOject = "libgobject-2.0.so.0";
    private const string Glib = "libglib-2.0.so.0";
    // GIO 与 GLib 是**两个库**：GInputStream 家族（含下面那个内存流）在 GIO 里。
    // 2026-10-03 把 g_memory_input_stream_new_from_data 挂到 libglib 上，真机运行直接
    // EntryPointNotFoundException（而且是在 scheme 回调里抛的，看起来像"页面加载崩了"）。
    private const string Gio = "libgio-2.0.so.0";
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string JSC = "libjavascriptcoregtk-4.1.so.0";

    // ---- GTK ----

    [LibraryImport(Gtk, EntryPoint = "gtk_init")]
    internal static partial void GtkInit(nint argc, nint argv);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_new")]
    internal static partial nint GtkWindowNew(int type); // GTK_WINDOW_TOPLEVEL = 0

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_title", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkWindowSetTitle(nint window, string title);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_default_size")]
    internal static partial void GtkWindowSetDefaultSize(nint window, int width, int height);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_position")]
    internal static partial void GtkWindowSetPosition(nint window, int position);

    [LibraryImport(Gtk, EntryPoint = "gtk_container_add")]
    internal static partial void GtkContainerAdd(nint container, nint widget);

    /// <summary>把子部件从容器里摘掉（引用计数随之减一；部件自身由我们那次 unref 收尾）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_container_remove")]
    internal static partial void GtkContainerRemove(nint container, nint widget);

    // ---- 布局盒与菜单栏 ----

    /// <summary>纵向布局（菜单栏在上、webview 在下）。</summary>
    public const int GtkOrientationVertical = 1;

    /// <summary>
    /// 建一个布局盒。加菜单栏时窗口不能直接装 webview 了，要改用
    /// <c>GtkBox{menubar, webview}</c>——GTK 的菜单栏是**窗口里的一个部件**，
    /// 与 Windows（菜单栏在非客户区）/ macOS（菜单在屏幕顶部）的模型不同。
    /// </summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_box_new")]
    internal static partial nint GtkBoxNew(int orientation, int spacing);

    /// <summary>
    /// 把子部件装到盒的"起始"端。
    /// </summary>
    /// <remarks>
    /// 多个 <c>pack_start</c> 按**调用顺序**从起始端排开（纵向盒就是从上到下）。
    /// 所以后加的菜单栏要用 <see cref="GtkBoxReorderChild"/> 挪到 0 号位，
    /// 否则它会排到 webview **下面**去。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_box_pack_start")]
    internal static partial void GtkBoxPackStart(
        nint box, nint child, [MarshalAs(UnmanagedType.Bool)] bool expand,
        [MarshalAs(UnmanagedType.Bool)] bool fill, uint padding);

    /// <summary>把子部件挪到盒内的第 <paramref name="position"/> 个位置（0 = 起始端）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_box_reorder_child")]
    internal static partial void GtkBoxReorderChild(nint box, nint child, int position);

    /// <summary>建一个菜单栏（横排的顶层条；子项用 <c>gtk_menu_item_set_submenu</c> 挂下拉）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_menu_bar_new")]
    internal static partial nint GtkMenuBarNew();

    /// <summary>
    /// 取部件的期望高度（最小 / 自然）。
    /// </summary>
    /// <remarks>
    /// 比 <c>gtk_widget_get_allocated_height</c> **早**可用：分配要等一次布局走完，
    /// 而"往布局盒里塞一个菜单栏"之后我们马上就要按菜单栏高度换算最小尺寸/Resize，
    /// 那时分配还没发生、allocated 恒为 0。菜单栏是 <c>expand=false</c> 装进去的，
    /// 它的实际高度就等于自然高度，所以用这个是准的。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_preferred_height")]
    internal static partial void GtkWidgetGetPreferredHeight(nint widget, out int minimumHeight, out int naturalHeight);

    /// <summary>
    /// 要求 GTK 下次布局时重算这个部件的尺寸。
    /// </summary>
    /// <remarks>
    /// 往布局盒里插/摘一个部件之后必须调：否则 webview 的**分配尺寸**要等到别的原因触发布局
    /// 才更新，紧随其后读 <c>InnerSize</c> 会拿到改动之前的值。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_queue_resize")]
    internal static partial void GtkWidgetQueueResize(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_show_all")]
    internal static partial void GtkWidgetShowAll(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_show")]
    internal static partial void GtkWidgetShow(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_main")]
    internal static partial void GtkMain();

    [LibraryImport(Gtk, EntryPoint = "gtk_main_quit")]
    internal static partial void GtkMainQuit();

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_destroy")]
    internal static partial void GtkWidgetDestroy(nint widget);

    // ---- 托盘图标（GtkStatusIcon）----
    // GtkStatusIcon 在 GTK3 里已标 deprecated，但仍是本库唯一"零额外依赖"的方案：
    // 引入 AppIndicator 会拖进 GTK2 时代的库，与进程级类型注册表冲突（Ryn 因此改走纯 D-Bus）。
    // 现代桌面的实际限制（GNOME 需扩展、Wayland 多数不显示）记在 OrielTray 的文档里。

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_new")]
    internal static partial nint GtkStatusIconNew();

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_from_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkStatusIconSetFromFile(nint statusIcon, string filename);

    /// <summary>
    /// 用一个 <c>GdkPixbuf</c> 设置托盘图标。
    /// </summary>
    /// <remarks>
    /// 对标 Tauri 2 的"图标是 RGBA 对象"：<c>GtkStatusIcon</c> 本来就支持直接吃 pixbuf，
    /// 于是 Linux 侧不需要落盘文件（Tauri 因为走 AppIndicator/KSNI 协议而必须落盘，
    /// 见其 <c>tempDirPath</c> 选项——这里走 GTK 自带的状态图标，没有那个约束）。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_from_pixbuf")]
    internal static partial void GtkStatusIconSetFromPixbuf(nint statusIcon, nint pixbuf);

    // ---- GdkPixbuf（RGBA → pixbuf）----
    // libgdk_pixbuf-2.0 是 GTK3 自带的依赖，不引入任何新东西。

    private const string GdkPixbuf = "libgdk_pixbuf-2.0.so.0";

    /// <summary>
    /// 由裸像素数据造 <c>GdkPixbuf</c>。返回的是**新引用**，调用方负责 <c>g_object_unref</c>。
    /// </summary>
    /// <param name="data">像素数据指针；传 0 时 pixbuf 自己分配缓冲（此时 <paramref name="destroyFn"/> 传 0）。</param>
    /// <param name="colorSpace">色彩空间（<c>GDK_COLORSPACE_RGB</c> = 0）。</param>
    /// <param name="hasAlpha">是否含 alpha 通道（1 = 含）。</param>
    /// <param name="bitsPerSample">每通道位数（只支持 8）。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="rowstride">每行字节数（RGBA 为 width*4）。</param>
    /// <param name="destroyFn">缓冲的销毁回调（传 0 表示不需要）。</param>
    /// <param name="destroyData">销毁回调的参数。</param>
    [LibraryImport(GdkPixbuf, EntryPoint = "gdk_pixbuf_new_from_data")]
    internal static partial nint GdkPixbufNewFromData(
        nint data, int colorSpace, int hasAlpha, int bitsPerSample,
        int width, int height, int rowstride, nint destroyFn, nint destroyData);

    /// <summary>取 pixbuf 的像素缓冲指针（对自己分配的 pixbuf 而言可写）。</summary>
    [LibraryImport(GdkPixbuf, EntryPoint = "gdk_pixbuf_get_pixels")]
    internal static partial nint GdkPixbufGetPixels(nint pixbuf);

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_tooltip_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkStatusIconSetTooltipText(nint statusIcon, string text);

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_visible")]
    internal static partial void GtkStatusIconSetVisible(nint statusIcon, int visible);

    /// <summary>图标是否真的进了托盘区（0 = 没有宿主：纯 Wayland 会话、或未装扩展的 GNOME）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_is_embedded")]
    internal static partial int GtkStatusIconIsEmbedded(nint statusIcon);

    /// <summary>
    /// 设置图标旁的文字（<c>GtkStatusIcon:title</c>）。
    /// </summary>
    /// <remarks>
    /// GTK 的文档写明它**不是**画在托盘上的标签（那是 AppIndicator/KSNI 的能力）：
    /// "It may be used by tools like screen readers to render the tray icon"——即无障碍用途。
    /// 因此 Linux 上 <c>title</c> 与 Tauri 的语义**不等价**，见 <c>GtkTrayBackend.SetTitle</c> 的说明。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_title", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkStatusIconSetTitle(nint statusIcon, string title);

    // ---- 菜单（GtkMenu）----

    [LibraryImport(Gtk, EntryPoint = "gtk_menu_new")]
    internal static partial nint GtkMenuNew();

    [LibraryImport(Gtk, EntryPoint = "gtk_menu_item_new_with_label", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GtkMenuItemNewWithLabel(string label);

    [LibraryImport(Gtk, EntryPoint = "gtk_check_menu_item_new_with_label", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GtkCheckMenuItemNewWithLabel(string label);

    [LibraryImport(Gtk, EntryPoint = "gtk_check_menu_item_set_active")]
    internal static partial void GtkCheckMenuItemSetActive(nint menuItem, int isActive);

    [LibraryImport(Gtk, EntryPoint = "gtk_separator_menu_item_new")]
    internal static partial nint GtkSeparatorMenuItemNew();

    [LibraryImport(Gtk, EntryPoint = "gtk_menu_shell_append")]
    internal static partial void GtkMenuShellAppend(nint menuShell, nint child);

    [LibraryImport(Gtk, EntryPoint = "gtk_menu_item_set_submenu")]
    internal static partial void GtkMenuItemSetSubmenu(nint menuItem, nint submenu);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_set_sensitive")]
    internal static partial void GtkWidgetSetSensitive(nint widget, int sensitive);

    /// <summary>控件当前是否"敏感"（接收输入）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_sensitive")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWidgetGetSensitive(nint widget);

    /// <summary>设置"紧急"提示（任务栏里高亮，请求用户注意）。</summary>
    /// <remarks>
    /// GTK **没有**"紧急程度"的分级——只有开/关。所以 Windows 与 macOS 上
    /// Critical / Informational 的区分在 Linux 上会退化成同一件事（见接口注释）。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_urgency_hint")]
    internal static partial void GtkWindowSetUrgencyHint(nint window, [MarshalAs(UnmanagedType.Bool)] bool setting);

    /// <summary>
    /// <c>GdkGeometry</c>：<c>gtk_window_set_geometry_hints</c> 的几何约束。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 <see cref="LayoutKind.Explicit"/> + 显式偏移而不是 Sequential：
    /// 这个结构里 <c>int</c> 与 <c>double</c> 混排，字段偏移要靠对齐规则推——
    /// 而本库在 <c>GdkEventConfigure</c> 上已经吃过一次"手算偏移算错"的亏
    /// （当时 24 vs 20，症状是几何查询给出乱值）。
    /// 显式写出来，再配一条布局测试钉住，就不需要每次读代码时重新推一遍。
    /// </para>
    /// <para>
    /// 对应 C 定义（共 56 字节：9 个 <c>gint</c> + 2 个 <c>gdouble</c>，按 8 字节对齐）：
    /// <code>
    /// struct GdkGeometry {
    ///   gint min_width, min_height, max_width, max_height;
    ///   gint base_width, base_height;
    ///   gint width_inc, height_inc;
    ///   gdouble min_aspect, max_aspect;
    ///   GdkGravity win_gravity;
    /// };
    /// </code>
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = GdkGeometrySize)]
    internal struct GdkGeometry
    {
        /// <summary>结构大小（字节）。测试会断言它与 <c>Marshal.SizeOf</c> 一致。</summary>
        internal const int GdkGeometrySize = 56;

        [FieldOffset(0)]
        public int min_width;

        [FieldOffset(4)]
        public int min_height;

        [FieldOffset(8)]
        public int max_width;

        [FieldOffset(12)]
        public int max_height;

        [FieldOffset(16)]
        public int base_width;

        [FieldOffset(20)]
        public int base_height;

        [FieldOffset(24)]
        public int width_inc;

        [FieldOffset(28)]
        public int height_inc;

        // 两个 gdouble 从 8 的倍数偏移开始（32 已是 8 的倍数）
        [FieldOffset(32)]
        public double min_aspect;

        [FieldOffset(40)]
        public double max_aspect;

        [FieldOffset(48)]
        public int win_gravity;

        // 52..55 是尾部填充（结构按 8 字节对齐）
    }

    /// <summary>设置几何约束（本库只用来声明最大尺寸，见 <c>LinuxWindowHost.SetMaxSize</c>）。</summary>
    /// <param name="window">顶层窗口。</param>
    /// <param name="geometryWidget">几何提示的参照控件；传 0 表示窗口自身。</param>
    /// <param name="geometry">约束值。</param>
    /// <param name="geomMask"><c>GdkWindowHints</c> 位掩码，只有置位的字段才生效。</param>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_geometry_hints")]
    internal static partial void GtkWindowSetGeometryHints(
        nint window, nint geometryWidget, ref GdkGeometry geometry, int geomMask);

    /// <summary>在指针位置弹出菜单；triggerEvent 传 0 表示用当前指针位置（GTK 3.22+）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_menu_popup_at_pointer")]
    internal static partial void GtkMenuPopupAtPointer(nint menu, nint triggerEvent);

    /// <summary>
    /// 在 <paramref name="relativeTo"/> 的矩形处弹出（GTK 3.22+）。
    /// </summary>
    /// <param name="menu">菜单。</param>
    /// <param name="relativeTo">定位基准的 <c>GdkWindow</c>（这里是 webview 的）。</param>
    /// <param name="rect">基准矩形（坐标是 <paramref name="relativeTo"/> 内的**应用像素**）。</param>
    /// <param name="rectAnchor">矩形上的哪个锚点。</param>
    /// <param name="menuAnchor">菜单的哪个点对准该锚点。</param>
    /// <param name="triggerEvent">
    /// 触发事件。<b>可以为 0</b>——与 <see cref="GtkMenuPopupAtPointer"/> 不同：
    /// 那条路径没有事件就定不了位，而这条的定位信息全在矩形里。
    /// </param>
    [LibraryImport(Gtk, EntryPoint = "gtk_menu_popup_at_rect")]
    internal static partial void GtkMenuPopupAtRect(
        nint menu, nint relativeTo, ref GdkRectangle rect, int rectAnchor, int menuAnchor, nint triggerEvent);

    /// <summary>收起菜单（没有在显示时是空操作）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_menu_popdown")]
    internal static partial void GtkMenuPopdown(nint menu);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_close")]
    internal static partial void GtkWindowClose(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_maximize")]
    internal static partial void GtkWindowMaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_unmaximize")]
    internal static partial void GtkWindowUnmaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_is_maximized")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowIsMaximized(nint window);

    /// <summary>窗口是不是"活动窗口"（有焦点那个）。</summary>
    /// <remarks>
    /// 用 <c>gtk_window_is_active</c> 而不是 <c>has_toplevel_focus</c>：
    /// 后者在**同一个应用内**切换窗口时可能仍为真（顶层容器仍有焦点），
    /// 而前者的语义就是"这是活动窗口"。多窗口应用必须按窗口判。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_is_active")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowIsActive(nint window);

    /// <summary>窗口当前是否可缩放（与 <c>gtk_window_set_resizable</c> 配对）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_resizable")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowGetResizable(nint window);

    /// <summary>取 GDK 窗口的状态位（最小化、贴边、全屏…）。</summary>
    /// <remarks>
    /// 返回值是 <c>GdkWindowState</c> 的位掩码，要按位判——
    /// 见 <c>LinuxWindowHost.IsMinimized</c>（用 <c>GDK_WINDOW_STATE_ICONIFIED</c>）。
    /// </remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_window_get_state")]
    internal static partial int GdkWindowGetState(nint gdkWindow);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_is_fullscreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowIsFullscreen(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_fullscreen")]
    internal static partial void GtkWindowFullscreen(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_unfullscreen")]
    internal static partial void GtkWindowUnfullscreen(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_keep_above")]
    internal static partial void GtkWindowSetKeepAbove(nint window, [MarshalAs(UnmanagedType.Bool)] bool above);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_move")]
    internal static partial void GtkWindowMove(nint window, int x, int y);

    /// <summary>
    /// 开始窗口拖动：把移动交给窗口管理器 / 窗口系统，而不是客户端自己摆。
    /// X11 下 GDK 发 <c>_NET_WM_MOVERESIZE</c> 给 WM；Wayland 下转成 <c>xdg_toplevel.move</c>，
    /// 由合成器接管（协议不允许客户端指定自己的位置，所以这是唯一的正路）。
    /// </summary>
    /// <remarks>
    /// <paramref name="button"/> 是触发拖动的按钮（左键 = 1）；<paramref name="rootX"/> /
    /// <paramref name="rootY"/> 是按下点的根窗口坐标；<paramref name="timestamp"/> 是那次点击事件的
    /// 时间（<c>GDK_CURRENT_TIME</c> = 0）。Wayland 下后三个参数不被使用——协议只吃 seat + serial，
    /// 而 GTK3 的 Wayland 后端取的是 seat 上**最近一次隐式抓取的 serial**，也就是按钮按下时记录的那个：
    /// 所以这个调用必须在鼠标按住期间完成，晚到松开之后就会被合成器忽略。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_begin_move_drag")]
    internal static partial void GtkWindowBeginMoveDrag(nint window, int button, int rootX, int rootY, uint timestamp);

    // ---- 无边框窗口的边缘 resize（见 LinuxResizeSupport 的说明）----
    //
    // 窗口一旦 set_decorated(false)，WM 就不再提供 resize 边框，只能自己判边缘命中、
    // 再把 resize 交回给 WM/合成器。这条路径与 gtk_window_begin_move_drag 同构：
    // Wayland 下同样必须在**按住期间**发出（协议只吃 seat + serial）。

    /// <summary>
    /// 开始一次 resize 拖动。<paramref name="edge"/> 是 <c>GdkWindowEdge</c>（见
    /// <see cref="GdkWindowEdge"/>）；<paramref name="rootX"/>/<paramref name="rootY"/> 是按下点的根窗口坐标，
    /// <paramref name="timestamp"/> 取自那次点击事件。
    /// </summary>
    /// <remarks>
    /// "When GDK can support it, the resize will be done using the standard mechanism for the window manager
    /// or windowing system." —— 也就是 X11 下交给 WM（有边缘吸附/贴边平铺）、Wayland 下转成
    /// <c>xdg_toplevel.resize</c> 交给合成器。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_begin_resize_drag")]
    internal static partial void GtkWindowBeginResizeDrag(
        nint window, int edge, int button, int rootX, int rootY, uint timestamp);

    /// <summary>取部件的 GdkWindow（未 realize 时为 0）。用于给 webview 单独设鼠标形状。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_window")]
    internal static partial nint GtkWidgetGetWindow(nint widget);

    /// <summary>往部件的事件掩码里加位。边缘 resize 的 motion 事件要靠它才会被投递。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_add_events")]
    internal static partial void GtkWidgetAddEvents(nint widget, int events);

    /// <summary><c>GDK_POINTER_MOTION_MASK</c>（GTK3 <c>gdk/gdkevents.h</c>：<c>1 &lt;&lt; 2</c>）。</summary>
    internal const int GdkPointerMotionMask = 1 << 2;

    /// <summary>部件被分配的宽度（逻辑像素）。用它而不是窗口尺寸：热区判定是在 webview 的坐标系里做的。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_allocated_width")]
    internal static partial int GtkWidgetGetAllocatedWidth(nint widget);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_allocated_height")]
    internal static partial int GtkWidgetGetAllocatedHeight(nint widget);

    /// <summary>
    /// 给某个 GdkWindow 设鼠标形状；<paramref name="cursor"/> 传 0 表示恢复默认。
    /// </summary>
    /// <remarks>
    /// 必须设在 **webview 自己的** GdkWindow 上，不能设在顶层窗口上：顶层窗口设的光标会被子窗口
    /// （webview）自己的光标盖掉，等于没设。
    /// </remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_window_set_cursor")]
    internal static partial void GdkWindowSetCursor(nint window, nint cursor);

    /// <summary>
    /// 按 <c>GdkCursorType</c> 造一个光标。返回的是**新引用**，调用方负责 <c>g_object_unref</c>。
    /// </summary>
    /// <remarks>
    /// 这个函数在 GTK 3.10 起被标记为 deprecated，官方推荐 <c>gdk_cursor_new_from_name</c>。
    /// 这里仍用它，是因为**名字**在不同光标主题下并不保证解析得到对应形状，而这个枚举值直接对应
    /// X 光标字体里的那一张图——我们要的是"形状确定"，不是"跟主题走"。GTK3 的生命周期内它不会消失。
    /// </remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_cursor_new_for_display")]
    internal static partial nint GdkCursorNewForDisplay(nint display, int cursorType);

    /// <summary>取事件的部件内坐标（逻辑像素）；返回 FALSE 表示该事件没有坐标（例如键盘事件）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_event_get_coords")]
    internal static partial int GdkEventGetCoords(nint eventPtr, out double x, out double y);

    /// <summary>取事件的根窗口坐标。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_event_get_root_coords")]
    internal static partial int GdkEventGetRootCoords(nint eventPtr, out double xRoot, out double yRoot);

    /// <summary>取事件的鼠标按键号（1 = 左键）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_event_get_button")]
    internal static partial int GdkEventGetButton(nint eventPtr, out uint button);

    /// <summary>取事件时间戳（<c>GDK_CURRENT_TIME</c> = 0）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_event_get_time")]
    internal static partial uint GdkEventGetTime(nint eventPtr);

    /// <summary>默认 GdkDisplay。用于判断当前跑在哪个 GDK 后端上。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_default")]
    internal static partial nint GdkDisplayGetDefault();

    /// <summary>
    /// 显示名：X11 形如 <c>:0</c>，Wayland 形如 <c>wayland-0</c>（取自 <c>WAYLAND_DISPLAY</c>）。
    /// </summary>
    /// <remarks>
    /// 返回裸指针、由调用方复制，**不要**写成
    /// <c>[return: MarshalAs(UnmanagedType.LPUTF8Str)]</c> + <c>string</c> 返回。
    /// 那样源生成器为返回值生成的封送代码会写坏调用者的栈，表现为调用后立刻
    /// <c>*** stack smashing detected ***: terminated</c> 并 abort
    /// （实测于 .NET 10 / Linux x64，且只在真正走到该调用时才触发——不碰这条路径时进程一切正常，
    /// 极难与"某个功能一用就闪退"对上号）。
    ///
    /// 本文件其它函数也一律是这个形态：返回的 <c>gchar*</c> 复制后再按需 <c>g_free</c>。
    /// 指针本身归 GDK 所有，不释放。
    /// </remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_name")]
    internal static partial nint GdkDisplayGetName(nint display);

    // ---- GDK：显示器与光标（IMonitorBackend 的 Linux 实现）----
    //
    // 坐标单位必须说清楚：GDK3 的 monitor geometry 与 device position 都是**应用像素**
    // （logical），X11 下由 GTK 按 scale factor 换算过、Wayland 下由合成器协商。
    // 物理像素 = 应用像素 × gdk_monitor_get_scale_factor，与 Windows/macOS 的报告口径一致。

    /// <summary>显示器数量。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_n_monitors")]
    internal static partial int GdkDisplayGetNMonitors(nint display);

    /// <summary>按下标取显示器（0..n-1；越界返回 NULL）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_monitor")]
    internal static partial nint GdkDisplayGetMonitor(nint display, int monitorNum);

    /// <summary>主显示器；没有主屏概念的后端可能返回 NULL（GTK 文档明确允许）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_primary_monitor")]
    internal static partial nint GdkDisplayGetPrimaryMonitor(nint display);

    /// <summary>包含该点的显示器（GDK 3.22+）；点不在任何屏上时返回 NULL。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_monitor_at_point")]
    internal static partial nint GdkDisplayGetMonitorAtPoint(nint display, int x, int y);

    /// <summary>包含该 GdkWindow 的显示器。窗口未 realize（<c>gdk_window</c> 为 NULL）时返回 NULL。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_monitor_at_window")]
    internal static partial nint GdkDisplayGetMonitorAtWindow(nint display, nint gdkWindow);

    /// <summary>显示器几何（应用像素）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_monitor_get_geometry")]
    internal static partial void GdkMonitorGetGeometry(nint monitor, out GdkRectangle rect);

    /// <summary>显示器缩放比（整数，≥1）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_monitor_get_scale_factor")]
    internal static partial int GdkMonitorGetScaleFactor(nint monitor);

    /// <summary>显示器型号名（<c>const gchar*</c>，归 GDK 所有；可能为 NULL）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_monitor_get_model")]
    internal static partial nint GdkMonitorGetModel(nint monitor);

    /// <summary>是否主屏。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_monitor_is_primary")]
    internal static partial int GdkMonitorIsPrimary(nint monitor);

    /// <summary>默认 seat（取指针设备的入口）。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_display_get_default_seat")]
    internal static partial nint GdkDisplayGetDefaultSeat(nint display);

    /// <summary>seat 的指针设备。</summary>
    [LibraryImport(Gdk, EntryPoint = "gdk_seat_get_pointer")]
    internal static partial nint GdkSeatGetPointer(nint seat);

    /// <summary>
    /// 指针在屏幕坐标里的位置（应用像素）。<paramref name="screen"/> 是 <c>GdkScreen**</c>。
    /// </summary>
    /// <remarks>拿不到时 <paramref name="x"/> / <paramref name="y"/> 是 -1（GTK 的约定）。</remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_device_get_position")]
    internal static partial void GdkDeviceGetPosition(nint device, out nint screen, out int x, out int y);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_resize")]
    internal static partial void GtkWindowResize(nint window, int width, int height);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_decorated")]
    internal static partial void GtkWindowSetDecorated(nint window, [MarshalAs(UnmanagedType.Bool)] bool decorated);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_resizable")]
    internal static partial void GtkWindowSetResizable(nint window, [MarshalAs(UnmanagedType.Bool)] bool resizable);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_present")]
    internal static partial void GtkWindowPresent(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_iconify")]
    internal static partial void GtkWindowIconify(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_hide")]
    internal static partial void GtkWidgetHide(nint widget);

    /// <summary>部件当前是否可见（<c>gtk_widget_get_visible</c>；<c>gtk_widget_hide</c> 之后为 false）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_visible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWidgetGetVisible(nint widget);

    // GTK3 的 gtk_window_get_position 返回 void（GTK4 才改成 gboolean）：
    // 声明成 bool 会让封送层去读一个没有意义的返回寄存器（评审 P3）。
    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_position")]
    internal static partial void GtkWindowGetPosition(nint window, out int x, out int y);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_size")]
    internal static partial void GtkWindowGetSize(nint window, out int width, out int height);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_screen")]
    internal static partial nint GtkWindowGetScreen(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_widget_set_size_request")]
    internal static partial void GtkWidgetSetSizeRequest(nint widget, int width, int height);

    [LibraryImport(Gtk, EntryPoint = "gtk_message_dialog_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GtkMessageDialogNew(nint parent, uint flags, int type, int buttons, string format, string arg0);

    [LibraryImport(Gtk, EntryPoint = "gtk_dialog_run")]
    internal static partial int GtkDialogRun(nint dialog);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_dialog_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GtkFileChooserDialogNew(string title, nint parent, int action, nint firstButtonText);

    [LibraryImport(Gtk, EntryPoint = "gtk_dialog_add_button", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkDialogAddButton(nint dialog, string text, int responseId);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_get_filename")]
    internal static partial nint GtkFileChooserGetFilename(nint chooser);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_set_do_overwrite_confirmation")]
    internal static partial void GtkFileChooserSetDoOverwriteConfirmation(nint chooser, [MarshalAs(UnmanagedType.Bool)] bool confirm);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_set_current_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkFileChooserSetCurrentName(nint chooser, string name);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_set_current_folder", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GtkFileChooserSetCurrentFolder(nint chooser, string filename);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_set_select_multiple")]
    internal static partial void GtkFileChooserSetSelectMultiple(nint chooser, [MarshalAs(UnmanagedType.Bool)] bool selectMultiple);

    /// <summary>多选结果：返回需 <see cref="GSListFree"/> 的 GSList，元素是需 g_free 的路径串。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_get_filenames")]
    internal static partial nint GtkFileChooserGetFilenames(nint chooser);

    [LibraryImport(Gtk, EntryPoint = "gtk_file_filter_new")]
    internal static partial nint GtkFileFilterNew();

    [LibraryImport(Gtk, EntryPoint = "gtk_file_filter_set_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkFileFilterSetName(nint filter, string name);

    /// <summary>给过滤器加一条通配模式（<c>*.txt</c>）；同名模式重复加没有副作用。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_file_filter_add_pattern", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkFileFilterAddPattern(nint filter, string pattern);

    /// <summary>把过滤器挂到 chooser 上；chooser 取得所有权，无需手动释放 filter。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_file_chooser_add_filter")]
    internal static partial void GtkFileChooserAddFilter(nint chooser, nint filter);

    // ---- GSList（glib）----
    // 只用到三个操作：长度、按下标取数据、整体释放。元素本身由调用方逐个 g_free。

    [LibraryImport(Glib, EntryPoint = "g_slist_length")]
    internal static partial uint GSListLength(nint list);

    /// <summary>取第 n 个元素的数据指针；越界返回 0（GTK 不检查，由调用方保证 n 在范围内）。</summary>
    [LibraryImport(Glib, EntryPoint = "g_slist_nth_data")]
    internal static partial nint GSListNthData(nint list, uint n);

    [LibraryImport(Glib, EntryPoint = "g_slist_free")]
    internal static partial void GSListFree(nint list);

    // ---- GDK ----

    [LibraryImport(Gdk, EntryPoint = "gdk_screen_get_width")]
    internal static partial int GdkScreenGetWidth(nint screen);

    [LibraryImport(Gdk, EntryPoint = "gdk_screen_get_height")]
    internal static partial int GdkScreenGetHeight(nint screen);

    /// <summary>
    /// 控件所在显示器的缩放比（1 = 100%）。
    /// </summary>
    /// <remarks>
    /// 用它而不是 <c>gdk_monitor_get_scale_factor</c>：后者要自己从控件找 monitor，
    /// 而 <c>gtk_widget_get_scale_factor</c> 已经处理了"控件还没 realize 时回落到默认显示器"。
    /// GTK 3.10+ 才有；更老的版本上符号解析失败会让整个库加载不了，但本库要求的
    /// libgtk-3 版本远高于此（见 README 的平台要求）。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_scale_factor")]
    internal static partial int GtkWidgetGetScaleFactor(nint widget);

    /// <summary>
    /// 窗口**含装饰**的边框矩形（根窗口坐标）。
    /// </summary>
    /// <remarks>
    /// <b>Wayland 下会失败或只返回客户区</b>：合成器不告诉客户端装饰尺寸，GDK 只能报
    /// 它自己知道的那部分。所以调用方必须容忍"外框 == 客户区"——这是平台事实
    /// （Tauri 的 <c>outer_size</c> 在 Wayland 上同样不可靠）。
    /// </remarks>
    [LibraryImport(Gdk, EntryPoint = "gdk_window_get_frame_extents")]
    internal static partial void GdkWindowGetFrameExtents(nint gdkWindow, out GdkRectangle rect);

    // ---- 剪贴板（GtkClipboard）----
    //
    // 用法是 `gtk_clipboard_get(GDK_SELECTION_CLIPBOARD)` 拿到那个**单例**，
    // 然后 wait_for_text / set_text。注意：
    //
    //   * `gdk_atom_intern_static_string` 拿到的是 atom，必须用**静态**那个变体
    //     （`gdk_atom_intern` 每次都会新建一个 atom，长期跑会累积）。
    //   * `wait_for_text` 是**同步阻塞**的：它会跑一个内嵌的 main loop 等剪贴板所有者应答。
    //     所以它必须在**主线程**上调，而且对方卡住时这里也会卡住。
    //     这不是偷懒——GTK 的剪贴板是异步 API，没有"非阻塞地取一次"这种东西，
    //     `wait_for_text` 就是它给同步调用方的那个入口。见 LinuxPlatformBackend 的说明。
    //   * `set_text` 之后必须 `gtk_clipboard_store`，否则应用退出后剪贴板内容就没了
    //     （X11 的剪贴板是"由所有者提供内容"的模型，不是把数据交给系统）。

    [LibraryImport(Gdk, EntryPoint = "gdk_atom_intern_static_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GdkAtomInternStaticString(string atomName);

    [LibraryImport(Gtk, EntryPoint = "gtk_clipboard_get")]
    internal static partial nint GtkClipboardGet(nint selection);

    [LibraryImport(Gtk, EntryPoint = "gtk_clipboard_wait_for_text")]
    internal static partial nint GtkClipboardWaitForText(nint clipboard);

    [LibraryImport(Gtk, EntryPoint = "gtk_clipboard_set_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkClipboardSetText(nint clipboard, string text, int len);

    [LibraryImport(Gtk, EntryPoint = "gtk_clipboard_clear")]
    internal static partial void GtkClipboardClear(nint clipboard);

    [LibraryImport(Gtk, EntryPoint = "gtk_clipboard_store")]
    internal static partial void GtkClipboardStore(nint clipboard);

    // ---- GObject / Glib ----

    [LibraryImport(GOject, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint GSignalConnectData(nint instance, string detailedSignal, void* handler, nint data, nint destroyData, uint flags);

    [LibraryImport(Glib, EntryPoint = "g_idle_add_full")]
    internal static partial uint GIdleAddFull(int priority, void* function, nint data, nint notify);

    [LibraryImport(Glib, EntryPoint = "g_free")]
    internal static partial void GFree(nint ptr);

    // ---- WebKitGTK 4.1 ----

    [LibraryImport(WebKit, EntryPoint = "webkit_user_content_manager_new")]
    internal static partial nint WebkitUserContentManagerNew();

    [LibraryImport(WebKit, EntryPoint = "webkit_user_content_manager_register_script_message_handler", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitUserContentManagerRegisterScriptMessageHandler(nint manager, string name);

    // 原型（WebKitGTK 4.1）：webkit_user_script_new(const char* source,
    //     WebKitUserContentInjectedFrames injected_frames,   // 0 = ALL_FRAMES，1 = TOP_FRAME
    //     WebKitUserScriptInjectionTime injection_time,      // 0 = AT_DOCUMENT_START，1 = AT_DOCUMENT_END
    //     const char* allow_list, const char* block_list)
    // 注意两个枚举的顺序：frames 在前、time 在后（写反会把脚本注入到文档末尾）。
    [LibraryImport(WebKit, EntryPoint = "webkit_user_script_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint WebkitUserScriptNew(string source, int injectedFrames, int injectionTime, nint allowList, nint blockList);

    // 4.1 的函数名是 add_script；webkit_user_content_manager_add_user_script 是 WebKit1 的名字，4.1 里不存在。
    [LibraryImport(WebKit, EntryPoint = "webkit_user_content_manager_add_script")]
    internal static partial void WebkitUserContentManagerAddScript(nint manager, nint script);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_new_with_user_content_manager")]
    internal static partial nint WebkitWebViewNewWithUserContentManager(nint manager);

    // ---- DevTools（对应 Windows 的 ICoreWebView2Settings.AreDevToolsEnabled）----

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_new")]
    internal static partial nint WebkitSettingsNew();

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_set_enable_developer_extras")]
    internal static partial void WebkitSettingsSetEnableDeveloperExtras(nint settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_set_settings")]
    internal static partial void WebkitWebViewSetSettings(nint webview, nint settings);

    [LibraryImport(GOject, EntryPoint = "g_object_unref")]
    internal static partial void GObjectUnref(nint obj);

    /// <summary>
    /// 接管一个 <c>GInitiallyUnowned</c>（GTK widget）的 floating 引用。
    /// </summary>
    /// <remarks>
    /// 所有 widget 创建时都带 floating 引用，直接 <c>g_object_unref</c> 会打乱引用计数——
    /// GTK 会打 "A floating object was finalized"，对象可能被销毁两次。持有 widget 的一方
    /// 要先 sink，之后的 unref 才是配对的。
    /// </remarks>
    [LibraryImport(GOject, EntryPoint = "g_object_ref_sink")]
    internal static partial nint GObjectRefSink(nint obj);

    // ---- 窗口图标 ----

    /// <summary>
    /// 从文件设置窗口图标。X11 下 GTK 会把它写进 <c>_NET_WM_ICON</c>（可用 xprop 验证）；
    /// Wayland 下窗口图标由合成器决定，多数合成器忽略它（那时以 desktop 文件的 Icon 为准）。
    /// error 传 0 表示不接收 GError（加载失败即静默保持无图标）。
    /// </summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_icon_from_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GtkWindowSetIconFromFile(nint window, string filename, nint error);

    /// <summary>
    /// 用一个 <c>GdkPixbuf</c> 设置窗口图标（对标 <c>gtk_window_set_icon</c>）。
    /// </summary>
    /// <remarks>
    /// 这是"从内存里的图像数据设图标"那条路——<c>_from_file</c> 那条要求先落盘，
    /// 而页面给的字节本来就在内存里，为它写一个临时文件既慢又留下垃圾。
    /// GTK 会**接管这个 pixbuf 的引用**（<c>gtk_window_set_icon</c> 会 ref 一次），
    /// 所以调用方仍然要自己 <c>g_object_unref</c> 掉自己那份（见调用点）。
    /// </remarks>
    [LibraryImport(Gtk, EntryPoint = "gtk_window_set_icon")]
    internal static partial void GtkWindowSetIcon(nint window, nint pixbuf);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_load_uri", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebViewLoadUri(nint webview, string uri);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_title")]
    internal static partial nint WebkitWebViewGetTitle(nint webview);

    // ---- 导航历史与重载 ----

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_go_back")]
    internal static partial void WebkitWebViewGoBack(nint webview);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_go_forward")]
    internal static partial void WebkitWebViewGoForward(nint webview);

    /// <summary>重新加载当前页面。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_reload")]
    internal static partial void WebkitWebViewReload(nint webview);

    /// <summary>是否有可后退/可前进的历史记录（返回 gboolean）。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_can_go_back")]
    internal static partial int WebkitWebViewCanGoBack(nint webview);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_can_go_forward")]
    internal static partial int WebkitWebViewCanGoForward(nint webview);

    /// <summary>当前文档 URI（返回 gchar*，归 webview 所有，不要释放）。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_uri")]
    internal static partial nint WebkitWebViewGetUri(nint webview);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_evaluate_javascript", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebViewEvaluateJavaScript(nint webview, string script, nint length, nint worldName, nint sourceUri, nint cancellable, nint callback, nint userData);

    /// <summary>
    /// 设置"内核就绪前的底色"（见 <c>OrielWindowOptions.BackgroundColor</c>）。
    /// </summary>
    /// <remarks>
    /// 已在 WSL 的 <c>libwebkit2gtk-4.1</c> 上确认本符号存在（<c>nm -D</c> 可见 set/get 两个）。
    /// 该 API 自 WebKitGTK 2.40 起被标记 deprecated（官方推荐改用页面 CSS），但没有被移除，
    /// 仍是有效的引擎级开关——页面 CSS 生效之前那一帧只有它能管。
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_set_background_color")]
    internal static partial void WebkitWebViewSetBackgroundColor(nint webview, in GdkRGBA rgba);

    [LibraryImport(WebKit, EntryPoint = "webkit_javascript_result_get_js_value")]
    internal static partial nint WebkitJavascriptResultGetJsValue(nint result);

    [LibraryImport(JSC, EntryPoint = "jsc_value_to_string")]
    internal static partial nint JscValueToString(nint value);

    // ---- 内嵌资源：自定义 scheme oriel:// ----
    //
    // 为什么是自定义 scheme 而不是 file://：见 Assets/AssetUrl.cs 与 EmbeddedAssetStore.cs。
    // 这里只列一条最实际的差别——file:// 页面是不透明来源，fetch 相对路径、localStorage、
    // 安全上下文全都要看引擎脸色且三平台不一致；oriel:// 有自己的 authority，来源稳定。

    /// <summary>进程级的默认 WebKitWebContext。</summary>
    /// <remarks>
    /// scheme 必须注册在**任何 webview 创建之前**，所以调用点在 LinuxWindowHost.Create 里
    /// 建 view 之前那一行；拿默认 context 是安全的（webview 也是用它）。
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_context_get_default")]
    internal static partial nint WebkitWebContextGetDefault();

    /// <summary>
    /// 注册自定义 scheme 的处理回调。
    /// </summary>
    /// <remarks>
    /// C 侧回调签名是 <c>void (*)(WebKitURISchemeRequest* request, gpointer user_data)</c>——
    /// 与 <c>LinuxSignalHandlers</c> 里的 trampoline 同一形态（<c>delegate* unmanaged</c> + <c>[UnmanagedCallersOnly]</c>）。
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_context_register_uri_scheme", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebContextRegisterUriScheme(nint context, string scheme, void* callback, nint userData, nint destroyNotify);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_context_get_security_manager")]
    internal static partial nint WebkitWebContextGetSecurityManager(nint context);

    /// <summary>
    /// 把自定义 scheme 标记为安全来源。
    /// </summary>
    /// <remarks>
    /// 自定义 scheme 默认**不是**安全上下文：<c>window.isSecureContext</c> 为假、
    /// <c>crypto.subtle</c> 与部分存储 API 直接不可用。Linux 有这条公开开关可按；
    /// macOS 的 WKWebView 没有对应 API（那边只能靠实测确认差异）——这条不对称是本库
    /// 选择 scheme 方案时唯一需要背的账。
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_security_manager_register_uri_scheme_as_secure", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitSecurityManagerRegisterUriSchemeAsSecure(nint manager, string scheme);

    /// <summary>
    /// 把自定义 scheme 标记为 **CORS 启用**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条是 <c>&lt;script type="module"&gt;</c> 能加载的前提</b>：ES module 的抓取走
    /// CORS 检查（module script 一律以 CORS 模式请求），而未登记的 scheme 会被直接拒绝
    /// ——表现是页面脚本**静默不执行**（控制台一条 CORS/module 错误，而宿主侧看不到），
    /// 于是"页面什么都没发生"：主题没应用、导航脚本没跑、探针没回报。
    /// </para>
    /// <para>
    /// 与 <c>..._as_secure</c> 是两件不同的事：secure 管 <c>isSecureContext</c>，
    /// CORS 管"这个 scheme 的资源能不能被同 scheme 的文档当 module/跨源请求抓取"。
    /// WebKitGTK 上两者都要显式登记（官方文档里 module 那条明确要求 CORS enabled）。
    /// </para>
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_security_manager_register_uri_scheme_as_cors_enabled", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitSecurityManagerRegisterUriSchemeAsCorsEnabled(nint manager, string scheme);

    /// <summary>请求的完整 URI（返回 gchar*，归 request 所有，不要释放）。</summary>
    /// <remarks>
    /// 用完整 URI 而不是 <c>get_path</c>：路径的百分号解码、query 与 fragment 的处理
    /// 交给 <see cref="Uri"/>，与 Windows/macOS 两侧走的是同一套解析（AssetUrl）。
    /// </remarks>
    [LibraryImport(WebKit, EntryPoint = "webkit_uri_scheme_request_get_uri")]
    internal static partial nint WebkitUriSchemeRequestGetUri(nint request);

    /// <summary>用一段内存流应答请求（stream 交给 WebKit 异步读完，本函数返回后可释放我们自己那份引用）。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_uri_scheme_request_finish", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitUriSchemeRequestFinish(nint request, nint stream, long streamLength, string contentType);

    /// <summary>以错误结束请求（域必须是 WEBKIT_NETWORK_ERROR，见 LinuxAssetScheme）。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_uri_scheme_request_finish_error")]
    internal static partial void WebkitUriSchemeRequestFinishError(nint request, nint error);

    /// <summary>WEBKIT_NETWORK_ERROR 的 GQuark（<c>finish_error</c> 只认这个域）。</summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_network_error_quark")]
    internal static partial uint WebkitNetworkErrorQuark();

    /// <summary>从一段内存建输入流；<paramref name="destroyNotify"/> 非空时，流销毁时会用它释放那段内存。</summary>
    [LibraryImport(Gio, EntryPoint = "g_memory_input_stream_new_from_data")]
    internal static partial nint GMemoryInputStreamNewFromData(nint data, nint length, nint destroyNotify);

    [LibraryImport(Glib, EntryPoint = "g_error_new_literal", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GErrorNewLiteral(uint domain, int code, string message);

    /// <summary>释放 GError。</summary>
    /// <remarks>
    /// <c>webkit_uri_scheme_request_finish_error</c> 的 error 参数是 "in"（GIR 里 transfer-ownership
    /// 为 none），WebKit 只读不接管——不释放就是每次 404 漏一块，释放两次则会崩。
    /// </remarks>
    [LibraryImport(Glib, EntryPoint = "g_error_free")]
    internal static partial void GErrorFree(nint error);

    // ---- GtkSettings（系统主题）----

    /// <summary>进程级的 GtkSettings 单例（首次调用时创建）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_settings_get_default")]
    internal static partial nint GtkSettingsGetDefault();

    /// <summary>按名字取 GType（如 "gboolean"、"gchararray"）——比硬编码 G_TYPE_* 常量稳妥。</summary>
    [LibraryImport(GOject, EntryPoint = "g_type_from_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint GTypeFromName(string name);

    [LibraryImport(GOject, EntryPoint = "g_value_init")]
    internal static partial nint GValueInit(nint value, nuint type);

    [LibraryImport(GOject, EntryPoint = "g_value_unset")]
    internal static partial void GValueUnset(nint value);

    [LibraryImport(GOject, EntryPoint = "g_object_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GObjectGetProperty(nint obj, string propertyName, nint value);

    /// <summary>写 GObject 属性（B15 的 <c>Window.setTheme</c> 落点）。</summary>
    [LibraryImport(GOject, EntryPoint = "g_object_set_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GObjectSetProperty(nint obj, string propertyName, nint value);

    /// <summary>把 GValue 置成一个布尔值（B15 用；读方向见 <see cref="GValueGetBoolean"/>）。</summary>
    [LibraryImport(GOject, EntryPoint = "g_value_set_boolean")]
    internal static partial void GValueSetBoolean(nint value, int v);

    [LibraryImport(GOject, EntryPoint = "g_value_get_boolean")]
    internal static partial int GValueGetBoolean(nint value);

    /// <summary>取 GValue 里的 int（<c>gtk-double-click-time</c> 这类以毫秒计的设置是 "gint"）。</summary>
    [LibraryImport(GOject, EntryPoint = "g_value_get_int")]
    internal static partial int GValueGetInt(nint value);

    /// <summary>取 GValue 里的字符串（归 GValue 所有，不要释放）。</summary>
    [LibraryImport(GOject, EntryPoint = "g_value_get_string")]
    internal static partial nint GValueGetString(nint value);

    // ---- 文件拖放（gtk_drag_dest_*）----
    // 在 webview 上注册 URIs 类型作为落点；载荷由 GTK 解析成 uri 列表，我们只负责把
    // file:// 转成本地路径（OrielFileDropSupport.UriToPath，纯函数、有单测）。

    /// <summary><c>GTK_DEST_DEFAULT_ALL</c>：高亮、跟踪与释放都交给 GTK 处理。</summary>
    public const int GtkDestDefaultAll = 0x07;

    /// <summary><c>GDK_ACTION_COPY</c>：只接受复制语义（拖放不改动来源）。</summary>
    public const uint GdkActionCopy = 1 << 1;

    /// <summary>把 widget 注册为拖放目标（targets 的内存只需存活到本调用返回）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_drag_dest_set")]
    internal static partial void GtkDragDestSet(nint widget, int flags, nint targets, int targetCount, uint actions);

    /// <summary>已注册的落点类型；返回 0 表示没注册上（自检用它断言"真的设进去了"）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_drag_dest_get_target_list")]
    internal static partial nint GtkDragDestGetTargetList(nint widget);

    /// <summary>
    /// 取拖放载荷里的 uri 列表（已按行拆好，需 <see cref="GStrfreev"/> 释放）；没有 uris 时返回 0。
    /// </summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_selection_data_get_uris")]
    internal static partial nint GtkSelectionDataGetUris(nint selection);

    /// <summary>通知拖放源已完成；<paramref name="deleteData"/> 表示是否要求源端删除原数据。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_drag_finish")]
    internal static partial void GtkDragFinish(nint context, [MarshalAs(UnmanagedType.Bool)] bool success, [MarshalAs(UnmanagedType.Bool)] bool deleteData, uint time);

    /// <summary>释放以 null 结尾的字符串数组（<c>g_strfreev</c>：元素与数组一起释放）。</summary>
    [LibraryImport(Glib, EntryPoint = "g_strfreev")]
    internal static partial void GStrfreev(nint strv);

    // ---- 内建右键菜单的接管（webkit_web_view_execute_editing_command）----
    // 接管式**不再读改** WebKit 构造的菜单对象：那条路（get_items + remove / g_list_free）
    // 在本环境的 WebKitGTK 4.1 上会破坏菜单内部结构，连点几次右键即 double free / 段错误。
    // 现在改为自己弹一个只含剪辑项的菜单，三项走渲染引擎自己的编辑命令——它们作用于当前选区
    // 与系统剪贴板，是 document.execCommand('cut'/'paste') 做不到的。

    /// <summary>
    /// 执行一个编辑命令，取值见 <c>WEBKIT_EDITING_COMMAND_*</c>：
    /// <c>"Cut"</c> / <c>"Copy"</c> / <c>"Paste"</c> / <c>"Undo"</c> / <c>"Redo"</c> /
    /// <c>"SelectAll"</c> / <c>"Delete"</c>。
    /// </summary>
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_execute_editing_command", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebViewExecuteEditingCommand(nint webView, string command);
}

/// <summary>GDK 的矩形（<c>GdkRectangle</c>）：四个 32 位整数。</summary>
/// <remarks>与 <c>cairo_rectangle_int_t</c> 是同一个布局，GDK 直接拿它当公开类型用。</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct GdkRectangle
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
}

/// <summary>GDK 的 RGBA 颜色：四个 0..1 的 double（<c>webkit_web_view_set_background_color</c> 的参数）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GdkRGBA
{
    public double Red;
    public double Green;
    public double Blue;
    public double Alpha;
}

/// <summary>
/// <c>GdkEventConfigure</c> 的托管映射（GTK3 的 <c>gdkevents.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用结构体而不是手算偏移</b>：这个结构的字段对齐很微妙（<c>gint8 send_event</c> 之后
/// 有 3 字节 padding、<c>GdkWindow*</c> 之前有 4 字节 padding），手算偏移写错 4 字节会让
/// x 读到 y、width 读到 height 之后的数据——症状是"上报的尺寸和位置全都不对"，
/// 而那种错误看起来像是"逻辑写错了"，很难定位到偏移。交给运行时按
/// <see cref="LayoutKind.Sequential"/> + 字段类型算，与 C 编译器一致。
/// </para>
/// <para>
/// <see cref="SendEvent"/> 声明为 <c>byte</c> 而不是 <c>bool</c>：C 的 <c>gint8</c> 是 1 字节整数，
/// 而 .NET 的 <c>bool</c> 在默认 marshalling 下是 4 字节 Win32 BOOL——那会让后续字段整体偏移 3 字节。
/// 这是本结构里最容易踩的一处。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct GdkEventConfigure
{
    /// <summary><c>GdkEventType</c>（int32）。</summary>
    public int Type;

    /// <summary><c>GdkWindow*</c>。</summary>
    public nint Window;

    /// <summary><c>gint8 send_event</c>：必须用 byte，用 bool 会整体错位。</summary>
    public byte SendEvent;

    /// <summary>相对父窗口/根窗口的 X（设备像素）。</summary>
    public int X;

    /// <summary>Y（设备像素）。</summary>
    public int Y;

    /// <summary>宽度（设备像素）。</summary>
    public int Width;

    /// <summary>高度（设备像素）。</summary>
    public int Height;
}
