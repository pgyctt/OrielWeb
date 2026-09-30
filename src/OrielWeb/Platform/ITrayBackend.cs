namespace OrielWeb;

/// <summary>
/// 托盘图标的平台后端契约（应用级；每个应用最多一个托盘）。
/// </summary>
/// <remarks>
/// 与 <see cref="IWindowBackend"/> 分开：托盘不属于任何窗口，它在应用起来之后、窗口之外存活，
/// 窗口全关也可能是"托盘继续驻留"的形态。
/// 通知**不在这里**：它是应用级概念（<see cref="OrielApp.ShowNotification(OrielNotificationOptions)"/>），
/// Windows 只是恰好用托盘气球做载体——那是实现细节，不该泄漏到公开 API 上。
/// </remarks>
internal interface ITrayBackend : IDisposable
{
    /// <summary>图标被点击/激活。</summary>
    event Action? Clicked;

    /// <summary>菜单项被点击，参数是该项的 <see cref="OrielMenuItem.Id"/>（无 id 的项不触发）。</summary>
    event Action<string>? MenuItemClicked;

    void SetTooltip(string tooltip);

    /// <summary>替换图标；null 表示回到平台默认。</summary>
    void SetIcon(string? iconPath);

    /// <summary>设置菜单；空列表表示清空。</summary>
    void SetMenu(IReadOnlyList<OrielMenuItem> items);

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
    /// 托盘是否**真的对用户可见**。语义按平台能力解释：
    /// Windows = 图标已成功加入通知区（<c>Shell_NotifyIcon</c> 成功）；macOS = 状态项已创建且未被隐藏；
    /// Linux = 图标被托盘宿主接收（<c>gtk_status_icon_is_embedded</c>——GNOME 未装扩展、Wayland 会话下为 false）。
    /// </summary>
    bool IsVisible { get; }
}
