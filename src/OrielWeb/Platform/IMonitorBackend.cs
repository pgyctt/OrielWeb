namespace OrielWeb;

/// <summary>
/// 显示器与光标的平台后端契约（OrielWeb 内部）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单开一个接口，而不是往 <see cref="IPlatformBackend"/> 里再加五个成员</b>：
/// 那个接口已经是"应用生命周期 + 窗口工厂 + 托盘工厂 + 通知 + 自启 + 剪贴板"的混合体
/// （742 行）。显示器是与它们互不相干的一整块平台知识（三套完全不同的枚举 API、
/// 三套坐标系、三套缩放语义），混进去会让每个平台后端类继续膨胀，也让"只想验显示器"的
/// 测试必须构造整个应用后端。分开之后：接口一个文件、每个平台一个实现文件、
/// 与 <see cref="ITrayBackend"/> 同一套分层方式。
/// </para>
/// <para>
/// <b>线程</b>：所有成员都只在 UI 线程调用（GTK 的 <c>gdk_display_*</c>、AppKit 的
/// <c>[NSScreen screens]</c>、Win32 的 <c>EnumDisplayMonitors</c> 都有线程亲和性）。
/// IPC 分发本身就在 UI 线程上执行命令处理函数，所以命令层不需要额外编组；
/// 宿主侧直接调用时要注意这一点（<see cref="OrielApp.InvokeOnUiThread"/>）。
/// </para>
/// </remarks>
internal interface IMonitorBackend
{
    /// <summary>枚举当前所有显示器（顺序由平台决定，不保证主屏在前）。</summary>
    IReadOnlyList<OrielMonitor> GetMonitors();

    /// <summary>主显示器；平台报不出来时返回 <c>null</c>。</summary>
    /// <remarks>
    /// 单独一条而不是让调用方从 <see cref="GetMonitors"/> 里找 <see cref="OrielMonitor.IsPrimary"/>：
    /// 平台 API 本来就是"直接给主屏"（<c>GetMonitorInfo</c> 的标志位 / <c>gdk_display_get_primary_monitor</c> /
    /// <c>CGMainDisplayID</c>），绕一圈反而多一次不一致的机会。
    /// </remarks>
    OrielMonitor? GetPrimaryMonitor();

    /// <summary>包含该点（物理像素，相对主屏左上角）的显示器；不在任何屏幕上时返回 <c>null</c>。</summary>
    OrielMonitor? MonitorFromPoint(double x, double y);

    /// <summary>窗口所在的显示器。</summary>
    /// <param name="nativeWindowHandle">
    /// 平台原生窗口句柄。**语义由本平台的实现决定**（Windows 是 <c>HWND</c>、Linux 是
    /// <c>GtkWindow*</c>、macOS 是 <c>NSWindow*</c>），因为只有本平台知道
    /// <see cref="IWindowBackend.NativeWindowHandle"/> 给的是什么。
    /// </param>
    /// <remarks>
    /// 不按"窗口中心点"近似：窗口跨屏时中心点可能落在副屏，而用户与系统都认为它属于
    /// 相交面积最大的那块（Windows 的 <c>MonitorFromWindow</c> 就是这个语义）。
    /// </remarks>
    OrielMonitor? MonitorFromWindow(nint nativeWindowHandle);

    /// <summary>光标位置（物理像素，相对主屏左上角）。</summary>
    OrielPosition GetCursorPosition();
}

/// <summary>
/// "没有显示器后端"时的空实现：应用尚未 <see cref="OrielApp.Run"/>，或测试里没装假后端。
/// </summary>
/// <remarks>
/// 返回空列表 / <c>null</c> / 原点而不是抛异常：显示器查询是**只读诊断类**调用，
/// 在窗口都没起来时问一句"有几块屏"不该炸掉调用方——Tauri 的对应命令在拿不到时
/// 也是返回 <c>null</c> / 空数组。真正会炸的是"用不存在的显示器去建窗口"，
/// 那一步在窗口 API 里另有一道检查。
/// </remarks>
internal sealed class EmptyMonitorBackend : IMonitorBackend
{
    /// <summary>进程内共享实例（无状态）。</summary>
    public static readonly EmptyMonitorBackend Instance = new();

    private EmptyMonitorBackend()
    {
    }

    public IReadOnlyList<OrielMonitor> GetMonitors() => [];

    public OrielMonitor? GetPrimaryMonitor() => null;

    public OrielMonitor? MonitorFromPoint(double x, double y) => null;

    public OrielMonitor? MonitorFromWindow(nint nativeWindowHandle) => null;

    public OrielPosition GetCursorPosition() => new(0, 0);
}
