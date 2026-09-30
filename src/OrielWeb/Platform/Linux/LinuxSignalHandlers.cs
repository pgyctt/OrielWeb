using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

// ============================================================================
// GTK 信号 trampoline + 状态注册表。
// [UnmanagedCallersOnly] trampoline 由 GTK 信号系统直接调用，
// 内部分发方法查找状态并转发到 LinuxWindowHost。
// ============================================================================

internal static unsafe class LinuxSignalHandlers
{
    private static readonly Dictionary<nint, LinuxWindowHost> WindowStates = [];
    private static readonly Dictionary<nint, LinuxWindowHost> WebviewStates = [];
    private static readonly Dictionary<nint, LinuxWebMessageHandler> ManagerStates = [];
    private static readonly ConcurrentQueue<Action> MainThreadQueue = [];

    // ---- 状态注册（LinuxWindowHost.Create 调用）----

    internal static void RegisterWindow(nint window, LinuxWindowHost host) => WindowStates[window] = host;
    internal static void RegisterWebview(nint webview, LinuxWindowHost host) => WebviewStates[webview] = host;
    internal static void RegisterManager(nint manager, LinuxWebMessageHandler handler) => ManagerStates[manager] = handler;

    internal static void UnregisterWindow(nint window) => WindowStates.Remove(window);
    internal static void UnregisterWebview(nint webview) => WebviewStates.Remove(webview);
    internal static void UnregisterManager(nint manager) => ManagerStates.Remove(manager);

    // ---- 仅供测试与诊断：注册表当前条目数（真机上多窗口反复开关时可观察其归零）----

    internal static int RegisteredWindowCount => WindowStates.Count;
    internal static int RegisteredWebviewCount => WebviewStates.Count;
    internal static int RegisteredManagerCount => ManagerStates.Count;

    // ---- 主线程调度 ----

    internal static void PostToMainThread(Action action)
    {
        MainThreadQueue.Enqueue(action);
        GtkNative.GIdleAddFull(200, (delegate* unmanaged<nint, int>)&PumpIdleTrampoline, 0, 0);
    }

    // ---- 信号连接 ----

    /// <summary>GtkSettings → 后端 的映射（主题信号回调需要找回后端实例）。</summary>
    private static readonly ConcurrentDictionary<nint, LinuxPlatformBackend> ThemeBackendStates = [];

    /// <summary>
    /// 连接主题相关信号。GtkSettings 是进程级单例，所以只连一次。
    /// </summary>
    internal static void ConnectThemeSignals(nint settings, LinuxPlatformBackend backend)
    {
        ThemeBackendStates[settings] = backend;
        // 两条属性都要听：不同发行版/桌面表达深色的方式不同（见 LinuxPlatformBackend.IsDarkTheme）
        GtkNative.GSignalConnectData(settings, "notify::gtk-theme-name",
            (delegate* unmanaged<nint, nint, nint, void>)&OnThemeNotifyTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(settings, "notify::gtk-application-prefer-dark-theme",
            (delegate* unmanaged<nint, nint, nint, void>)&OnThemeNotifyTrampoline, 0, 0, 0);
    }

    [UnmanagedCallersOnly]
    internal static void OnThemeNotifyTrampoline(nint settings, nint pspec, nint data)
    {
        try
        {
            if (ThemeBackendStates.TryGetValue(settings, out var backend))
            {
                backend.RaiseThemeIfChanged();
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    internal static void ConnectSignals(nint gtkWindow, nint webview, nint userContentManager)
    {
        GtkNative.GSignalConnectData(gtkWindow, "delete-event",
            (delegate* unmanaged<nint, nint, nint, int>)&OnDeleteEventTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(gtkWindow, "destroy",
            (delegate* unmanaged<nint, nint, void>)&OnDestroyTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(gtkWindow, "window-state-event",
            (delegate* unmanaged<nint, nint, nint, int>)&OnWindowStateEventTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(webview, "load-changed",
            (delegate* unmanaged<nint, int, nint, void>)&OnLoadChangedTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(webview, "load-failed",
            (delegate* unmanaged<nint, int, nint, nint, nint, void>)&OnLoadFailedTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(webview, "notify::title",
            (delegate* unmanaged<nint, nint, nint, void>)&OnTitleNotifyTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(userContentManager, "script-message-received::oriel",
            (delegate* unmanaged<nint, nint, nint, void>)&OnScriptMessageTrampoline, 0, 0, 0);
        // 拖放载荷：只在 webview 上接（落点也设在 webview 上，见 LinuxWindowHost.Create）
        GtkNative.GSignalConnectData(webview, "drag-data-received",
            (delegate* unmanaged<nint, nint, int, int, nint, uint, uint, nint, void>)&OnDragDataReceivedTrampoline, 0, 0, 0);
    }

    // ------------------------------------------------------------------
    // [UnmanagedCallersOnly] trampolines（GTK 直接调用的入口）
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    internal static int OnDeleteEventTrampoline(nint widget, nint eventPtr, nint data)
    {
        try
        {
            if (WindowStates.TryGetValue(widget, out var host))
            {
                return host.OnWindowShouldClose() ? 0 : 1;
            }
        }
        catch
        {
            // 异常不外泄
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    internal static void OnDestroyTrampoline(nint widget, nint data)
    {
        try
        {
            if (WindowStates.Remove(widget, out var host))
            {
                host.OnWindowDestroyed();
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    // GTK3 的 "window-state-event"（GdkEventWindowState）。这里刻意不解析事件结构——布局细节随 GDK
    // 版本有异，且我们只需要"状态可能变了"这个可靠信号，权威状态一律用 gtk_window_is_maximized 读。
    // 返回 0（FALSE）= 不吞事件，GTK 自身与其它 handler 继续处理。
    [UnmanagedCallersOnly]
    internal static int OnWindowStateEventTrampoline(nint widget, nint gdkEvent, nint data)
    {
        try
        {
            if (WindowStates.TryGetValue(widget, out var host))
            {
                host.SyncMaximizedState();
            }
        }
        catch
        {
            // 异常不外泄
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    internal static void OnLoadChangedTrampoline(nint webview, int loadEvent, nint data)
    {
        try
        {
            if (!WebviewStates.TryGetValue(webview, out var host))
            {
                return;
            }

            // WEBKIT_LOAD_STARTED = 0、WEBKIT_LOAD_FINISHED = 3。中间两个事件
            // （REDIRECTED / COMMITTED）刻意不上报：门面只暴露"开始 / 完成 / 失败"三态。
            if (loadEvent == 0)
            {
                host.OnLoadStarted();
            }
            else if (loadEvent == 3)
            {
                host.OnLoadFinished();
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    /// <summary>
    /// GTK 的 <c>drag-data-received</c>。信号签名：
    /// <c>(GtkWidget*, GdkDragContext*, gint x, gint y, GtkSelectionData*, guint info, guint time, gpointer)</c>。
    /// </summary>
    /// <remarks>
    /// 无论成败都要调 <c>gtk_drag_finish</c>：不调的话源端（文件管理器）会一直等结果，
    /// 表现为"拖完卡住、源窗口不恢复"。载荷里也可能没有 uri（拖的是纯文本之类），
    /// 那时如实上报空列表、并把 success 传 false。
    /// </remarks>
    [UnmanagedCallersOnly]
    internal static void OnDragDataReceivedTrampoline(nint widget, nint context, int x, int y, nint selection, uint info, uint time, nint data)
    {
        List<string> paths = [];

        try
        {
            nint uris = GtkNative.GtkSelectionDataGetUris(selection);
            if (uris != 0)
            {
                try
                {
                    // gchar**：以 null 结尾的字符串数组，逐个取到 null 为止
                    for (nint item = uris; Marshal.ReadIntPtr(item) != 0; item += IntPtr.Size)
                    {
                        string? uri = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(item));
                        // 非 file:// 的项（http、data: 等）在这里被丢掉——拖放进来的应该是文件
                        if (OrielFileDropSupport.UriToPath(uri) is { } path)
                        {
                            paths.Add(path);
                        }
                    }
                }
                finally
                {
                    GtkNative.GStrfreev(uris);
                }
            }

            if (paths.Count > 0 && WebviewStates.TryGetValue(widget, out var host))
            {
                host.OnFilesDropped(paths);
            }
        }
        catch
        {
            // 异常不外泄
        }
        finally
        {
            GtkNative.GtkDragFinish(context, paths.Count > 0, false, time);
        }
    }

    /// <summary>
    /// WebKitGTK 的 <c>load-failed</c>（整页加载失败，如地址不可达、TLS 失败）。
    /// 信号签名：<c>(WebKitWebView*, WebKitLoadEvent, const gchar* failing_uri, GError* error, gpointer)</c>。
    /// </summary>
    [UnmanagedCallersOnly]
    internal static void OnLoadFailedTrampoline(nint webview, int loadEvent, nint failingUri, nint error, nint data)
    {
        try
        {
            if (WebviewStates.TryGetValue(webview, out var host))
            {
                host.OnLoadFailed(
                    failingUri == 0 ? string.Empty : Marshal.PtrToStringUTF8(failingUri) ?? string.Empty,
                    GErrorMessage(error));
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    /// <summary>
    /// 取 GError 的 message 字段。GError 的布局自 GLib 2.0 起未变：
    /// <c>{ GQuark domain; gint code; gchar* message; }</c>——在 64 位平台上 message 位于偏移 8。
    /// 不为此引入完整的结构体映射，只需要这一个字段。
    /// </summary>
    private static string? GErrorMessage(nint error)
    {
        if (error == 0)
        {
            return null;
        }

        nint messagePtr = Marshal.ReadIntPtr(error, 8);
        return messagePtr == 0 ? null : Marshal.PtrToStringUTF8(messagePtr);
    }

    [UnmanagedCallersOnly]
    internal static void OnTitleNotifyTrampoline(nint webview, nint pspec, nint data)
    {
        try
        {
            if (WebviewStates.TryGetValue(webview, out var host))
            {
                var titlePtr = GtkNative.WebkitWebViewGetTitle(webview);
                if (titlePtr != 0)
                {
                    var title = Marshal.PtrToStringUTF8(titlePtr);
                    if (!string.IsNullOrEmpty(title))
                    {
                        host.RaiseTitleChanged(title);
                    }
                }
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    [UnmanagedCallersOnly]
    internal static void OnScriptMessageTrampoline(nint manager, nint jsResult, nint data)
    {
        try
        {
            if (!ManagerStates.TryGetValue(manager, out var handler))
            {
                return;
            }
            var jscValue = GtkNative.WebkitJavascriptResultGetJsValue(jsResult);
            var strPtr = GtkNative.JscValueToString(jscValue);
            if (strPtr == 0)
            {
                return;
            }
            var json = Marshal.PtrToStringUTF8(strPtr);
            GtkNative.GFree(strPtr);
            if (!string.IsNullOrEmpty(json))
            {
                handler.OnScriptMessage(json);
            }
        }
        catch
        {
            // 异常不外泄
        }
    }

    [UnmanagedCallersOnly]
    internal static int PumpIdleTrampoline(nint data)
    {
        while (MainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 动作来自 public 的 OrielApp.PostToMainThread，异常不得穿越原生边界
                // （外泄 = 进程 fail-fast）；单条失败不影响后续排空
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 主线程队列动作抛出异常：{ex}");
            }
        }
        return 0; // 移除该 idle 源
    }
}
