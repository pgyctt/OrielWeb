using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>原生消息/回调分发与销毁流程。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    // ------------------------------------------------------------------
    // 生命周期回调（由 LinuxSignalHandlers 转发，UI 线程）
    // ------------------------------------------------------------------

    /// <summary>
    /// GTK <c>delete-event</c>：返回 true 表示**吞掉**该事件（窗口不销毁）。
    /// </summary>
    /// <remarks>
    /// GTK 的返回值语义与另外两个平台相反，这里是唯一的翻译点：
    /// <c>true</c> = 阻止关闭，<c>false</c> = 放行。
    /// </remarks>
    internal bool OnWindowShouldClose()
    {
        OrielCloseDecision decision = DecideClose();

        // Proceed → false（放行，GTK 继续销毁窗口）
        // Cancel / Suspend → true（吞掉事件，窗口留在打开状态）
        return decision != OrielCloseDecision.Proceed;
    }

    internal void OnWindowDestroyed()
    {
        // 先清理状态注册表与指针，再触发 Closed——避免用户在回调里发起 IPC 时
        // 走到已销毁的宿主，也避免 GTK 释放 webview 后同地址被新窗口复用造成
        // 陈旧映射（ABA）。注册表持有托管 host 的强引用，不清理则对象树永不释放。
        // 时机由 GTK 的 destroy 信号回调（OnDestroyTrampoline）保证，正是真实销毁点。
        LinuxSignalHandlers.UnregisterWebview(_webview);
        LinuxSignalHandlers.UnregisterManager(_userContentManager);
        ReleaseResizeCursors();
        _webview = 0;
        _userContentManager = 0;

        // GTK 对象多半已随 destroy 链 finalize：句柄清零后，门面迟到的调用走
        // IWindowBackend 各入口的 WindowAlive 守卫（no-op），而不是对悬空指针发消息。
        _gtkWindow = 0;

        // 保留的上下文菜单（编辑菜单）一并释放：GtkMenu._bindings 的 GCHandle 根住捕获
        // host 的闭包，不释放的话上面的注册表清理白做——整棵 host 对象树跟着菜单漏掉。
        _contextMenu?.Dispose();
        _contextMenu = null;

        // 菜单栏同理（GCHandle + 我们那一份引用）。这里**不再 gtk_container_remove**：
        // 布局盒正随窗口一起销毁，摘已经没意义；unref 是安全的——我们还持着一份引用，
        // 所以对象不会在摘之前被 finalize。
        _menuBar?.Dispose();
        _menuBar = null;
        _rootBox = 0;

        // 页面回环随文档一起消失：挂起的 ExecuteScriptAsync 在这里立刻失败，
        // 而不是让调用方的 await 永不返回。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：窗口已销毁，页面回环不会再返回结果。");

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }

    /// <summary>
    /// 判定一次关闭请求（宿主否决 → 页面接管 → 放行）。
    /// </summary>
    /// <remarks>
    /// "页面可决定"要求页面已开启接管**且已加载完成**：那条 <c>close-requested</c>
    /// 送不到未加载的页面，挂起只会让窗口变成点 X 没反应。
    /// </remarks>
    internal OrielCloseDecision DecideClose()
    {
        // 强制销毁途中不再问任何人：Destroy() 之后的任何关闭请求都直接放行，
        // 否则"页面接管"会把强制销毁也拦下来（那与 Destroy 的语义直接冲突）。
        if (_forceDestroy)
        {
            return OrielCloseDecision.Proceed;
        }

        return OrielClosePipeline.Decide(
            Closing,
            pageCanDecide: CloseInterceptEnabled && _pageReady,
            emitCloseRequested: () => PushEventOnUi(OrielClosePipeline.CloseRequestedEventName, "null"));
    }

    // ------------------------------------------------------------------
    // 尺寸 / 位置 / 焦点上报（对标 Tauri 2 的 onResized / onMoved / onFocusChanged）
    // ------------------------------------------------------------------

    /// <summary>
    /// GTK 的 <c>configure-event</c>：尺寸或位置变了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一个信号覆盖两件事</b>：GTK3 的 <c>configure-event</c> 在窗口被移动**或**调整大小时都会发，
    /// 所以尺寸与位置都在这里上报。这也是 GTK 上唯一可靠的双向来源——
    /// <c>size-allocate</c> 只管尺寸（且合成器发起的移动不触发），
    /// 而 <c>gtk_window_get_position</c> 在 Wayland 下拿不到真实位置。
    /// </para>
    /// <para>
    /// 从 <c>GdkEventConfigure</c> 的字段直接读，而不是回头调 <c>gtk_window_get_size</c>：
    /// 后者在 Wayland 下可能给不出合成器侧的真实尺寸（窗口被平铺、被缩放时）。
    /// GdkEventConfigure 的结构是 { GdkEventType type; GdkWindow* window; gint8 send_event;
    /// guint32 x, y, width, height; } —— 前 4 个字段固定，之后是三个 32 位整数。
    /// </para>
    /// <para>
    /// 坐标单位是**设备像素**（GDK 在 HiDPI 下不折算），所以除以缩放比换成逻辑像素，
    /// 与 <see cref="MoveTo"/> / <see cref="Resize"/> 的入参语义对齐。
    /// </para>
    /// </remarks>
    internal void OnConfigureEvent(nint gdkEvent)
    {
        if (gdkEvent == 0)
        {
            return;
        }

        // 用结构体映射而不是手算偏移：GdkEventConfigure 的字段对齐很微妙
        // （gint8 send_event 后有 3 字节 padding、GdkWindow* 前有 4 字节 padding），
        // 手算写错 4 字节会让 x 读到 y、width 读到 height 之后的数据——症状是
        // "上报的尺寸和位置全都不对"，而那种错误看起来像是逻辑问题，极难定位到偏移。
        // 结构定义与踩坑说明见 GtkNative.GdkEventConfigure。
        GdkEventConfigure configure = Marshal.PtrToStructure<GdkEventConfigure>(gdkEvent);

        // 尺寸为 0 是 GTK 在 realize/unrealize 之间的中间态，不是真实尺寸
        if (configure.Width <= 0 || configure.Height <= 0)
        {
            return;
        }

        double scale = CurrentScaleFactor();
        ReportSize(configure.Width / scale, configure.Height / scale);
        ReportPosition(configure.X / scale, configure.Y / scale);
    }

    /// <summary>GTK 的 <c>focus-in-event</c> / <c>focus-out-event</c>。</summary>
    internal void OnFocusChanged(bool focused) => ReportFocus(focused);
}
