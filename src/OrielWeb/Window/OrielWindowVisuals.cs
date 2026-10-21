namespace OrielWeb;

/// <summary>
/// 窗口视觉材质（对标 Tauri 2 的 <c>Effect</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 取值与 Tauri 逐键一致（27 个：macOS 的 21 个 vibrancy 材质 + Windows 的 6 个
/// Mica/Acrylic/Blur/Tabbed 系）。**这个枚举本身是跨平台的**，但"哪个值在哪个平台
/// 生效"是逐平台的，见 <see cref="IWindowBackend.SetEffects"/> 的支持矩阵。
/// </para>
/// <para>
/// 与 Tauri 一致："冲突的材质取列表里第一个本平台支持的那个，其余忽略"，
/// 所以一份代码可以同时列出 Windows 与 macOS 的材质。**一个都支持不了时抛
/// <see cref="NotSupportedException"/>**，不静默 succeed（见 B22 的护栏）。
/// </para>
/// </remarks>
public enum OrielEffect
{
    /// <summary>按 appearance 选材质（macOS 10.14 起废弃）。</summary>
    AppearanceBased,
    /// <summary>浅色材质（macOS 10.14 起废弃）。</summary>
    Light,
    /// <summary>深色材质（macOS 10.14 起废弃）。</summary>
    Dark,
    /// <summary>中浅色材质（macOS 10.14 起废弃）。</summary>
    MediumLight,
    /// <summary>极深色材质（macOS 10.14 起废弃）。</summary>
    UltraDark,
    /// <summary>标题栏材质（macOS 10.10+）。</summary>
    Titlebar,
    /// <summary>选中项材质（macOS 10.10+）。</summary>
    Selection,
    /// <summary>菜单材质（macOS 10.11+）。</summary>
    Menu,
    /// <summary>浮层材质（macOS 10.11+）。</summary>
    Popover,
    /// <summary>侧栏材质（macOS 10.11+）。</summary>
    Sidebar,
    /// <summary>表头材质（macOS 10.14+）。</summary>
    HeaderView,
    /// <summary>表单材质（macOS 10.14+）。</summary>
    Sheet,
    /// <summary>窗口背景材质（macOS 10.14+）。</summary>
    WindowBackground,
    /// <summary>HUD 材质（macOS 10.14+）。</summary>
    HudWindow,
    /// <summary>全屏 UI 材质（macOS 10.14+）。</summary>
    FullScreenUi,
    /// <summary>气泡提示材质（macOS 10.14+）。</summary>
    Tooltip,
    /// <summary>内容背景材质（macOS 10.14+）。</summary>
    ContentBackground,
    /// <summary>窗口之下材质（macOS 10.14+）。</summary>
    UnderWindowBackground,
    /// <summary>页面之下材质（macOS 10.14+）。</summary>
    UnderPageBackground,
    /// <summary>液态玻璃（常规）（macOS 26+，本库不实现）。</summary>
    LiquidGlassRegular,
    /// <summary>液态玻璃（透明）（macOS 26+，本库不实现）。</summary>
    LiquidGlassClear,
    /// <summary>Mica（Windows 11）。</summary>
    Mica,
    /// <summary>模糊（Windows 7/10/11 22H1）。</summary>
    Blur,
    /// <summary>亚克力（Windows 10/11）。</summary>
    Acrylic,
    /// <summary>Tabbed，跟随系统深浅（Windows 11）。</summary>
    Tabbed,
    /// <summary>Tabbed 深色（Windows 11）。</summary>
    TabbedDark,
    /// <summary>Tabbed 浅色（Windows 11）。</summary>
    TabbedLight,
}

/// <summary>特效的激活状态（对标 Tauri 2 的 <c>EffectState</c>）。</summary>
/// <remarks>
/// <b>只在 macOS 上有意义</b>：Windows 的 DWM 材质本身就跟着窗口激活态走，
/// 没有"强制激活/非激活"的入口。传 <see cref="Active"/> / <see cref="Inactive"/>
/// 在 Windows 上不会报错，但会记一条诊断——那是"这个字段被忽略了"的信号。
/// </remarks>
public enum OrielEffectState
{
    /// <summary>跟随窗口激活态（三平台都能表达，Windows 的默认行为）。</summary>
    FollowsWindowActiveState,
    /// <summary>始终激活（仅 macOS）。</summary>
    Active,
    /// <summary>始终非激活（仅 macOS）。</summary>
    Inactive,
}

/// <summary>
/// 标题栏样式（对标 Tauri 2 的 <c>TitleBarStyle</c>）。
/// </summary>
/// <remarks>
/// <b>仅 macOS 生效</b>（Tauri 同样只有 macOS）：Windows/Linux 上抛
/// <see cref="NotSupportedException"/>。Windows 的等价能力是"无边框窗口 + 页面自绘"，
/// 本库用 <see cref="OrielWindowOptions.Frameless"/> 表达，没有运行期切换的入口。
/// </remarks>
public enum OrielTitleBarStyle
{
    /// <summary>可见的标题栏（默认）。</summary>
    Visible,
    /// <summary>标题栏透明，内容仍从标题栏下方开始。</summary>
    Transparent,
    /// <summary>标题栏透明且隐藏标题，页面延伸到标题栏下方（只留红绿灯）。</summary>
    Overlay,
}

/// <summary>任务栏 / Dock 进度条状态（对标 Tauri 2 的 <c>ProgressBarStatus</c>）。</summary>
public enum OrielProgressBarStatus
{
    /// <summary>隐藏进度条。</summary>
    None,
    /// <summary>正常状态。</summary>
    Normal,
    /// <summary>不确定状态（无百分比）。</summary>
    Indeterminate,
    /// <summary>暂停。</summary>
    Paused,
    /// <summary>出错。</summary>
    Error,
}

/// <summary>
/// 一次 <c>setEffects</c> 的完整参数（对标 Tauri 2 的 <c>Effects</c> 接口）。
/// </summary>
/// <param name="Effects">材质列表；"第一个本平台支持的生效，其余忽略"。</param>
/// <param name="State">激活状态（仅 macOS 有意义）。</param>
/// <param name="Radius">特效视图的圆角（仅 macOS；<c>null</c> = 不设）。</param>
/// <param name="ColorArgb">
/// 特效色调（<c>0xAARRGGBB</c>；仅 Windows 的 blur / acrylic 与 macOS 的液态玻璃用得上）。
/// </param>
/// <param name="Interactive">交互式玻璃（macOS 26+；本库不实现，接受但忽略）。</param>
public readonly record struct OrielEffects(
    IReadOnlyList<OrielEffect> Effects,
    OrielEffectState State,
    double? Radius,
    uint? ColorArgb,
    bool Interactive);

/// <summary>把页面传来的字符串解析成 <see cref="OrielEffect"/> 一族（逐键对应 Tauri 的取值）。</summary>
internal static class OrielEffectNames
{
    /// <summary>把 <c>Effect</c> 的字符串取值（camelCase）解析成枚举。</summary>
    internal static bool TryParseEffect(string? name, out OrielEffect effect)
    {
        switch (name)
        {
            case "appearanceBased": effect = OrielEffect.AppearanceBased; return true;
            case "light": effect = OrielEffect.Light; return true;
            case "dark": effect = OrielEffect.Dark; return true;
            case "mediumLight": effect = OrielEffect.MediumLight; return true;
            case "ultraDark": effect = OrielEffect.UltraDark; return true;
            case "titlebar": effect = OrielEffect.Titlebar; return true;
            case "selection": effect = OrielEffect.Selection; return true;
            case "menu": effect = OrielEffect.Menu; return true;
            case "popover": effect = OrielEffect.Popover; return true;
            case "sidebar": effect = OrielEffect.Sidebar; return true;
            case "headerView": effect = OrielEffect.HeaderView; return true;
            case "sheet": effect = OrielEffect.Sheet; return true;
            case "windowBackground": effect = OrielEffect.WindowBackground; return true;
            case "hudWindow": effect = OrielEffect.HudWindow; return true;
            case "fullScreenUI": effect = OrielEffect.FullScreenUi; return true;
            case "tooltip": effect = OrielEffect.Tooltip; return true;
            case "contentBackground": effect = OrielEffect.ContentBackground; return true;
            case "underWindowBackground": effect = OrielEffect.UnderWindowBackground; return true;
            case "underPageBackground": effect = OrielEffect.UnderPageBackground; return true;
            case "liquidGlassRegular": effect = OrielEffect.LiquidGlassRegular; return true;
            case "liquidGlassClear": effect = OrielEffect.LiquidGlassClear; return true;
            case "mica": effect = OrielEffect.Mica; return true;
            case "blur": effect = OrielEffect.Blur; return true;
            case "acrylic": effect = OrielEffect.Acrylic; return true;
            case "tabbed": effect = OrielEffect.Tabbed; return true;
            case "tabbedDark": effect = OrielEffect.TabbedDark; return true;
            case "tabbedLight": effect = OrielEffect.TabbedLight; return true;
            default: effect = default; return false;
        }
    }

    /// <summary>把 <c>EffectState</c> 的字符串取值解析成枚举。</summary>
    internal static bool TryParseEffectState(string? name, out OrielEffectState state)
    {
        switch (name)
        {
            case "followsWindowActiveState": state = OrielEffectState.FollowsWindowActiveState; return true;
            case "active": state = OrielEffectState.Active; return true;
            case "inactive": state = OrielEffectState.Inactive; return true;
            default: state = default; return false;
        }
    }

    /// <summary>把 <c>TitleBarStyle</c> 的字符串取值解析成枚举。</summary>
    internal static bool TryParseTitleBarStyle(string? name, out OrielTitleBarStyle style)
    {
        switch (name)
        {
            case "visible": style = OrielTitleBarStyle.Visible; return true;
            case "transparent": style = OrielTitleBarStyle.Transparent; return true;
            case "overlay": style = OrielTitleBarStyle.Overlay; return true;
            default: style = default; return false;
        }
    }

    /// <summary>把 <c>ProgressBarStatus</c> 的字符串取值解析成枚举。</summary>
    internal static bool TryParseProgressBarStatus(string? name, out OrielProgressBarStatus status)
    {
        switch (name)
        {
            case "none": status = OrielProgressBarStatus.None; return true;
            case "normal": status = OrielProgressBarStatus.Normal; return true;
            case "indeterminate": status = OrielProgressBarStatus.Indeterminate; return true;
            case "paused": status = OrielProgressBarStatus.Paused; return true;
            case "error": status = OrielProgressBarStatus.Error; return true;
            default: status = default; return false;
        }
    }
}
