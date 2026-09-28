using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 平台后端：Win32 消息循环 + 主线程调度（消息专用窗口）+ 窗口工厂。
/// </summary>
internal sealed unsafe class WindowsPlatformBackend : IPlatformBackend
{
    private const string MessageWindowClassName = "OrielWeb_MsgWindow";

    private static int s_messageClassRegistered;
    private static readonly object s_messageClassGate = new();

    private readonly uint _uiThreadId;
    private readonly nint _messageHwnd;
    private int _aliveWindows;

    public WindowsPlatformBackend()
    {
        EnsureDpiAwareness();
        EnsureMessageWindowClass();

        _uiThreadId = Win32.GetCurrentThreadId();
        // lpParam 传 0：MessageWindowProc 只处理 WM_APP_DISPATCH（要被执行的 Action 随消息的
        // lParam 传递），从不读取创建参数或 GWLP_USERDATA，因此无需分配 GCHandle
        _messageHwnd = Win32.CreateWindowExW(
            0, RegisterClassNamePtr(MessageWindowClassName), 0, 0,
            0, 0, 0, 0,
            Win32Constants.HWND_MESSAGE, 0, Win32.GetModuleHandleW(null),
            null);
        if (_messageHwnd == 0)
        {
            throw new InvalidOperationException($"创建 OrielWeb 调度窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }

        // Win32 消息循环本身没有 SynchronizationContext：安装后，await 的续体会被 Post 回
        // UI 线程队列，这是 WebView2 生成绑定异步装配（Task 化）能正确工作的前提。
        Win32SynchronizationContext.Install(PostToMainThread);
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = Win32WindowHost.Create(window, options, app, assetDirectory, this);

        _aliveWindows++;
        return host;
    }

    public void RunMessageLoop()
    {
        while (Win32.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }
    }

    public void Quit() => Win32.PostQuitMessage(0);

    public bool IsOnUiThread() => Win32.GetCurrentThreadId() == _uiThreadId;

    public void PostToMainThread(Action action)
    {
        var handle = GCHandle.Alloc(action);
        if (!Win32.PostMessageW(_messageHwnd, Win32Constants.WM_APP_DISPATCH, 0, GCHandle.ToIntPtr(handle)))
        {
            handle.Free();
            throw new InvalidOperationException("PostToMainThread 失败：调度窗口已失效。");
        }
    }

    internal void OnWindowDestroyed()
    {
        if (--_aliveWindows <= 0)
        {
            Win32.PostQuitMessage(0);
        }
    }

    /// <summary>调度窗口随消息循环结束销毁，当前无进程级非托管资源需要释放。</summary>
    public void Dispose()
    {
    }

    // ---- 消息窗口 ----

    [UnmanagedCallersOnly]
    private static nint MessageWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == Win32Constants.WM_APP_DISPATCH)
        {
            // 先取回并释放 GCHandle，避免 Action 抛异常时句柄泄漏
            var handle = GCHandle.FromIntPtr(lParam);
            Action? action;
            try
            {
                action = (Action)handle.Target!;
            }
            finally
            {
                handle.Free();
            }

            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                // PostToMainThread 是 public API，用户 Action 的异常绝不能穿越原生边界
                // （外泄 = 进程 fail-fast，不可捕获）
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] PostToMainThread 回调抛出异常：{ex}");
            }
            return 0;
        }
        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnsureMessageWindowClass()
    {
        if (Volatile.Read(ref s_messageClassRegistered) == 1)
        {
            return;
        }

        lock (s_messageClassGate)
        {
            if (Volatile.Read(ref s_messageClassRegistered) == 1)
            {
                return;
            }

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&MessageWindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = RegisterClassNamePtr(MessageWindowClassName),
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"注册 OrielWeb 调度窗口类失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            // 注册成功后才置位（理由同 Win32WindowHost.EnsureWindowClass）
            Volatile.Write(ref s_messageClassRegistered, 1);
        }
    }

    private static void EnsureDpiAwareness()
    {
        try
        {
            _ = Win32.SetProcessDpiAwarenessContext(Win32Constants.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // 旧系统无此 API，忽略
        }
    }

    private static readonly Dictionary<string, nint> s_classNamePtrs = new(StringComparer.Ordinal);

    private static nint RegisterClassNamePtr(string className)
    {
        lock (s_classNamePtrs)
        {
            if (!s_classNamePtrs.TryGetValue(className, out var pointer))
            {
                pointer = Marshal.StringToHGlobalUni(className);
                s_classNamePtrs[className] = pointer;
            }
            return pointer;
        }
    }
}
