namespace OrielWeb.Platform.Linux;

/// <summary>
/// 无边框拖动在两种 GDK 后端下的形态判定。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能被测到：真正的后端识别是一次 GDK 调用（<c>gdk_display_get_default</c>），
/// 而"这个显示名算不算 Wayland"是它可断言的那一半——后端分叉的判据本身不该只能靠真机验证。
/// </remarks>
internal static class LinuxDragSupport
{
    /// <summary>
    /// <c>gdk_display_get_name()</c> 的结果是否表示 Wayland 后端。
    /// </summary>
    /// <remarks>
    /// X11 的显示名形如 <c>:0</c> / <c>host:0</c>，Wayland 的形如 <c>wayland-0</c>
    /// （取自 <c>WAYLAND_DISPLAY</c>）。刻意只看名字而不是看环境变量：Wayland 会话里用
    /// <c>GDK_BACKEND=x11</c> 强制走 XWayland 时，X11 恰是我们要的那一侧——那时客户端
    /// 可以自己摆窗口，流式拖动有效。
    /// </remarks>
    internal static bool IsWaylandDisplay(string? displayName)
        => displayName is not null
           && displayName.StartsWith("wayland", StringComparison.Ordinal);

    /// <summary>
    /// Wayland 下"这已经算一次拖动"的位移阈值（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 为什么需要阈值：Wayland 的移动是<b>一次性交给合成器</b>（<c>xdg_toplevel.move</c>），
    /// 交出去之后指针就被合成器 grab 了，页面再也收不到后续事件——于是<b>第二次点击也被吞掉，
    /// 双击永远凑不齐，dblclick 不触发</b>。按下的瞬间就交出去，等于让"双击标题栏最大化"失效。
    /// 因此要等指针真的动了才交（此时按键仍按着，serial 依然有效）。
    /// <para>
    /// X11 不需要这个：那边是按增量自己摆窗口（<c>gtk_window_move</c>），指针没被 grab，
    /// 页面照常收事件。Windows 上也不需要——但那是页面的责任（原生模态循环会吞第二次点击，
    /// 所以示例里在页面侧就用了阈值）。
    /// </para>
    /// </remarks>
    internal const int MoveThresholdPx = 4;

    /// <summary>增量是否已经超过拖动阈值（任一轴超过即算）。</summary>
    internal static bool ExceedsMoveThreshold(double dx, double dy)
        => Math.Abs(dx) > MoveThresholdPx || Math.Abs(dy) > MoveThresholdPx;
}
