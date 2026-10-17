namespace OrielWeb;

/// <summary>托盘图标事件的类型（对标 Tauri 2 的 <c>TrayIconEventType</c>）。</summary>
/// <remarks>
/// <b>Linux 不支持其中任何一个</b>：GTK 的 <c>GtkStatusIcon</c> 只发 <c>activate</c> 与
/// <c>popup-menu</c> 两个信号，拿不到"进入/移动/离开"与按键状态。这是平台事实而非漏实现
/// （Tauri 的文档同样标注 "Linux: Unsupported"）。Linux 上能拿到的只有
/// <see cref="OrielTrayEventType.Click"/> 的退化形态（无按键信息，见 <see cref="OrielTrayEvent"/>）。
/// </remarks>
public enum OrielTrayEventType
{
    /// <summary>鼠标按键在图标上按下或抬起。</summary>
    Click,

    /// <summary>图标被双击。</summary>
    DoubleClick,

    /// <summary>指针进入图标区域。</summary>
    Enter,

    /// <summary>指针在图标区域内移动。</summary>
    Move,

    /// <summary>指针离开图标区域。</summary>
    Leave,
}

/// <summary>触发了托盘事件的鼠标按键。</summary>
public enum OrielTrayMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>鼠标按键是按下还是抬起。</summary>
public enum OrielTrayButtonState
{
    Down,
    Up,
}

/// <summary>屏幕坐标（物理像素）。</summary>
public readonly record struct OrielTrayPoint(double X, double Y);

/// <summary>矩形：位置 + 尺寸（物理像素）。</summary>
public readonly record struct OrielTrayRect(OrielTrayPoint Position, double Width, double Height);

/// <summary>
/// 一次托盘图标事件（对标 Tauri 2 的 <c>TrayIconEvent</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与旧的 <c>OrielTray.Clicked</c>（无参数）相比，这里带上了按键、状态与位置——页面/宿主
/// 据此才能区分"左键点"与"右键点"、知道菜单该往哪儿弹。
/// </para>
/// <para>
/// <b>各平台能给的粒度不同</b>，未提供的字段为 <c>null</c> 而不是编造一个值：
/// <list type="bullet">
///   <item><description><b>Windows</b>：能给全（按键、状态、坐标；<c>rect</c> 需额外查询通知区位置，当前不提供）。</description></item>
///   <item><description><b>macOS</b>：只给 Click（<c>NSStatusItem</c> 的按钮 action 不带按键信息）；
///     Enter/Move/Leave 与按键状态取不到。</description></item>
///   <item><description><b>Linux</b>：只给 Click（<c>activate</c> 信号），且按键未知。</description></item>
/// </list>
/// 这条差异是平台事实，写进 API.md 与 README 的平台矩阵，不假装能用。
/// </para>
/// </remarks>
public sealed class OrielTrayEvent
{
    internal OrielTrayEvent(
        string trayId,
        OrielTrayEventType type,
        OrielTrayMouseButton? button = null,
        OrielTrayButtonState? buttonState = null,
        OrielTrayPoint? position = null,
        OrielTrayRect? rect = null)
    {
        TrayId = trayId;
        Type = type;
        Button = button;
        ButtonState = buttonState;
        Position = position;
        Rect = rect;
    }

    /// <summary>触发本次事件的托盘 id。</summary>
    public string TrayId { get; }

    /// <summary>事件类型。</summary>
    public OrielTrayEventType Type { get; }

    /// <summary>触发的按键；平台未提供时为 <c>null</c>。</summary>
    public OrielTrayMouseButton? Button { get; }

    /// <summary>按键状态；平台未提供时为 <c>null</c>。</summary>
    public OrielTrayButtonState? ButtonState { get; }

    /// <summary>事件的屏幕坐标（物理像素）；平台未提供时为 <c>null</c>。</summary>
    public OrielTrayPoint? Position { get; }

    /// <summary>图标的位置与尺寸；平台未提供时为 <c>null</c>。</summary>
    public OrielTrayRect? Rect { get; }

    /// <inheritdoc />
    public override string ToString()
        => $"{Type}{(Button is { } b ? $" {b}" : string.Empty)}" +
           $"{(ButtonState is { } s ? $" {s}" : string.Empty)}" +
           $"{(Position is { } p ? $" @({p.X},{p.Y})" : string.Empty)}";
}
