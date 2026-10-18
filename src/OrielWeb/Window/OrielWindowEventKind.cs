namespace OrielWeb;

/// <summary>
/// 哪几类宿主事件要**转发给页面**（页面用 <c>oriel.on</c> / <c>@orielweb/api/event</c> 订阅）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是显式开关而不是默认全开</b>：尺寸与位置事件在拖动时**每个像素来一次**
/// （宿主侧已按 0.5 逻辑像素去重，但一次跨屏拖动仍有上千条），
/// 而"把事件推给页面"在 macOS/Linux 上是**一次 JS 求值**——上千次求值会明显拖慢拖动。
/// 没人订阅的事件推过去就是白推，所以默认 <see cref="None"/>，
/// 由 JS 包在 <c>listen</c> 到对应事件名时按需打开（见 <c>window.setEventForwarding</c>）。
/// </para>
/// <para>
/// <c>close-requested</c> **不在这里**：它不是高频事件，而且它的推送由关闭接管开关
/// （<see cref="OrielWindowOptions.InterceptClose"/>）直接决定——挂起时必须推，
/// 不挂起时不该推，没有"订阅了才推"的中间态。
/// </para>
/// </remarks>
[Flags]
public enum OrielWindowEventKind
{
    /// <summary>不转发（默认）。</summary>
    None = 0,

    /// <summary>客户区尺寸变化（页面侧事件名 <c>resize</c>，payload <c>{ width, height }</c>）。</summary>
    Resize = 1,

    /// <summary>窗口位置变化（页面侧事件名 <c>move</c>，payload <c>{ x, y }</c>）。</summary>
    Move = 2,

    /// <summary>获得/失去焦点（页面侧事件名 <c>focus</c> / <c>blur</c>，payload 为 <c>null</c>）。</summary>
    Focus = 4,

    /// <summary>
    /// 窗口所在显示器的缩放比变化（页面侧事件名 <c>scale.changed</c>，
    /// payload <c>{ scaleFactor, size }</c>，与 Tauri 的 <c>ScaleFactorChanged</c> 同形）。
    /// </summary>
    /// <remarks>
    /// 它不是高频事件（只在窗口跨到不同 DPI 的显示器、或系统缩放设置变了时来一次），
    /// 但仍然做成"订阅了才推"：与其余事件保持同一套机制，少一个特例。
    /// </remarks>
    Scale = 8,
}
