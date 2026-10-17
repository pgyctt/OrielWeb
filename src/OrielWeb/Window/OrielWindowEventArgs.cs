namespace OrielWeb;

/// <summary>
/// 窗口客户区尺寸变化的参数（<see cref="WebviewWindow.Resized"/>）。
/// </summary>
/// <remarks>
/// 宽高是**逻辑像素的客户区**，与 <see cref="WebviewWindow.Resize"/> 的入参同一语义
/// （见 docs/API.md 的尺寸语义说明）。用它而不是物理像素：调用方关心的是 CSS 布局尺寸，
/// 而各平台的物理像素基准不同。
/// <para>
/// <b>与页面 <c>window.resize</c> 的区别</b>：那个事件只在客户区变化时触发，
/// 且拿不到"窗口被移动到别的显示器"这类信息；本事件由宿主在原生侧产生，
/// 三平台都会到。
/// </para>
/// </remarks>
public sealed class OrielSizeChangedEventArgs(double width, double height) : EventArgs
{
    /// <summary>客户区宽度（逻辑像素）。</summary>
    public double Width { get; } = width;

    /// <summary>客户区高度（逻辑像素）。</summary>
    public double Height { get; } = height;
}

/// <summary>
/// 窗口位置变化的参数（<see cref="WebviewWindow.Moved"/>）。
/// </summary>
/// <remarks>
/// 坐标是窗口**左上角**的屏幕逻辑坐标，与 <see cref="WebviewWindow.MoveTo"/> 同一原点与单位
/// （三平台一致，见 docs/API.md）。
/// <para>
/// <b>为什么页面拿不到这个</b>：<c>window.screenX</c> / <c>screenY</c> 在多显示器与 HiDPI
/// 下的行为三平台不一致（有的给 CSS 像素、有的给设备像素、副屏还可能是负值且基准不同），
/// 所以窗口位置只能由宿主在原生侧取。
/// </para>
/// </remarks>
public sealed class OrielPositionChangedEventArgs(double x, double y) : EventArgs
{
    /// <summary>窗口左上角的屏幕 X（逻辑像素，可为负——副屏在主屏左侧时）。</summary>
    public double X { get; } = x;

    /// <summary>窗口左上角的屏幕 Y（逻辑像素，可为负——副屏在主屏上方时）。</summary>
    public double Y { get; } = y;
}
