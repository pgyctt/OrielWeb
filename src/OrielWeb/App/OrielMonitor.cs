namespace OrielWeb;

/// <summary>
/// 一台显示器（对标 Tauri 2 的 <c>Monitor</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标与尺寸都是物理像素</b>，原点是**主显示器左上角**，Y 轴向下。副显示器位于主屏
/// 左侧或上方时 <see cref="Position"/> 为负值。这与 Tauri 的 <c>PhysicalPosition</c> /
/// <c>PhysicalSize</c> 同一约定。
/// </para>
/// <para>
/// <b>与窗口几何的单位不同，这是有意的</b>：<see cref="WebviewWindow"/> 的尺寸与位置一律是
/// **逻辑像素**（见 <see cref="OrielSize"/> 的说明），而显示器一律是物理像素。理由不是"对齐
/// Tauri 就照抄"，而是显示器必须跨不同缩放比的屏幕比较——只有物理像素是统一刻度；
/// 窗口则必须按 CSS 尺寸布局——只有逻辑像素是统一刻度。两者之间用
/// <see cref="ScaleFactor"/> 换算。
/// </para>
/// <para>
/// 换算示例：页面要"把窗口放到第二块屏中间"，就得先算
/// <c>monitor.Position + (monitor.Size - 窗口尺寸 × monitor.ScaleFactor) / 2</c>，
/// 再把结果除以 <see cref="ScaleFactor"/> 交给窗口 API。
/// </para>
/// </remarks>
/// <param name="Name">
/// 人类可读的名称（Windows 是设备名 <c>\\.\DISPLAY1</c>，Linux 是 GDK 的 model，
/// macOS 是本地化名称）。平台给不出时为 <c>null</c>——Tauri 的类型同样允许 null。
/// </param>
/// <param name="Position">左上角（物理像素，相对主屏左上角）。</param>
/// <param name="Size">分辨率（物理像素）。</param>
/// <param name="ScaleFactor">缩放比（1.0 = 96 DPI / 无缩放，2.0 = Retina / 200%）。</param>
public sealed record OrielMonitor(
    string? Name,
    OrielPosition Position,
    OrielSize Size,
    double ScaleFactor)
{
    /// <summary>是否是主显示器。</summary>
    /// <remarks>
    /// <para>
    /// 页面侧拿不到这个字段——Tauri 的 <c>Monitor</c> 只有
    /// <c>name</c> / <c>position</c> / <c>size</c> / <c>scaleFactor</c> 四个键，
    /// 多一个键会让"用 deepEqual 比对形状"的迁移测试无缘无故变红。
    /// 主屏要用 <see cref="OrielApp.PrimaryMonitor"/> 取。
    /// </para>
    /// <para>
    /// 宿主侧保留它是因为多屏布局的常见分支就是"这块是不是主屏"，而逐次比较
    /// <see cref="Position"/> 是否为 (0,0) 并不可靠（有的平台主屏不在原点）。
    /// </para>
    /// </remarks>
    public bool IsPrimary { get; init; }
}
