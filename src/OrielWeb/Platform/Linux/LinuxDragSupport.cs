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
}
