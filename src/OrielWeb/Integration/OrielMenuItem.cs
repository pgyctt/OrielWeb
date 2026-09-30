namespace OrielWeb;

/// <summary>
/// 菜单项：托盘菜单、应用菜单、窗口上下文菜单共用同一种结构。
/// </summary>
/// <remarks>
/// 三种菜单共用一个类型而不是各写一个：它们的表达能力本来就相同（自定义项 / 平台标准项 /
/// 分隔线 / 子菜单 / 勾选 / 禁用），拆成三个类型只会把相同的字段与校验抄三遍。
/// 各平台能表达的子集不同（例如托盘菜单在 Linux 上只有一层、Windows 的 menu accelerator
/// 只显示不生效），超出平台能力的部分被**忽略**而不是抛异常——菜单是尽力而为的 UI，
/// 为"某个平台画不出来"而让整个应用起不来没有意义。
/// </remarks>
public sealed class OrielMenuItem
{
    /// <summary>
    /// 自定义项标识：点击后回传给宿主的就是它（托盘是 <c>MenuItemClicked</c>，
    /// 窗口上下文菜单是 <c>ContextMenuItemClicked</c>）。
    /// 分隔线与 role 项不需要它；自定义项不给 id 时点击不会上报。
    /// </summary>
    public string? Id { get; set; }

    /// <summary>显示文本。role 项可省略（各平台会填自己的标准文案，如 macOS 的 "Quit AppName"）。</summary>
    public string? Label { get; set; }

    /// <summary>
    /// 平台标准项，取值见 <see cref="OrielMenuRole"/>。设置后点击走平台自身行为
    /// （如 macOS 的响应链、Windows 上由库模拟的窗口/编辑动作），而不是回传 <see cref="Id"/>。
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// 加速键，如 <c>"CmdOrCtrl+Shift+A"</c>，语法见 <see cref="OrielAccelerator"/>。
    /// macOS 上是真正的快捷键（key equivalent）；Windows/Linux 上多数情形只作**显示**用
    /// （库不拦截按键，按键仍会到达页面）。解析失败时忽略该字段。
    /// </summary>
    public string? Accelerator { get; set; }

    /// <summary>是否分隔线（此时其余字段无意义）。</summary>
    public bool IsSeparator { get; set; }

    /// <summary>是否可用（false 时平台画成灰色/禁用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否勾选（视平台支持：Windows 画勾、macOS 画对号；Linux 托盘菜单忽略）。</summary>
    public bool Checked { get; set; }

    /// <summary>子菜单项；为空或空列表时本项是叶子节点。</summary>
    public IReadOnlyList<OrielMenuItem>? Items { get; set; }

    /// <summary>分隔线。</summary>
    public static OrielMenuItem Separator() => new() { IsSeparator = true };

    /// <summary>自定义点击项。</summary>
    public static OrielMenuItem Item(string id, string label) => new() { Id = id, Label = label };

    /// <summary>平台标准项（<paramref name="role"/> 取值见 <see cref="OrielMenuRole"/>）。</summary>
    public static OrielMenuItem RoleItem(string role) => new() { Role = role };
}

/// <summary>
/// 菜单 role 常量：语义由平台决定，库负责把同一组名字映射到各自的标准行为。
/// </summary>
/// <remarks>
/// 取值与命名对齐 Tauri/Ryn 的既有约定（同一套名字在三处都出现过），这样用过它们的人不必重学。
/// 各平台支持度差异见 README 的平台矩阵：<b>macOS 全支持</b>（走 responder chain，行为最标准），
/// <b>Windows 支持其中可模拟的一部分</b>（窗口类、编辑类、退出），<b>Linux 不支持应用菜单</b>。
/// 顶层菜单 role（<see cref="AppMenu"/> 等）只在 macOS 上有意义。
/// </remarks>
public static class OrielMenuRole
{
    // ---- 应用级（主要是 macOS 的固定结构）----

    /// <summary>macOS 的应用菜单（含 About/Quit 等），展开成平台标准结构。</summary>
    public const string AppMenu = "appMenu";

    /// <summary>macOS 的 Edit 菜单（撤销/剪切/拷贝…，走标准响应链）。</summary>
    public const string EditMenu = "editMenu";

    /// <summary>macOS 的 Window 菜单（最小化/缩放…）。</summary>
    public const string WindowMenu = "windowMenu";

    // ---- 单项 ----

    public const string About = "about";
    public const string Quit = "quit";
    public const string Hide = "hide";
    public const string HideOthers = "hideOthers";
    public const string ShowAll = "showAll";

    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string Cut = "cut";
    public const string Copy = "copy";
    public const string Paste = "paste";
    public const string SelectAll = "selectAll";
    public const string Delete = "delete";

    public const string Minimize = "minimize";
    public const string Zoom = "zoom";
    public const string Close = "close";
    public const string Front = "front";
    public const string ToggleFullScreen = "toggleFullScreen";
}
