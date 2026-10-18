using System.Globalization;

namespace OrielWeb;

/// <summary>
/// 转发给页面的事件 payload 拼装。
/// </summary>
/// <remarks>
/// <para>
/// 共享一份而不是三平台各写一遍：这些 payload 是**页面侧的公开形状**
/// （<c>listen(TauriEvent.WINDOW_RESIZED)</c> 收到的是 <c>{ width, height }</c>），
/// 三份手写必然漂移，而漂移的症状是"某个平台上事件字段名不一样"——
/// 页面代码在开发机上好好的、到另一个平台就取不到值。
/// </para>
/// <para>
/// <b>手拼 JSON 而不是走 STJ</b>：这条路径在拖动时每秒可能走几百次，
/// 而 <c>JsonSerializer</c> 在这里要走反射（Native AOT 下直接给 IL2026/IL3050）。
/// 数值必须用 <see cref="CultureInfo.InvariantCulture"/>——逗号小数点的区域会写出非法 JSON。
/// </para>
/// </remarks>
internal static class OrielEventPayload
{
    /// <summary>尺寸事件的 payload：<c>{ "width": …, "height": … }</c>（逻辑像素的客户区）。</summary>
    internal static string Size(double width, double height)
        => $"{{\"width\":{Number(width)},\"height\":{Number(height)}}}";

    /// <summary>位置事件的 payload：<c>{ "x": …, "y": … }</c>（窗口左上角，屏幕逻辑坐标）。</summary>
    internal static string Position(double x, double y)
        => $"{{\"x\":{Number(x)},\"y\":{Number(y)}}}";

    /// <summary>
    /// 缩放变化事件的 payload：<c>{ "scaleFactor": …, "size": { "width": …, "height": … } }</c>。
    /// </summary>
    /// <remarks>
    /// 形状与 Tauri 的 <c>ScaleFactorChanged</c> 一致（那里 <c>size</c> 是 <c>PhysicalSize</c>，
    /// 所以这里的宽高也是**物理像素**）——迁移代码会照搬 <c>payload.size.width</c> 这类读法，
    /// 给它逻辑尺寸就等于悄悄换了单位。
    /// </remarks>
    internal static string ScaleFactor(double scaleFactor, double physicalWidth, double physicalHeight)
        => $"{{\"scaleFactor\":{Number(scaleFactor)},\"size\":{Size(physicalWidth, physicalHeight)}}}";

    /// <summary>
    /// 数值 → JSON 数字字面量。
    /// </summary>
    /// <remarks>
    /// NaN / Infinity 在 JSON 里不是合法字面量（<c>double.NaN.ToString()</c> 给 <c>"NaN"</c>，
    /// 页面 <c>JSON.parse</c> 会直接抛）。写 0 而不是让整条事件变成语法错误——
    /// 尺寸/位置本来也不该是 NaN，真有那一天 0 至少是可解析的。
    /// </remarks>
    internal static string Number(double value)
        => double.IsFinite(value) ? value.ToString("0.####", CultureInfo.InvariantCulture) : "0";
}
