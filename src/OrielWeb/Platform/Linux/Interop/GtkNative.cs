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

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_load_uri", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebViewLoadUri(nint webview, string uri);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_title")]
    internal static partial nint WebkitWebViewGetTitle(nint webview);

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_evaluate_javascript", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WebkitWebViewEvaluateJavaScript(nint webview, string script, nint length, nint worldName, nint sourceUri, nint cancellable, nint callback, nint userData);

    [LibraryImport(WebKit, EntryPoint = "webkit_javascript_result_get_js_value")]
    internal static partial nint WebkitJavascriptResultGetJsValue(nint result);

    [LibraryImport(JSC, EntryPoint = "jsc_value_to_string")]
    internal static partial nint JscValueToString(nint value);
}
