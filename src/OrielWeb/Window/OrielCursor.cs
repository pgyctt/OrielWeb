namespace OrielWeb;

/// <summary>
/// 光标形状（对标 Tauri 2 的 <c>CursorIcon</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 取值与 CSS 的 <c>cursor</c> 关键字一一对应（Tauri 的 TS 类型就是 CSS 名的 camelCase 形式），
/// 所以页面侧传的是字符串（<c>'notAllowed'</c>、<c>'ewResize'</c>…），命令层解析成这个枚举。
/// </para>
/// <para>
/// <b>三平台各自只能给出"最接近的"形状</b>：Windows 只有十来个系统光标、macOS 的
/// <c>NSCursor</c> 也没有 <c>copy</c> / <c>zoomIn</c> 这类形状。每个平台的映射写在各自的宿主里，
/// 并在 docs/API.md 列出降级表——"设了 copy 却得到箭头"是平台事实，不是可以藏掉的实现细节。
/// </para>
/// </remarks>
public enum OrielCursorIcon
{
    Default,
    Crosshair,
    Hand,
    Arrow,
    Move,
    Text,
    Wait,
    Help,
    Progress,
    NotAllowed,
    ContextMenu,
    Cell,
    VerticalText,
    Alias,
    Copy,
    NoDrop,
    Grab,
    Grabbing,
    AllScroll,
    ZoomIn,
    ZoomOut,
    EResize,
    NResize,
    NeResize,
    NwResize,
    SResize,
    SeResize,
    SwResize,
    WResize,
    EwResize,
    NsResize,
    NeswResize,
    NwseResize,
    ColResize,
    RowResize,
}

/// <summary>
/// resize 拖动从哪条边/哪个角开始（对标 Tauri 2 的 <c>ResizeDirection</c>）。
/// </summary>
public enum OrielResizeDirection
{
    East,
    North,
    NorthEast,
    NorthWest,
    South,
    SouthEast,
    SouthWest,
    West,
}

/// <summary>
/// 光标枚举与页面字符串之间的转换（纯函数，可单测）。
/// </summary>
/// <remarks>
/// 解析用的是**显式表**而不是"把字符串转成 PascalCase 再 Enum.Parse"：
/// 后者会把 <c>'ergonomic'</c> 这类拼错当成未知值抛错（还行），但也会让
/// <c>'notallowed'</c>（全小写）这种近似拼写意外通过——而页面传错值时，
/// 报错要比"默默用了一个别的形状"有用。
/// </remarks>
internal static class OrielCursorNames
{
    /// <summary>Tauri 的 <c>CursorIcon</c> 字符串 → 枚举。未知值返回 false。</summary>
    internal static bool TryParse(string? name, out OrielCursorIcon icon)
    {
        icon = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // 大小写敏感：Tauri 的类型是字面量联合类型，拼错在那边是编译错误
        return Map.TryGetValue(name, out icon);
    }

    /// <summary>Tauri 的 <c>ResizeDirection</c> 字符串 → 枚举。未知值返回 false。</summary>
    internal static bool TryParseDirection(string? name, out OrielResizeDirection direction)
    {
        direction = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return DirectionMap.TryGetValue(name, out direction);
    }

    /// <summary>
    /// 这个形状在 Linux（GTK）上的光标名。
    /// </summary>
    /// <remarks>
    /// 直接用 CSS 名：GTK3 的 <c>gdk_cursor_new_from_name</c> 认的就是这一套
    /// （主题里没有的名字会落到 <c>default</c>）。与 Windows 不同，GTK 没有"系统光标枚举"
    /// 那条老路可走——<c>GdkCursorType</c> 只是 X 光标字体里的固定图，覆盖不到
    /// <c>copy</c> / <c>zoom-in</c> 这些现代名字。
    /// </remarks>
    internal static string ToGdkName(OrielCursorIcon icon) => icon switch
    {
        OrielCursorIcon.Default => "default",
        OrielCursorIcon.Crosshair => "crosshair",
        OrielCursorIcon.Hand => "pointer",
        OrielCursorIcon.Arrow => "default",
        OrielCursorIcon.Move => "move",
        OrielCursorIcon.Text => "text",
        OrielCursorIcon.Wait => "wait",
        OrielCursorIcon.Help => "help",
        OrielCursorIcon.Progress => "progress",
        OrielCursorIcon.NotAllowed => "not-allowed",
        OrielCursorIcon.ContextMenu => "context-menu",
        OrielCursorIcon.Cell => "cell",
        OrielCursorIcon.VerticalText => "vertical-text",
        OrielCursorIcon.Alias => "alias",
        OrielCursorIcon.Copy => "copy",
        OrielCursorIcon.NoDrop => "no-drop",
        OrielCursorIcon.Grab => "grab",
        OrielCursorIcon.Grabbing => "grabbing",
        OrielCursorIcon.AllScroll => "all-scroll",
        OrielCursorIcon.ZoomIn => "zoom-in",
        OrielCursorIcon.ZoomOut => "zoom-out",
        OrielCursorIcon.EResize => "e-resize",
        OrielCursorIcon.NResize => "n-resize",
        OrielCursorIcon.NeResize => "ne-resize",
        OrielCursorIcon.NwResize => "nw-resize",
        OrielCursorIcon.SResize => "s-resize",
        OrielCursorIcon.SeResize => "se-resize",
        OrielCursorIcon.SwResize => "sw-resize",
        OrielCursorIcon.WResize => "w-resize",
        OrielCursorIcon.EwResize => "ew-resize",
        OrielCursorIcon.NsResize => "ns-resize",
        OrielCursorIcon.NeswResize => "nesw-resize",
        OrielCursorIcon.NwseResize => "nwse-resize",
        OrielCursorIcon.ColResize => "col-resize",
        OrielCursorIcon.RowResize => "row-resize",

        // 枚举与表脱节时给一个确定的形状，而不是抛异常：
        // 光标形状错只是难看，"设个光标把应用搞崩"是另一回事
        _ => "default",
    };

    private static readonly Dictionary<string, OrielCursorIcon> Map = new(StringComparer.Ordinal)
    {
        ["default"] = OrielCursorIcon.Default,
        ["crosshair"] = OrielCursorIcon.Crosshair,
        ["hand"] = OrielCursorIcon.Hand,
        ["arrow"] = OrielCursorIcon.Arrow,
        ["move"] = OrielCursorIcon.Move,
        ["text"] = OrielCursorIcon.Text,
        ["wait"] = OrielCursorIcon.Wait,
        ["help"] = OrielCursorIcon.Help,
        ["progress"] = OrielCursorIcon.Progress,
        ["notAllowed"] = OrielCursorIcon.NotAllowed,
        ["contextMenu"] = OrielCursorIcon.ContextMenu,
        ["cell"] = OrielCursorIcon.Cell,
        ["verticalText"] = OrielCursorIcon.VerticalText,
        ["alias"] = OrielCursorIcon.Alias,
        ["copy"] = OrielCursorIcon.Copy,
        ["noDrop"] = OrielCursorIcon.NoDrop,
        ["grab"] = OrielCursorIcon.Grab,
        ["grabbing"] = OrielCursorIcon.Grabbing,
        ["allScroll"] = OrielCursorIcon.AllScroll,
        ["zoomIn"] = OrielCursorIcon.ZoomIn,
        ["zoomOut"] = OrielCursorIcon.ZoomOut,
        ["eResize"] = OrielCursorIcon.EResize,
        ["nResize"] = OrielCursorIcon.NResize,
        ["neResize"] = OrielCursorIcon.NeResize,
        ["nwResize"] = OrielCursorIcon.NwResize,
        ["sResize"] = OrielCursorIcon.SResize,
        ["seResize"] = OrielCursorIcon.SeResize,
        ["swResize"] = OrielCursorIcon.SwResize,
        ["wResize"] = OrielCursorIcon.WResize,
        ["ewResize"] = OrielCursorIcon.EwResize,
        ["nsResize"] = OrielCursorIcon.NsResize,
        ["neswResize"] = OrielCursorIcon.NeswResize,
        ["nwseResize"] = OrielCursorIcon.NwseResize,
        ["colResize"] = OrielCursorIcon.ColResize,
        ["rowResize"] = OrielCursorIcon.RowResize,
    };

    private static readonly Dictionary<string, OrielResizeDirection> DirectionMap = new(StringComparer.Ordinal)
    {
        ["East"] = OrielResizeDirection.East,
        ["North"] = OrielResizeDirection.North,
        ["NorthEast"] = OrielResizeDirection.NorthEast,
        ["NorthWest"] = OrielResizeDirection.NorthWest,
        ["South"] = OrielResizeDirection.South,
        ["SouthEast"] = OrielResizeDirection.SouthEast,
        ["SouthWest"] = OrielResizeDirection.SouthWest,
        ["West"] = OrielResizeDirection.West,
    };
}
