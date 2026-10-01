namespace OrielWeb;

/// <summary>
/// 注入给**库里那段拖动实现**的宿主事实：系统双击间隔。
/// </summary>
/// <remarks>
/// <para>
/// 它不再出现在页面 API 上（<c>oriel.system</c> 已移除）：页面拿不到这些值，也就不存在
/// "应用依赖它们"这回事——拖动与双击由库接管（页面只需把标题栏标成拖动区域）。
/// 值以局部常量的形式写进注入脚本，只有脚本内部能用。
/// </para>
/// <para>
/// 为什么必须是"注入时就写死"：拖动要在 <c>mousedown</c> 里**同步**判断"这是不是双击的第二下"，
/// 而 <c>oriel.invoke</c> 是异步的——<c>await</c> 回来时那次按下已经过去了。
/// 本库没有、也不打算引入同步 RPC（三平台各要一套自研的原生机制）。
/// </para>
/// </remarks>
/// <param name="DoubleClickTimeMs">系统双击间隔（毫秒）。</param>
internal readonly record struct OrielSystemSnapshot(int DoubleClickTimeMs)
{
    /// <summary>取不到系统设置时用的双击间隔：与 Windows 的默认值一致（GTK 的默认是 400）。</summary>
    internal const int DefaultDoubleClickTimeMs = 500;

    /// <summary>
    /// 判定"这是拖动而不是点击"的位移阈值（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 三个平台都需要它，只是原因不同：Windows 的原生模态拖动会吞掉后续点击（按下就发起等于
    /// 让双击失效），Wayland 下把移动交给合成器会让指针被 grab（同理）。X11/macOS 本来可以立即
    /// 开始，但用同一个阈值能让三平台的手感一致——而且"几乎没动"的按下本来就不该被算成拖动。
    /// </remarks>
    internal const int DragThresholdPx = 3;

    /// <summary>把平台取到的原始值规整成可注入的值（取不到时回退到默认值）。</summary>
    /// <remarks>
    /// 纯函数，好让它被逐例单测：三个平台的取值入口都可能返回"没读到"
    /// （Windows 句柄无效、GTK 没有 GtkSettings 等），而回退只该有这一处——
    /// 否则三个平台会各自攒出一个不一样的默认值。
    /// </remarks>
    internal static OrielSystemSnapshot Normalize(int doubleClickTimeMs)
        => new(doubleClickTimeMs > 0 ? doubleClickTimeMs : DefaultDoubleClickTimeMs);
}
