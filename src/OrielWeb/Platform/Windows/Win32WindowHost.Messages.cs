using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionDevice;

using DCompTarget = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionTarget;
using DCompVisual = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>窗口消息分发（WndProc 与 HandleMessage）。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    // ------------------------------------------------------------------
    // WndProc
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static unsafe nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            nint userData = Win32.GetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA);

            if (message == Win32Constants.WM_NCCREATE && userData == 0)
            {
                var createStruct = (CREATESTRUCTW*)lParam;
                if (createStruct->lpCreateParams != 0)
                {
                    Win32.SetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA, createStruct->lpCreateParams);
                    userData = createStruct->lpCreateParams;
                }
            }

            if (userData == 0)
            {
                return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
            }

            var host = (Win32WindowHost)GCHandle.FromIntPtr(userData).Target!;
            return host.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 用户事件处理器（Closing/Loaded/Closed 等）的异常绝不能穿越原生边界
            // （外泄 = 进程 fail-fast，不可捕获）。
            // 降级为默认处理：若异常来自 WM_CLOSE 的 Closing 回调，窗口会按默认语义销毁
            // ——即"取消关闭"的意图失效，这是"异常时无法确定用户意图"的合理降级。
            OrielDiagnostics.Report($"[OrielWeb] WindowProc 处理消息 0x{message:X} 时抛出异常：{ex}");
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private unsafe nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case Win32Constants.WM_NCCALCSIZE:
            {
                // 仅无边框窗口把客户区铺满整个窗口（见下段注释）。framed 窗口（库默认，
                // WS_OVERLAPPEDWINDOW）必须交回 DefWindowProc：不判 Frameless 会把系统标题栏与
                // 边框整个压没，且全客户区下 DefWindowProc 对 WM_NCHITTEST 只回 HTCLIENT，
                // 原生边缘 resize 一并丢失——Linux（gtk_window_set_decorated）与 macOS
                // （StyleTitled）的 framed 模式都是原生装饰，这里对齐。
                if (_decorations)
                {
                    break;
                }

                // 无边框窗口：客户区等于整个窗口，四周不留任何系统边框。
                // 之所以不再需要"让出边框换热区"（见 API.md 的历史记录）：窗口是
                // WS_EX_NOREDIRECTIONBITMAP + DirectComposition 宿主，WebView2 是合成树里的视觉
                // 而非子窗口，因此这条消息与 WM_NCHITTEST 都能真正到达本窗口，边缘命中由下面的
                // WM_NCHITTEST 分支显式给出。
                //
                // **挂了菜单栏时例外**：菜单栏画在客户区顶上，必须在这里让出它的高度，
                // 否则菜单栏会盖住页面顶部（而且 innerSize 会把菜单栏算进客户区，
                // 与"页面看到的是 webview 区域"这条契约冲突）。
                if (wParam != 0)
                {
                    // lParam 为 NCCALCSIZE_PARAMS*，其首成员已是窗口矩形；返回 0 即"客户区 = 该矩形"。
                    if (_menuBar is not null)
                    {
                        var rect = (RECT*)lParam;
                        rect->Top += Win32.GetSystemMetrics(Win32Constants.SM_CYMENU);
                    }

                    return 0;
                }

                // lParam 为 RECT*（拟议客户区，屏幕坐标）：显式改写为窗口矩形。
                if (lParam != 0
                    && Win32.GetWindowRect(hwnd, out var windowRect)
                    && windowRect.Right > windowRect.Left
                    && windowRect.Bottom > windowRect.Top)
                {
                    var rect = (RECT*)lParam;
                    *rect = windowRect;
                    if (_menuBar is not null)
                    {
                        rect->Top += Win32.GetSystemMetrics(Win32Constants.SM_CYMENU);
                    }
                }
                return 0;
            }

            case Win32Constants.WM_NCHITTEST:
            {
                // 组合宿主下 WebView 不再是子窗口，系统终于会把这条消息送到本窗口。
                // 无边框窗口的客户区铺满整个窗口，边缘的调整大小语义必须在此显式补回；
                // framed 窗口的 NC 区与命中测试由 DefWindowProc 原生处理，不进这个分支。
                if (!_decorations && _options.Resizable)
                {
                    nint hit = HitTestResizeBorder(lParam);
                    if (hit != 0)
                    {
                        return hit;
                    }
                }
                break; // 其余情况交给 DefWindowProcW
            }

            case Win32Constants.WM_SETCURSOR:
            {
                // 应用显式设了图标才接管，而且只接管**客户区**：
                // 非客户区（边框、标题栏）必须留给 DefWindowProc，否则无边框窗口边缘的
                // resize 光标会被这里统一成箭头，拖边缘时看不出能拖。
                if (_cursorIcon is { } icon
                    && (nint)(lParam & 0xFFFF) == Win32Constants.HTCLIENT)
                {
                    _ = Win32.SetCursor(CursorHandle(icon));
                    return 1; // TRUE = 已经设过光标，系统不要再改
                }

                break;
            }

            case Win32Constants.WM_MOUSEMOVE:
            case Win32Constants.WM_LBUTTONDOWN:
            case Win32Constants.WM_LBUTTONUP:
            case Win32Constants.WM_LBUTTONDBLCLK:
            case Win32Constants.WM_RBUTTONDOWN:
            case Win32Constants.WM_RBUTTONUP:
            case Win32Constants.WM_RBUTTONDBLCLK:
            case Win32Constants.WM_MBUTTONDOWN:
            case Win32Constants.WM_MBUTTONUP:
            case Win32Constants.WM_MBUTTONDBLCLK:
            case Win32Constants.WM_MOUSEWHEEL:
            case Win32Constants.WM_MOUSEHWHEEL:
                ForwardMouseMessage(hwnd, message, wParam, lParam);
                break;

            case Win32Constants.WM_MOUSELEAVE:
                // 指针移出窗口：组合托管下 WebView 不是窗口，系统不会把"离开"送给它，必须由宿主
                // 补发 LEAVE——否则页面的 :hover / tooltip 会滞留在最后一次坐标上（评审 P3，
                // 2026-10-11 复核确认代码里缺的正是这一步）。标志复位后由下一次 WM_MOUSEMOVE 重新武装。
                _trackingMouseLeave = false;
                ForwardMouseLeave();
                return 0;

            // 键盘不需要转发：组合托管的 WebView 自己处理键盘输入，宿主只需在获得焦点时
            // 把焦点交给它（见下）。这与 AOTrino 的 Composition 宿主一致——它也只转发鼠标/指针。
            case Win32Constants.WM_SETFOCUS:
                _controller?.MoveFocus((int)COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
                ReportFocus(true);
                return 0;

            // WM_KILLFOCUS **仍然不把焦点塞回组合宿主**（那会让页面的 caret/选区高亮与 IME
            // 状态错乱，见下方原说明）。但"上报失焦"与"转移焦点"是两件事：前者只是通知，
            // 不改变任何焦点归属，因此可以安全地加。Tauri 的 onFocusChanged 也是这个语义。
            case Win32Constants.WM_KILLFOCUS:
                ReportFocus(false);
                return 0;

            case Win32Constants.WM_SIZE:
                UpdateBounds();
                ReportPosition();
                return 0;

            case Win32Constants.WM_MOVE:
                // 注意：此处理论上应调用 controller.NotifyParentWindowPositionChanged()（槽 23），
                // 但实测在窗口最大化等场景该调用会触发 AV（疑似本机运行时 ComWrappers/互操作问题），
                // 且窗口位置变化由 WM_SIZE 的 put_Bounds 兜底，MVP 先跳过（见 API.md）。
                //
                // 但**上报位置**与那个调用无关，可以安全地做：页面拿不到可靠的窗口位置
                // （window.screenX 在多屏与 HiDPI 下三平台行为不一致），只能由宿主上报。
                ReportPosition();
                return 0;

            case Win32Constants.WM_GETMINMAXINFO:
            {
                var info = (MINMAXINFO*)lParam;

                // 最大化限制到显示器工作区（无边框 WS_POPUP 不会自动避开任务栏）
                var monitor = Win32.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (Win32.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    info->ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
                    info->ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
                    info->ptMaxPosition.X = monitorInfo.rcWork.Left;
                    info->ptMaxPosition.Y = monitorInfo.rcWork.Top;
                }

                // 最小尺寸同样按逻辑像素折算（与 Create/Resize 共用 LogicalPixels 这一个入口）。
                // 注意 ptMinTrackSize 是**整窗**尺寸（它约束的是 WM_GETMINMAXINFO 的跟踪尺寸），
                // 所以 framed 窗口还要把"客户区下限"换算成整窗——否则最小尺寸会退化成
                // "客户区能缩到 客户区下限 - 边框"，与另两个平台不一致。
                uint dpi = Win32.GetDpiForWindow(hwnd);
                if (_minWidth > 0)
                {
                    int minWidth = LogicalPixels.ToPhysical(_minWidth, dpi);
                    info->ptMinTrackSize.X = _options.Frameless
                        ? minWidth
                        : ClientToWindowSize(minWidth, 1, Win32Conversions.ComputeStyle(_options), dpi).Width;
                }

                if (_minHeight > 0)
                {
                    int minHeight = LogicalPixels.ToPhysical(_minHeight, dpi);
                    info->ptMinTrackSize.Y = _options.Frameless
                        ? minHeight
                        : ClientToWindowSize(1, minHeight, Win32Conversions.ComputeStyle(_options), dpi).Height;
                }

                // 最大尺寸：与 min 同一处处理，同样是**整窗**尺寸，所以 framed 窗口要先
                // 把"客户区上限"换算成整窗——否则上限会变成"客户区最多到 上限 - 边框"。
                if (_maxWidth > 0)
                {
                    int maxWidth = LogicalPixels.ToPhysical(_maxWidth, dpi);
                    info->ptMaxTrackSize.X = _options.Frameless
                        ? maxWidth
                        : ClientToWindowSize(maxWidth, 1, Win32Conversions.ComputeStyle(_options), dpi).Width;
                }

                if (_maxHeight > 0)
                {
                    int maxHeight = LogicalPixels.ToPhysical(_maxHeight, dpi);
                    info->ptMaxTrackSize.Y = _options.Frameless
                        ? maxHeight
                        : ClientToWindowSize(1, maxHeight, Win32Conversions.ComputeStyle(_options), dpi).Height;
                }

                return 0;
            }

            case Win32Constants.WM_COMMAND:
            {
                // 菜单栏的点击走这里：wParam 低字是命令 id（AppendMenuW 分配的那个），
                // lParam == 0 表示来自菜单或加速键。本库不注册加速键，所以 lParam == 0
                // 就一定是菜单点击。
                //
                // 为什么不用 WM_MENUCOMMAND：那条要菜单建成 MNS_NOTIFYBYPOS 风格
                // （传位置而不是 id），而本库的菜单构建是 id 驱动的（TrackPopupMenuEx
                // 的 TPM_RETURNCMD 也回传 id）——同一条 id 映射，两条路径共用。
                if (lParam == 0)
                {
                    OnMenuCommand((uint)(wParam & 0xFFFF));
                    return 0;
                }

                break;
            }

            case Win32Constants.WM_CLOSE:
                // WM_CLOSE 是**同步消息**，而页面回执是异步 IPC——不可能在这里等页面答复
                // （等它就是锁死 UI 线程，而回执的处理恰好也要 UI 线程）。所以判定只有三种出路：
                //   放行 → 当场销毁；宿主否决 / 页面接管 → 不销毁（见 OrielClosePipeline）。
                switch (DecideClose())
                {
                    case OrielCloseDecision.Proceed:
                        Win32.DestroyWindow(hwnd);
                        break;

                    // Cancel 与 Suspend 都只是"这次不销毁"，但原因不同（见 OrielCloseDecision）：
                    //   Cancel  = 宿主的 Closing 拒绝了
                    //   Suspend = 页面接管了，等它稍后调 win.destroy
                    default:
                        break;
                }

                return 0;

            case Win32Constants.WM_DROPFILES:
                HandleFileDrop((nint)wParam, (nint)lParam);
                return 0;

            case Win32Constants.WM_DPICHANGED:
                // **只上报，不接管**：窗口尺寸该不该按 lParam 那个建议矩形调整，
                // 留给 DefWindowProc 的默认处理（break 会落到末尾的 DefWindowProcW）。
                //
                // 本库此前没有这个分支，也就是说一直走默认路径。加分支的目的只是
                // 让页面能知道缩放变了；顺手改布局行为会引入一个我无法目视验证的变化。
                ReportScaleFactor();
                break;

            case Win32Constants.WM_DESTROY:
                OnWindowDestroyedCore();
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
