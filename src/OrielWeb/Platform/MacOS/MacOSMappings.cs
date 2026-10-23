namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 库的枚举 → AppKit 常量/选择子的映射。
/// </summary>
/// <remarks>
/// <para>
/// 同一套标准与 <see cref="OrielWeb.Platform.Windows.Win32Conversions"/> 一致：
/// **不碰宿主字段、不调原生 API**，因此可以在任意平台上跑单测。
/// </para>
/// <para>
/// 这些 switch 是三个平台里最容易"改错一个分支而不报错"的地方——枚举值、选择子、键等价字符
/// 拼错都只是运行期行为不对，编译期完全看不出来。表驱动测试（每个枚举值一条用例）
/// 是唯一能挡住它的形式。
/// </para>
/// </remarks>
internal static class MacOSMappings
{
    /// <summary>把 Tauri 的 <c>Effect</c> 映射到 <c>NSVisualEffectMaterial</c>；不支持的返回 -1。</summary>
    /// <remarks>
    /// 编号来自 AppKit 的 <c>NSVisualEffectMaterial</c>（10.14 的取值集合）。前五个是 10.14
    /// 起废弃的旧材质，但仍然是合法取值——老系统上它们才是能用的那几个。
    /// </remarks>
    internal static int ToVisualEffectMaterial(OrielEffect effect) => effect switch
    {
        OrielEffect.AppearanceBased => 0,
        OrielEffect.Light => 1,
        OrielEffect.Dark => 2,
        OrielEffect.Titlebar => 3,
        OrielEffect.Selection => 4,
        OrielEffect.Menu => 5,
        OrielEffect.Popover => 6,
        OrielEffect.Sidebar => 7,
        OrielEffect.MediumLight => 8,
        OrielEffect.UltraDark => 9,
        OrielEffect.HeaderView => 10,
        OrielEffect.Sheet => 11,
        OrielEffect.WindowBackground => 12,
        OrielEffect.HudWindow => 13,
        OrielEffect.FullScreenUi => 15,
        OrielEffect.Tooltip => 17,
        OrielEffect.ContentBackground => 18,
        OrielEffect.UnderWindowBackground => 21,
        OrielEffect.UnderPageBackground => 22,

        // Windows 材质与 macOS 26 的液态玻璃：本平台没有对应物
        _ => -1,
    };

    /// <summary>枚举 → <c>NSCursor</c> 的类方法选择器（没有对应形状时用最接近的一个）。</summary>
    /// <remarks>
    /// <b>AppKit 只有 18 个公开光标</b>，<c>copy</c> / <c>zoomIn</c> / 对角线 resize 都没有对应形状。
    /// 降级表与 docs/API.md 逐条对应——改这里要同步改文档。
    /// 对角 resize 落到 <c>resizeUpDownCursor</c>/<c>resizeLeftRightCursor</c>：
    /// 这两个至少给出"能拖"的视觉暗示，而箭头什么都不给。
    /// </remarks>
    internal static string CursorSelector(OrielCursorIcon icon) => icon switch
    {
        OrielCursorIcon.Crosshair or OrielCursorIcon.Cell => "crosshairCursor",
        OrielCursorIcon.Hand => "pointingHandCursor",
        OrielCursorIcon.Move or OrielCursorIcon.AllScroll or OrielCursorIcon.Grab => "openHandCursor",
        OrielCursorIcon.Grabbing => "closedHandCursor",
        OrielCursorIcon.Text => "IBeamCursor",
        OrielCursorIcon.VerticalText => "IBeamCursorForVerticalLayout",
        OrielCursorIcon.NotAllowed or OrielCursorIcon.NoDrop => "operationNotAllowedCursor",
        OrielCursorIcon.ContextMenu => "contextualMenuCursor",
        OrielCursorIcon.Alias => "dragLinkCursor",
        OrielCursorIcon.Copy => "dragCopyCursor",
        OrielCursorIcon.EResize or OrielCursorIcon.WResize or OrielCursorIcon.EwResize
            or OrielCursorIcon.ColResize or OrielCursorIcon.NwResize or OrielCursorIcon.SeResize
            or OrielCursorIcon.NwseResize => "resizeLeftRightCursor",
        OrielCursorIcon.NResize or OrielCursorIcon.SResize or OrielCursorIcon.NsResize
            or OrielCursorIcon.RowResize or OrielCursorIcon.NeResize or OrielCursorIcon.SwResize
            or OrielCursorIcon.NeswResize => "resizeUpDownCursor",

        // default / arrow / wait / progress / zoomIn / zoomOut：AppKit 没有对应形状
        _ => "arrowCursor",
    };

    /// <summary>编辑类 role → AppKit 的标准 selector；不认识的 role 返回 null。</summary>
    internal static string? EditingSelector(string? role) => role switch
    {
        OrielMenuRole.Cut => "cut:",
        OrielMenuRole.Copy => "copy:",
        OrielMenuRole.Paste => "paste:",
        OrielMenuRole.Undo => "undo:",
        OrielMenuRole.Redo => "redo:",
        OrielMenuRole.SelectAll => "selectAll:",
        OrielMenuRole.Delete => "delete:",
        _ => null,
    };

    internal static string EditingKeyEquivalent(string? role) => role switch
    {
        OrielMenuRole.Cut => "x",
        OrielMenuRole.Copy => "c",
        OrielMenuRole.Paste => "v",
        _ => string.Empty,
    };
}
