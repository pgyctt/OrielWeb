namespace OrielWeb.Platform.Windows;

/// <summary>
/// 逻辑像素 → 物理像素的换算（Windows Per-Monitor-V2）。
/// </summary>
/// <remarks>
/// <para>
/// 库的窗口 API（<c>WithSize</c>/<c>WithMinSize</c>/<c>Resize</c>/<c>At</c>/<c>MoveTo</c>）一律收
/// **逻辑像素**，三平台同语义；而 Win32 的 <c>CreateWindowExW</c>/<c>SetWindowPos</c> 收物理像素，
/// 进程又是 PMv2 感知的（见 <c>WindowsPlatformBackend.EnsureDpiAwareness</c>），所以必须显式折算。
/// 不折算的后果是 150%/200% 屏上窗口只有一半大、位置也只落到应有落点的几分之一。
/// </para>
/// <para>
/// 抽成纯函数是为了可测：尺寸与位置两条路径共用同一个换算入口，避免"只折算尺寸、忘了位置"
/// 这类不对称（评审 2026-10-08 发现 2 就是这么来的）。四舍五入走 <see cref="Math.Round(double)"/>
/// 的默认（.5 向偶数），与迁移前的窗口尺寸实现逐位一致。
/// </para>
/// </remarks>
internal static class LogicalPixels
{
    /// <summary>逻辑像素 → 物理像素（以 96 DPI 为基准，四舍五入到整像素）。</summary>
    internal static int ToPhysical(int logical, uint dpi) => (int)Math.Round(logical * dpi / 96.0);
}
