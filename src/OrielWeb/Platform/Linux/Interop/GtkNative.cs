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

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_tooltip_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void GtkStatusIconSetTooltipText(nint statusIcon, string text);

    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_set_visible")]
    internal static partial void GtkStatusIconSetVisible(nint statusIcon, int visible);

    /// <summary>图标是否真的进了托盘区（0 = 没有宿主：纯 Wayland 会话、或未装扩展的 GNOME）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_status_icon_is_embedded")]
    internal static partial int GtkStatusIconIsEmbedded(nint statusIcon);

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

    /// <summary>在指针位置弹出菜单；triggerEvent 传 0 表示用当前指针位置（GTK 3.22+）。</summary>
    [LibraryImport(Gtk, EntryPoint = "gtk_menu_popup_at_pointer")]
    internal static partial void GtkMenuPopupAtPointer(nint menu, nint triggerEvent);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_close")]
    internal static partial void GtkWindowClose(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_maximize")]
    internal static partial void GtkWindowMaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_unmaximize")]
    internal static partial void GtkWindowUnmaximize(nint window);

    [LibraryImport(Gtk, EntryPoint = "gtk_window_is_maximized")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowIsMaximized(nint window);

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

    [LibraryImport(Gtk, EntryPoint = "gtk_window_get_position")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GtkWindowGetPosition(nint window, out int x, out int y);

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

    [LibraryImport(WebKit, EntryPoint = "webkit_javascript_result_get_js_value")]
    internal static partial nint WebkitJavascriptResultGetJsValue(nint result);

    [LibraryImport(JSC, EntryPoint = "jsc_value_to_string")]
    internal static partial nint JscValueToString(nint value);

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
