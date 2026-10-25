using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = DirectN.IDCompositionDevice;
using DCompFunctions = DirectN.Functions;
using DCompHWND = DirectN.HWND;
using DCompPoint = DirectN.POINT;
using DCompTarget = DirectN.IDCompositionTarget;
using DCompVisual = DirectN.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>尺寸、位置、DPI 换算与变化上报。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    /// <summary>
    /// 检测最大化状态变化并推送给页面。用户经原生路径最大化/还原（拖边框到屏幕顶端、
    /// 双击标题栏、Win+↑）时，页面无从得知，标题栏的"最大化/还原"图标必须靠这条通知同步。
    /// </summary>
    private void SyncMaximizedState()
    {
        bool isMaximized = Win32.IsZoomed(_hwnd);
        if (isMaximized == _wasMaximized)
        {
            return;
        }

        _wasMaximized = isMaximized;
        MaximizedChanged?.Invoke(isMaximized);
        PushEventOnUi("maximized", isMaximized ? "true" : "false");
    }

    internal void UpdateBounds()
    {
        SyncMaximizedState();

        var controller = _controller;
        if (controller is null || !Win32.GetClientRect(_hwnd, out var client))
        {
            return;
        }

        controller.Bounds = new DirectN.RECT
        {
            left = 0,
            top = 0,
            right = client.Right,
            bottom = client.Bottom,
        };

        // 客户区是**物理像素**，而上报给宿主的必须是**逻辑像素**（与 Resize 的入参同语义）。
        // 除而不是乘：GetClientRect 给的是设备像素，逻辑值 = 设备值 / 缩放比。
        double scale = CurrentScaleFactor();
        ReportSize(client.Right - client.Left, client.Bottom - client.Top, scale);
    }

    /// <summary>
    /// 当前窗口所在显示器的缩放比（1.0 = 100%）。
    /// </summary>
    /// <remarks>
    /// 用 <c>GetDpiForWindow</c> 而不是 <c>GetDpiForSystem</c>：本进程是 Per-Monitor-V2 感知的，
    /// 窗口被拖到另一块不同缩放的显示器后 DPI 会变，而系统 DPI 不变。用系统值的话，
    /// 副屏上的尺寸/位置上报会整体偏一个倍数。
    /// </remarks>
    private double CurrentScaleFactor()
    {
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// 把物理像素的客户区尺寸换成逻辑像素并上报（与上次相同则跳过）。
    /// </summary>
    /// <remarks>
    /// 去重是必须的：拖动边框时 <c>WM_SIZE</c> 每个像素来一次，每秒可达数百次。
    /// 每次都触发托管事件 + 页面 IPC 会把一次拖动变成一场流量风暴，而其中 99% 的值是重复的。
    /// </remarks>
    private void ReportSize(int physicalWidth, int physicalHeight, double scale)
    {
        double width = physicalWidth / scale;
        double height = physicalHeight / scale;

        if (_lastReportedSize is { } previous
            && Math.Abs(previous.Width - width) < 0.5
            && Math.Abs(previous.Height - height) < 0.5)
        {
            return;
        }

        _lastReportedSize = (width, height);
        Resized?.Invoke(new OrielSizeChangedEventArgs(width, height));

        // 页面侧转发是**按需**的（默认关）：拖动时这条路径每个像素走一次，
        // 而推给页面在 macOS/Linux 上是一次 JS 求值。见 OrielWindowEventKind。
        if ((EventForwarding & OrielWindowEventKind.Resize) != 0)
        {
            PushEventOnUi("resize", OrielEventPayload.Size(width, height));
        }
    }


    /// <summary>上报窗口位置（物理像素的窗口矩形 → 逻辑像素的左上角）；与上次相同则跳过。</summary>
    private void ReportPosition()
    {
        if (!Win32.GetWindowRect(_hwnd, out var window))
        {
            return;
        }

        double scale = CurrentScaleFactor();
        double x = window.Left / scale;
        double y = window.Top / scale;

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
    /// 上报缩放比变化（见 <see cref="IWindowBackend.ScaleFactorChanged"/>）；与上次相同则跳过。
    /// </summary>
    /// <remarks>
    /// 由 <c>WM_DPICHANGED</c> 调用。payload 里的 <c>size</c> 是**物理像素**客户区
    /// （与 Tauri 的 <c>PhysicalSize</c> 对齐），所以按新缩放比把逻辑客户区乘回去。
    /// </remarks>
    private void ReportScaleFactor()
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

    // ------------------------------------------------------------------
    /// <summary>
    /// 把屏幕坐标转换为窗口边缘的调整大小命中值；不在边缘时返回 0（交由默认处理）。
    /// 四角优先于四边（系统的判定顺序亦然）。
    /// </summary>
    private nint HitTestResizeBorder(nint lParam)
    {
        // 最大化 / 全屏时不允许拖动边缘改尺寸
        if (_isFullscreen || Win32.IsZoomed(_hwnd))
        {
            return 0;
        }

        // WM_NCHITTEST 的 lParam：低 16 位为 x、高 16 位为 y 的**有符号**屏幕坐标
        // （多显示器下可为负，故必须按 short 解释）
        long packed = lParam.ToInt64();
        int screenX = (short)(packed & 0xFFFF);
        int screenY = (short)((packed >> 16) & 0xFFFF);

        if (!Win32.GetWindowRect(_hwnd, out var window))
        {
            return 0;
        }

        int border = BorderThickness(horizontal: true);
        bool left = screenX < window.Left + border;
        bool right = screenX >= window.Right - border;
        bool top = screenY < window.Top + border;
        bool bottom = screenY >= window.Bottom - border;

        if (top && left) return Win32Constants.HTTOPLEFT;
        if (top && right) return Win32Constants.HTTOPRIGHT;
        if (bottom && left) return Win32Constants.HTBOTTOMLEFT;
        if (bottom && right) return Win32Constants.HTBOTTOMRIGHT;
        if (left) return Win32Constants.HTLEFT;
        if (right) return Win32Constants.HTRIGHT;
        if (top) return Win32Constants.HTTOP;
        if (bottom) return Win32Constants.HTBOTTOM;
        return 0;
    }

    /// <summary>系统窗口边框厚度（物理像素，随窗口所在显示器 DPI 缩放）。</summary>
    private int BorderThickness(bool horizontal)
    {
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        // 必须用 GetSystemMetricsForDpi：GetSystemMetrics 在此进程的 per-monitor DPI 感知下
        // 返回的是系统 DPI 的值，在副屏（不同缩放）上会明显偏小。
        int frame = Win32.GetSystemMetricsForDpi(
            horizontal ? Win32Constants.SM_CXSIZEFRAME : Win32Constants.SM_CYSIZEFRAME, dpi);
        frame += Win32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, dpi);
        return frame > 0 ? frame : 8;
    }

    /// <summary>
    /// 客户区尺寸（物理像素）→ 整窗尺寸（物理像素）。
    /// </summary>
    /// <param name="clientWidth">客户区宽（物理像素）。</param>
    /// <param name="clientHeight">客户区高（物理像素）。</param>
    /// <param name="style">窗口样式（决定边框与标题栏的组成）。</param>
    /// <param name="dpi">窗口所在显示器的 DPI。</param>
    /// <remarks>
    /// <para>
    /// 存在的唯一理由：把 <c>WithSize</c> / <c>Resize</c> 的语义统一成**客户区**。
    /// Linux 的 <c>gtk_window_resize</c> 与 macOS 的 <c>setContentSize:</c> 都直接设客户区，
    /// 只有 Win32 设整窗——不换算就是"同一份代码在 Windows 上小一圈"。
    /// </para>
    /// <para>
    /// <b>调用方必须自己排除 frameless 窗口</b>：那种窗口的 <c>WM_NCCALCSIZE</c> 让客户区
    /// 铺满整窗，客户区已等于整窗，再换算会大一圈。这里不做判断，因为样式与
    /// <see cref="OrielWindowOptions.Frameless"/> 是两处信息，放在一处判更不容易漏。
    /// </para>
    /// <para>
    /// 换算失败（<c>AdjustWindowRectExForDpi</c> 返回 false）时**退回原值**而不是抛异常：
    /// 拿不到边框尺寸时"窗口小一圈"比"应用起不来"好得多，而且这属于平台调用失败，
    /// 不该让一个纯观感问题升级成启动失败。
    /// </para>
    /// </remarks>
    private static (int Width, int Height) ClientToWindowSize(
        int clientWidth, int clientHeight, uint style, uint dpi)
    {
        if (dpi == 0)
        {
            dpi = 96;
        }

        var rect = new RECT { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
        if (!Win32.AdjustWindowRectExForDpi(ref rect, style, false, Win32Constants.WS_EX_NOREDIRECTIONBITMAP, dpi))
        {
            return (clientWidth, clientHeight);
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // 极端情况下（样式异常、DPI 巨大）可能算出非正值：退回原值，不让窗口变成 0 宽
        return width > 0 && height > 0 ? (width, height) : (clientWidth, clientHeight);
    }

    /// <summary>窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。</summary>
    public double ScaleFactor => CurrentScaleFactor();

    /// <summary>客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。</summary>
    public OrielSize InnerSize
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetClientRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielSize((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
        }
    }

    /// <summary>整窗尺寸（逻辑像素）；见 <see cref="IWindowBackend.OuterSize"/>。</summary>
    public OrielSize OuterSize
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetWindowRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielSize((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
        }
    }

    /// <summary>窗口左上角（逻辑像素）；见 <see cref="IWindowBackend.OuterPosition"/>。</summary>
    public OrielPosition OuterPosition
    {
        get
        {
            if (_hwnd == 0 || !Win32.GetWindowRect(_hwnd, out RECT rect))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielPosition(rect.Left / scale, rect.Top / scale);
        }
    }

    /// <summary>
    /// 客户区左上角（逻辑像素）；见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </summary>
    /// <remarks>
    /// 用 <c>ClientToScreen(hwnd, (0,0))</c> 而不是"外框位置 + 边框厚度"：
    /// 后者要分别猜左右边框与标题栏三处，而 Win32 直接给了精确答案。
    /// </remarks>
    public OrielPosition InnerPosition
    {
        get
        {
            if (_hwnd == 0)
            {
                return default;
            }

            var origin = new POINT(0, 0);
            if (!Win32.ClientToScreen(_hwnd, ref origin))
            {
                return default;
            }

            double scale = CurrentScaleFactor();
            return new OrielPosition(origin.X / scale, origin.Y / scale);
        }
    }

    /// <summary>设置最大尺寸（见 <see cref="IWindowBackend.SetMaxSize"/>）。</summary>
    /// <remarks>
    /// 只存字段：真正的约束在 <c>WM_GETMINMAXINFO</c> 里施加（那边本来就在处理 min）。
    /// </remarks>
    public void SetMaxSize(int width, int height)
    {
        _maxWidth = width;
        _maxHeight = height;
    }

    /// <summary>设置缩放倍率（见 <see cref="IWindowBackend.SetZoom"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 落点是 <c>ICoreWebView2Controller.ZoomFactor</c>（1.0 = 100%）。必须从 UI 线程调——
    /// 命令层本来就在 UI 线程上执行，这里不再自行派发。
    /// </para>
    /// <para>
    /// WebView2 装配完成之前（<c>_controller</c> 为 null）没有可缩放的引擎，
    /// 此时没有页面可缩放，直接跳过——与 <see cref="SetTheme"/> 对 <c>_hwnd == 0</c>
    /// 的处理同一姿态。
    /// </para>
    /// </remarks>
    public void SetZoom(double scale)
    {
        if (_controller is null)
        {
            return;
        }

        _controller.ZoomFactor = scale;
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
    }

    public void MoveTo(int x, int y)
    {
        // 窗口已销毁时直接返回：_hwnd 为 0 时 GetDpiForWindow 也给 0，
        // LogicalPixels.ToPhysical(x, 0) 会把目标坐标算成 0（DPI 无效 → 无效坐标），
        // SetWindowPos 虽然无害但语义混乱。同文件 InnerSize / CurrentScaleFactor 同款守卫。
        if (_hwnd == 0)
        {
            return;
        }

        // x/y 是屏幕逻辑坐标（API.md 的契约，三平台一致），按当前窗口 DPI 折算成物理像素——
        // 与 Resize/最小尺寸共用 LogicalPixels（评审 2026-10-08 发现 2：此前只有尺寸折算）。
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        Win32.SetWindowPos(
            _hwnd, 0,
            LogicalPixels.ToPhysical(x, dpi), LogicalPixels.ToPhysical(y, dpi),
            0, 0,
            Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Resize(int width, int height)
    {
        // 守卫理由同 MoveTo：销毁后的 Resize 只会把 0 DPI 折算出的 0x0 设给一个不存在的窗口。
        if (_hwnd == 0)
        {
            return;
        }

        // width/height 是**逻辑像素的客户区**（与 Linux gtk_window_resize、macOS setContentSize: 同语义）：
        //   1. 逻辑 → 物理（按当前窗口 DPI）；
        //   2. 客户区 → 整窗（仅 framed；frameless 的客户区已等于整窗）。
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        int physicalWidth = LogicalPixels.ToPhysical(width, dpi);
        int physicalHeight = LogicalPixels.ToPhysical(height, dpi);

        if (!_options.Frameless)
        {
            (physicalWidth, physicalHeight) = ClientToWindowSize(
                physicalWidth, physicalHeight, Win32Conversions.ComputeStyle(_options), dpi);
        }

        Win32.SetWindowPos(
            _hwnd, 0, 0, 0,
            physicalWidth, physicalHeight,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Center()
    {
        if (!Win32.GetWindowRect(_hwnd, out var windowRect))
        {
            return;
        }
        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;

        var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        int x = info.rcWork.Left + ((info.rcWork.Right - info.rcWork.Left) - width) / 2;
        int y = info.rcWork.Top + ((info.rcWork.Bottom - info.rcWork.Top) - height) / 2;
        Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }
}
