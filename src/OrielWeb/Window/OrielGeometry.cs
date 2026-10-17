namespace OrielWeb;

/// <summary>
/// 一个尺寸（**逻辑像素**）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="OrielWindowOptions.Width"/> / <see cref="WebviewWindow.Resize"/> 同一单位：
/// 逻辑像素。库对页面**只暴露逻辑像素**，不暴露物理像素——见
/// <see cref="WebviewWindow.InnerSize"/> 的说明（那里解释了为什么与 Tauri 不同）。
/// </para>
/// <para>
/// 用 <c>double</c> 而不是 <c>int</c>：物理 → 逻辑的除法不总是整数
/// （150% 屏上 1000/1.5 = 666.67），取整会逐次累积偏差——反复
/// <c>setSize(innerSize())</c> 之后窗口会越来越小。
/// </para>
/// </remarks>
/// <param name="Width">宽度（逻辑像素）。</param>
/// <param name="Height">高度（逻辑像素）。</param>
public readonly record struct OrielSize(double Width, double Height);

/// <summary>
/// 一个位置（**逻辑像素**，**左上角原点**的屏幕坐标）。
/// </summary>
/// <remarks>
/// 原点与 y 轴方向与 <see cref="WebviewWindow.MoveTo"/> 一致（三平台统一，见 API.md §2）。
/// 副屏在主屏左侧/上方时坐标为负。
/// </remarks>
/// <param name="X">X（逻辑像素，可为负）。</param>
/// <param name="Y">Y（逻辑像素，可为负）。</param>
public readonly record struct OrielPosition(double X, double Y);
