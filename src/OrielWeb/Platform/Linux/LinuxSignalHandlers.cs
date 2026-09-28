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

    // ---- 主线程调度 ----

    internal static void PostToMainThread(Action action)
    {
        MainThreadQueue.Enqueue(action);
        GtkNative.GIdleAddFull(200, (delegate* unmanaged<nint, int>)&PumpIdleTrampoline, 0, 0);
    }

    // ---- 信号连接 ----

    internal static void ConnectSignals(nint gtkWindow, nint webview, nint userContentManager)
    {
        GtkNative.GSignalConnectData(gtkWindow, "delete-event",
            (delegate* unmanaged<nint, nint, nint, int>)&OnDeleteEventTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(gtkWindow, "destroy",
            (delegate* unmanaged<nint, nint, void>)&OnDestroyTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(webview, "load-changed",
            (delegate* unmanaged<nint, int, nint, void>)&OnLoadChangedTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(webview, "notify::title",
            (delegate* unmanaged<nint, nint, nint, void>)&OnTitleNotifyTrampoline, 0, 0, 0);
        GtkNative.GSignalConnectData(userContentManager, "script-message-received::oriel",
            (delegate* unmanaged<nint, nint, nint, void>)&OnScriptMessageTrampoline, 0, 0, 0);
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

    [UnmanagedCallersOnly]
    internal static void OnLoadChangedTrampoline(nint webview, int loadEvent, nint data)
    {
        try
        {
            if (WebviewStates.TryGetValue(webview, out var host) && loadEvent == 3 /*WEBKIT_LOAD_FINISHED*/)
            {
                host.OnLoadFinished();
            }
        }
        catch
        {
            // 异常不外泄
        }
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
