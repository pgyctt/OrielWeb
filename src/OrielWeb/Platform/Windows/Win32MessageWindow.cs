using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 一个不可见的普通窗口，专供托盘与通知接收回调消息。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不复用平台的调度窗口</b>：那个窗口的消息空间是**与 WebView2 共享**的。
/// WebView2 的合成宿主自己也在用 <c>WM_APP</c> 范围的私有消息，于是我们自选的
/// "WM_APP + n" 会与它撞车——实测现象是托盘分支收到大量无关消息
/// （wParam 像指针、lParam 是 <c>WM_MOUSEMOVE</c>），而真正的托盘回调淹没在里面，
/// 表现为"点了图标没反应"。参考实现 Ryn 就是给托盘单独开一个窗口类与窗口，这里照做。
/// </para>
/// <para>
/// <b>用普通但不可见的窗口，不用 message-only 窗口</b>（<c>HWND_MESSAGE</c>）：
/// 弹出托盘菜单前必须把它设为前台窗口（<c>SetForegroundWindow</c>，否则菜单会不显示或立刻消失），
/// 而 message-only 窗口不能成为前台窗口。
/// </para>
/// </remarks>
internal sealed unsafe class Win32MessageWindow : IDisposable
{
    private static readonly Dictionary<nint, Win32MessageWindow> s_windows = [];
    private static string? s_className;
    private static nint s_classNamePtr;

    private nint _hwnd;
    private bool _disposed;

    /// <summary>窗口句柄（通知与托盘的 <c>NOTIFYICONDATA.hWnd</c> 用它）。</summary>
    internal nint Handle => _hwnd;

    /// <summary>窗口收到的任意消息（未经筛选，由订阅者按消息号分派）。</summary>
    internal event Action<uint, nuint, nint>? MessageReceived;

    internal Win32MessageWindow()
    {
        EnsureClassRegistered();

        _hwnd = Win32.CreateWindowExW(
            0,                  // 扩展样式
            s_classNamePtr,     // 窗口类名（已分配的非托管串，建窗时由系统读取）
            0,                  // 窗口标题：不需要
            0,                  // 样式：不带 WS_VISIBLE，因此不可见、也不接收用户输入
            0, 0, 0, 0,         // 位置与尺寸（不可见窗口无所谓）
            0,                  // 父窗口
            0,                  // 菜单
            Win32.GetModuleHandleW(null),
            null);              // lpParam

        if (_hwnd == 0)
        {
            throw new InvalidOperationException($"创建消息窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }

        s_windows[_hwnd] = this;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (s_windows.TryGetValue(hwnd, out Win32MessageWindow? window))
            {
                window.MessageReceived?.Invoke(message, wParam, lParam);
            }
        }
        catch
        {
            // 异常不得穿越原生边界（托管异常过界 = 进程终止）
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        if (s_className is not null)
        {
            return;
        }

        // 类名带进程 ID：窗口类在系统范围内按模块共享，重名没有意义且容易误撞
        s_className = $"OrielWebMessageWindow_{Environment.ProcessId}";
        s_classNamePtr = Marshal.StringToHGlobalUni(s_className);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WindowProc,
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = s_classNamePtr,
        };

        // 同名类已注册时返回 0；那也无妨——按名字建窗照样能成，所以不当作错误
        _ = Win32.RegisterClassExW(ref wc);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != 0)
        {
            s_windows.Remove(_hwnd);
            _ = Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
