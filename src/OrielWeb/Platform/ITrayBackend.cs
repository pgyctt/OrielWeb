namespace OrielWeb;

/// <summary>
/// 托盘图标的平台后端契约（对标 Tauri 2 的 <c>tray_icon::TrayIcon</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IWindowBackend"/> 分开：托盘不属于任何窗口，它在应用起来之后、窗口之外存活，
/// 窗口全关也可能是"托盘继续驻留"的形态。
/// 通知**不在这里**：它是应用级概念（<see cref="OrielApp.ShowNotification(OrielNotificationOptions)"/>），
/// Windows 只是恰好用托盘气球做载体——那是实现细节，不该泄漏到公开 API 上。
/// </para>
/// <para>
/// <b>一个后端实例 = 一个托盘图标</b>（Tauri 2 支持多托盘，本库对齐）。
/// 实例的生命周期由 <see cref="OrielApp"/> 的托盘注册表持有，见 <c>OrielTrayCollection</c>。
/// </para>
/// </remarks>
internal interface ITrayBackend : IDisposable
{
    /// <summary>本托盘的唯一标识（用于 <c>getById</c> / 事件回传）。</summary>
    string Id { get; }

    /// <summary>托盘图标被点击/激活（退化形态：不带按键与坐标，见 <see cref="EventReceived"/>）。</summary>
    event Action? Clicked;

    /// <summary>
    /// 一次托盘事件（富形态，对标 Tauri 2 的 <c>TrayIconEvent</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Clicked"/> 并存而不是取代它：后者是既有 API（无参数、向后兼容），
    /// 前者带按键/状态/坐标。两者在同一次交互里都会触发——宿主与页面按各自需要订阅。
    /// </remarks>
    event Action<OrielTrayEvent>? EventReceived;

    /// <summary>菜单项被点击，参数是该项的 <see cref="OrielMenuItem.Id"/>（无 id 的项不触发）。</summary>
    event Action<string>? MenuItemClicked;

    void SetTooltip(string tooltip);

    /// <summary>
    /// 替换图标（<c>null</c> 表示回到平台默认）。
    /// </summary>
    /// <remarks>
    /// 接受 <see cref="OrielImage"/> 而不是路径：解码在进入后端之前就已完成，
    /// 三平台各自落到"从 RGBA 造图标"的原生入口（见各平台的 <c>*IconFactory</c> / <c>*ImageFactory</c>）。
    /// 解码失败因此是**调用方**看得见的一次失败，而不是后端里一次静默的加载失败。
    /// </remarks>
    void SetIcon(OrielImage? icon);

    /// <summary>设置菜单；空列表表示清空。</summary>
    void SetMenu(IReadOnlyList<OrielMenuItem> items);

    /// <summary>
    /// 设置图标旁显示的文字（对标 Tauri 2 的 <c>title</c>）。
    /// </summary>
    /// <remarks>
    /// <b>Linux 显示、Windows 不支持、macOS 显示在图标旁</b>——平台事实，见 API.md。
    /// 不支持的后端是空操作（不报错：设置一个平台不显示的属性不该让应用崩）。
    /// </remarks>
    void SetTitle(string? title);

    /// <summary>
    /// 把当前图标标记为"模板图"（仅 macOS 有效）。
    /// </summary>
    /// <remarks>
    /// 模板图由系统按菜单栏深浅色自动反色，适合单色图标；彩色品牌图必须置 false，
    /// 否则会被系统反色成一片看不清的剪影（对标 Tauri 2 的 <c>icon_as_template</c>）。
    /// 其它平台是空操作。
    /// </remarks>
    void SetIconAsTemplate(bool asTemplate);

    void Show();
    void Hide();

    /// <summary>
    /// 托盘收到的**原始平台事件**（诊断用，参数已是可读文本）。
    /// </summary>
    /// <remarks>
    /// 为什么值得进公开面："点了图标却什么都没发生"是最难排查的一类问题——
    /// 可能是事件没送达、可能是送达了但没识别、也可能是识别了但菜单没弹出来。
    /// 这个事件把第一种与后两种分开。正常使用不需要订阅它。
    /// 各平台能给的粒度不同：Windows 给 wParam/lParam 与解析出的事件类型，
    /// 另两个平台目前只上报信号/selector 名。
    /// </remarks>
    event Action<string>? RawEvent;

    /// <summary>
    /// 是否允许**左键点击弹出菜单**（默认 false：左键只触发 <see cref="Clicked"/>，菜单走右键）。
    /// </summary>
    /// <remarks>
    /// <b>只有 Windows 真正支持运行期切换</b>：Linux 与 macOS 的"左键弹菜单"是平台惯例
    /// （Tauri 的 <c>showMenuOnLeftClick</c> 同标注 Linux Unsupported），那两个后端的 setter
    /// 直接抛 <see cref="NotSupportedException"/>——由命令层转成 <c>not_supported</c>，
    /// 不让"关掉了左键菜单"变成一句空话（B12 护栏：不支持 ≠ 静默忽略）。
    /// </remarks>
    bool MenuOnClick { get; set; }

    /// <summary>
    /// 托盘是否**真的对用户可见**。语义按平台能力解释：
    /// Windows = 图标已成功加入通知区（<c>Shell_NotifyIcon</c> 成功）；macOS = 状态项已创建且未被隐藏；
    /// Linux = 图标被托盘宿主接收（<c>gtk_status_icon_is_embedded</c>——GNOME 未装扩展、Wayland 会话下为 false）。
    /// </summary>
    bool IsVisible { get; }
}

/// <summary>
/// 自检探针：把一次**合成**事件从后端的派发点走一遍。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：真实的托盘点击要由系统把鼠标事件送进托盘区，**无法无头自动化**。
/// 但"接线断了"——<see cref="OrielTray"/> 构造时漏订阅某个后端事件、或订阅到错的事件——
/// 恰恰是这类代码最常见的回归形态（0.4.1 就有一次 <c>CreateTray</c> 漏接
/// <c>MenuItemClicked</c>，症状是"托盘菜单点了没反应"）。这个探针把那一类变成机器断言：
/// 它**不证明**"系统真的把点击送来了"，只证明"事件从后端发出后一路能到达公开事件"。
/// </para>
/// <para>
/// 生产代码里**没有任何调用点**，唯一的调用方是 <c>--selftest tray</c>。
/// 做成独立接口而不是往 <see cref="ITrayBackend"/> 上加成员：这是可选的测试能力，
/// 不该成为将来每个新后端都必须兑现的契约。
/// </para>
/// <para>
/// 真实的"左键单击 → <see cref="OrielTray.Clicked"/> 送达"仍只能人眼验证，
/// 已登记在 <c>docs/ROADMAP.md</c> 的 Windows 人眼项里。
/// </para>
/// </remarks>
internal interface ITrayEventProbe
{
    /// <summary>触发一次合成事件，走与真实事件相同的派发路径。</summary>
    /// <param name="kind">要触发的环节。</param>
    /// <param name="itemId">菜单项 id（仅 <see cref="TraySyntheticEvent.MenuItemClicked"/> 使用）。</param>
    void RaiseSynthetic(TraySyntheticEvent kind, string? itemId = null);
}

/// <summary>合成事件的环节（见 <see cref="ITrayEventProbe"/>）。</summary>
internal enum TraySyntheticEvent
{
    /// <summary>无参数的点击事件（<see cref="ITrayBackend.Clicked"/>）。</summary>
    Clicked,

    /// <summary>富事件（<see cref="ITrayBackend.EventReceived"/>）。</summary>
    TrayEvent,

    /// <summary>菜单项点击（<see cref="ITrayBackend.MenuItemClicked"/>）。</summary>
    MenuItemClicked,
}
