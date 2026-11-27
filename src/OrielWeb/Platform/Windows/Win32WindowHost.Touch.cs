using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 触摸 / 笔指针转发的实现部分。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这一段</b>：本库是组合宿主（<c>WS_EX_NOREDIRECTIONBITMAP</c> + DirectComposition），
/// WebView2 在页面里是视觉而不是子窗口，**收不到任何系统输入**。鼠标由
/// <see cref="ForwardMouseMessage"/> 用 <c>SendMouseInput</c> 转发；触摸则必须走
/// <c>SendPointerInput</c>，因为只有它携带"这是触摸（PT_TOUCH）"这个类型信息。
/// </para>
/// <para>
/// <b>不转发会怎样</b>：系统会把触摸"提升"成鼠标消息（WM_LBUTTONDOWN + WM_MOUSEMOVE 序列）。
/// 宿主如果照旧按鼠标转发，Chromium 看到的是"鼠标按住并拖动"——于是它去选中文本 / 拖元素，
/// <b>而不是滚动页面</b>。这就是 Win10 触摸屏上页面不能滚动的根因，
/// 而且因为没有任何报错，现场很容易误判成页面 CSS 的问题。
/// </para>
/// </remarks>
internal partial class Win32WindowHost
{
    /// <summary>缓存的 <c>ICoreWebView2Environment3</c>（触摸转发要用它的 <c>CreateCoreWebView2PointerInfo</c>）。</summary>
    private ICoreWebView2Environment3? _environment3;

    /// <summary>
    /// 是否至少成功转发过一次触摸指针。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个标志是"鼠标消息过滤"的**安全闸门**，不是性能优化。触摸会同时产生
    /// <c>WM_POINTER*</c> 和一套提升的鼠标消息；两条都送进 WebView 会让 Chromium 看到重复输入，
    /// 所以鼠标那条必须滤掉（见 <see cref="IsPromotedTouchMouseMessage"/>）。
    /// </para>
    /// <para>
    /// 但如果 pointer 这条路走不通（运行时太老、<c>WM_POINTER</c> 没到达、OLE 未就绪…），
    /// 滤掉鼠标就等于把触摸**彻底弄死**——修复前至少还能点。所以只有在"确认 pointer 转发
    /// 成功过"之后才允许过滤；没成功就保留原来的鼠标路径作为兜底，行为与 0.6.2 之前一致。
    /// </para>
    /// <para>
    /// 只增不减：一旦转发成功，本次会话就按 pointer 通道工作，不再回退。
    /// </para>
    /// </remarks>
    private bool _touchPointerForwarded;

    /// <summary>
    /// 触摸转发失败只报一次诊断。
    /// </summary>
    /// <remarks>
    /// 触摸是高频事件，逐次上报会把诊断通道刷满；而同一台机器上的失败原因不会变
    /// （运行时太老 / 环境未就绪）。报一次的用处是：真机上"触摸不滚"时，
    /// 日志里能直接看到原因，而不是只能看到一个静默不响应的界面。
    /// </remarks>
    private bool _touchForwardFailureReported;

    /// <summary>
    /// 把窗口收到的 <c>WM_POINTER*</c> 转发给组合托管的 WebView。
    /// </summary>
    /// <param name="hwnd">本窗口。</param>
    /// <param name="message">WM_POINTERUPDATE / DOWN / UP / ENTER / LEAVE。</param>
    /// <param name="wParam">低 16 位是指针 id。</param>
    /// <returns>
    /// <c>true</c> 表示已消费（WndProc 应返回 0，让系统不再把这次触摸提升成鼠标消息）；
    /// <c>false</c> 表示不是触摸或环境未就绪，交回默认处理。
    /// </returns>
    private bool ForwardPointerMessage(nint hwnd, uint message, nuint wParam)
    {
        ICoreWebView2CompositionController? controller = _compositionController?.Object;
        if (controller is null || _environment is null)
        {
            return false;
        }

        uint pointerId = (uint)(wParam & 0xFFFF);

        if (!Win32.GetPointerType(pointerId, out uint pointerType))
        {
            return false;
        }

        // 只接管触摸。鼠标（PT_MOUSE）本来就有自己的 WM_MOUSE* 通道，再按指针送一遍会重复；
        // 笔（PT_PEN）当前不转发——它需要 PenFlags/PenMask 那一组字段，且没有真实设备验证过，
        // 与其写一份没测过的实现，不如让它维持现状（笔在触摸屏上通常退化成鼠标行为）。
        if (pointerType != Win32Constants.PT_TOUCH)
        {
            return false;
        }

        if (!Win32.GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO touchInfo))
        {
            // ENTER / LEAVE 时指针还没接触，取不到 contact 信息是正常的，不算失败。
            if (message is not (Win32Constants.WM_POINTERENTER or Win32Constants.WM_POINTERLEAVE))
            {
                ReportTouchForwardFailure("GetPointerTouchInfo 失败");
            }

            return false;
        }

        // WebView2 要的是**相对 WebView 的物理坐标**，而 POINTER_TOUCH_INFO 给的是屏幕坐标。
        // 组合视觉铺满整个客户区，所以"客户区坐标"就是"WebView 坐标"。
        var origin = new POINT(0, 0);
        if (!Win32.ClientToScreen(hwnd, ref origin))
        {
            return false;
        }

        ICoreWebView2Environment3? environment3 = EnsureEnvironment3();
        if (environment3 is null)
        {
            ReportTouchForwardFailure("当前 WebView2 运行时不支持 ICoreWebView2Environment3（触摸转发需要它）");
            return false;
        }

        if (environment3.CreateCoreWebView2PointerInfo(out nint raw) < 0 || raw == 0)
        {
            ReportTouchForwardFailure("CreateCoreWebView2PointerInfo 失败");
            return false;
        }

        try
        {
            // FromPointer 自己 AddRef 一份、Dispose 时放掉；raw 这一份是 out 参数带来的，
            // 由下面 finally 里的 Release 归还。两者配对，别合并成一次。
            using IComObject<ICoreWebView2PointerInfo>? info =
                ComObject.FromPointer<ICoreWebView2PointerInfo>(raw);

            if (info is null)
            {
                return false;
            }

            ICoreWebView2PointerInfo pointerInfo = info.Object;
            POINTER_INFO source = touchInfo.pointerInfo;

            _ = pointerInfo.put_PointerKind(pointerType);
            _ = pointerInfo.put_PointerId(source.pointerId);
            _ = pointerInfo.put_FrameId(source.frameId);

            // pointerFlags 直接透传：Chromium 靠 INRANGE / INCONTACT / DOWN / UPDATE / UP
            // 这几位来区分"按下""拖动""抬起"。自己拼一份会是错的——比如漏掉 INRANGE，
            // 指针就不会被当作悬停在页面上，滚动直接不发生。
            _ = pointerInfo.put_PointerFlags(source.pointerFlags);

            _ = pointerInfo.put_Time(source.dwTime);
            _ = pointerInfo.put_HistoryCount(source.historyCount);
            _ = pointerInfo.put_InputData(source.InputData);
            _ = pointerInfo.put_KeyStates(source.dwKeyStates);
            _ = pointerInfo.put_PerformanceCount(source.PerformanceCount);
            _ = pointerInfo.put_ButtonChangeKind(source.ButtonChangeType);

            _ = pointerInfo.put_PixelLocation(new POINT(
                source.ptPixelLocation.X - origin.X,
                source.ptPixelLocation.Y - origin.Y));

            _ = pointerInfo.put_PixelLocationRaw(new POINT(
                source.ptPixelLocationRaw.X - origin.X,
                source.ptPixelLocationRaw.Y - origin.Y));

            // himetric 是设备单位，与窗口位置无关，原样透传
            _ = pointerInfo.put_HimetricLocation(source.ptHimetricLocation);
            _ = pointerInfo.put_HimetricLocationRaw(source.ptHimetricLocationRaw);

            // 触摸专属：contact 区同样从屏幕坐标换到客户区。
            // touchMask 决定哪几个字段被 Chromium 采信，所以它必须先写。
            _ = pointerInfo.put_TouchMask(touchInfo.touchMask);
            _ = pointerInfo.put_TouchFlags(touchInfo.touchFlags);

            _ = pointerInfo.put_TouchContact(new RECT
            {
                Left = touchInfo.rcContact.Left - origin.X,
                Top = touchInfo.rcContact.Top - origin.Y,
                Right = touchInfo.rcContact.Right - origin.X,
                Bottom = touchInfo.rcContact.Bottom - origin.Y,
            });

            _ = pointerInfo.put_TouchContactRaw(new RECT
            {
                Left = touchInfo.rcContactRaw.Left - origin.X,
                Top = touchInfo.rcContactRaw.Top - origin.Y,
                Right = touchInfo.rcContactRaw.Right - origin.X,
                Bottom = touchInfo.rcContactRaw.Bottom - origin.Y,
            });

            _ = pointerInfo.put_TouchOrientation(touchInfo.orientation);
            _ = pointerInfo.put_TouchPressure(touchInfo.pressure);

            // COREWEBVIEW2_POINTER_EVENT_KIND 的取值与 WM_POINTER* 的消息号一一对应
            // （UPDATE=581=0x245、DOWN=582=0x246 …），所以 message 可以直接当 eventKind。
            // WM_POINTERCAPTURECHANGED 不在那个枚举里，调用方不要传进来（见 WndProc）。
            int hr = controller.SendPointerInput((int)message, raw);
            if (hr < 0)
            {
                // 没送进去就**不能**设标志：否则下面会滤掉系统提升的鼠标消息，
                // 而 Chromium 又没收到指针事件——触摸会彻底失效（比修复前更糟）。
                ReportTouchForwardFailure($"SendPointerInput 返回 0x{hr:X8}");
                return false;
            }

            // 转发成功：从现在起允许过滤系统提升出来的鼠标消息（否则触摸会被送两遍，
            // Chromium 会当成"鼠标按住拖动"，页面不滚动）。
            _touchPointerForwarded = true;
        }
        finally
        {
            _ = Marshal.Release(raw);
        }

        return true;
    }

    /// <summary>触摸转发失败时上报一次诊断（同一窗口只报一次）。</summary>
    private void ReportTouchForwardFailure(string reason)
    {
        if (_touchForwardFailureReported)
        {
            return;
        }

        _touchForwardFailureReported = true;
        OrielDiagnostics.Report(
            $"[OrielWeb] 收到触摸输入但无法转发给 WebView：{reason}。" +
            "触摸滚动/捏合等手势将不可用（点击仍可能通过系统提升的鼠标消息工作）。");
    }

    /// <summary>惰性取 <c>ICoreWebView2Environment3</c>：它是同一 COM 对象上的更高版本接口。</summary>
    private ICoreWebView2Environment3? EnsureEnvironment3()
    {
        if (_environment3 is not null)
        {
            return _environment3;
        }

        if (_environment is null)
        {
            return null;
        }

        _environment3 = WebView2Utilities.GetInterface<ICoreWebView2Environment3>(_environment.Object);
        return _environment3;
    }

    /// <summary>销毁时丢掉缓存的接口引用，避免它比 Environment 活得更久。</summary>
    private void ReleasePointerEnvironment()
    {
        _environment3 = null;
    }
}
