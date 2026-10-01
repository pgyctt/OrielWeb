namespace OrielWeb;

/// <summary>
/// 注入给页面的"宿主事实"快照：页面**同步**读得到的那几个值（<c>window.oriel.system</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要同步：无边框拖动要在 <c>mousedown</c> 里立刻判断"这是不是双击的第二下"，
/// 而 <c>oriel.invoke</c> 是异步的——<c>await</c> 回来时那次按下已经过去了，判断没有任何意义。
/// 所以这几个值必须在脚本注入时就写死在脚本里（本库没有、也不打算引入同步 RPC：
/// 三平台各要一套自研的原生机制，见 DECISIONS）。
/// </para>
/// <para>
/// 取值的时刻是**建窗时**，同一文档内不再变（导航/刷新会重新注入，因此自然跟随）。
/// 窗口被拖到另一块不同缩放的屏幕上时 <see cref="Scale"/> 会过时，此时页面自己的
/// <c>window.devicePixelRatio</c> 反而更准——这些值的用途是"启动时的事实"，
/// 不是"实时状态镜像"。
/// </para>
/// </remarks>
/// <param name="DoubleClickTimeMs">系统双击间隔（毫秒）。</param>
/// <param name="Scale">窗口所在的缩放因子（1.0 = 无缩放）。</param>
internal readonly record struct OrielSystemSnapshot(int DoubleClickTimeMs, double Scale)
{
    /// <summary>取不到系统设置时用的双击间隔：与 Windows 的默认值一致（GTK 的默认是 400）。</summary>
    internal const int DefaultDoubleClickTimeMs = 500;

    /// <summary>取不到缩放时按无缩放算。</summary>
    internal const double DefaultScale = 1.0;

    /// <summary>
    /// 把平台取到的原始值规整成可注入的值。
    /// </summary>
    /// <remarks>
    /// 纯函数，好让它被逐例单测：三个平台各自的取值入口都可能返回"没读到"
    /// （Windows 句柄无效时 <c>GetDpiForWindow</c> 返回 0、GTK 没有 GtkSettings、
    /// macOS 没有显示器时 <c>mainScreen</c> 是 nil），而这些地方不该各自写一份回退逻辑——
    /// 更不该让 0 或 NaN 流进注入脚本（<c>scale: 0</c> 会让页面除零，<c>NaN</c> 则是语法错误）。
    /// </remarks>
    internal static OrielSystemSnapshot Normalize(int doubleClickTimeMs, double scale)
        => new(
            doubleClickTimeMs > 0 ? doubleClickTimeMs : DefaultDoubleClickTimeMs,
            IsUsableScale(scale) ? scale : DefaultScale);

    /// <summary>缩放必须是正数且有限：0 会让页面除零，NaN/Infinity 写进脚本是语法错误。</summary>
    private static bool IsUsableScale(double scale)
        => double.IsFinite(scale) && scale > 0;
}
