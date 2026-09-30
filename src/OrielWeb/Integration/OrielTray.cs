namespace OrielWeb;

/// <summary>托盘图标选项（应用级；每个应用最多一个托盘）。</summary>
public sealed class OrielTrayOptions
{
    /// <summary>
    /// 图标文件路径（PNG / ICO）。不设置时各平台行为不同：Windows 用应用自身的图标、
    /// macOS 显示一个占位按钮、Linux 不显示图标（只保留菜单）。
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>悬停提示文本。</summary>
    public string Tooltip { get; set; } = "OrielWeb";

    /// <summary>
    /// 是否允许点击图标直接弹出菜单。默认 false：Windows 下左键点击只触发
    /// <see cref="OrielTray.Clicked"/>，菜单走右键；置 true 后左键也弹菜单。
    /// </summary>
    public bool MenuOnClick { get; set; }
}

/// <summary>
/// 系统托盘图标（应用级，最多一个）。经 <see cref="OrielAppBuilder.AddTray"/> 启用，
/// 运行期由 <see cref="OrielApp.Tray"/> 取得。
/// </summary>
/// <remarks>
/// <para>
/// 平台事实（不是实现偷懒，见 README 平台矩阵）：<b>Linux 走 GTK3 的 <c>GtkStatusIcon</c></b>，
/// 该 API 在现代桌面上面临现实限制——GNOME Shell 默认不显示托盘（需 AppIndicator 扩展），
/// Wayland 会话下多数合成器也不显示；且其菜单只有一层（嵌套子菜单会被摊平或忽略）。
/// </para>
/// <para>
/// 所有方法都要求在 UI 线程调用；事件本身从 UI 线程发出。
/// </para>
/// </remarks>
public sealed class OrielTray : IDisposable
{
    private readonly ITrayBackend _backend;
    private string _tooltip;
    private string? _iconPath;
    private bool _disposed;

    internal OrielTray(ITrayBackend backend, OrielTrayOptions options)
    {
        _backend = backend;
        _tooltip = options.Tooltip;
        _iconPath = options.IconPath;

        backend.Clicked += () => Clicked?.Invoke();
        backend.MenuItemClicked += id => MenuItemClicked?.Invoke(id);
    }

    /// <summary>图标被点击（Windows 左键抬起、macOS 状态项按钮点击、Linux 的 activate 信号）。</summary>
    public event Action? Clicked;

    /// <summary>菜单项被点击；参数是该项的 <see cref="OrielMenuItem.Id"/>（没有 id 的项不上报）。</summary>
    public event Action<string>? MenuItemClicked;

    /// <summary>悬停提示文本（可随时更新）。</summary>
    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value;
            _backend.SetTooltip(value);
        }
    }

    /// <summary>替换图标文件（托盘常见需求：按状态换图）。传 null 表示回到平台默认。</summary>
    public void SetIcon(string? iconPath)
    {
        _iconPath = iconPath;
        _backend.SetIcon(iconPath);
    }

    /// <summary>当前图标路径（未设置时为 null）。</summary>
    public string? IconPath => _iconPath;

    /// <summary>
    /// 托盘当前是否真的对用户可见。
    /// 语义按平台能力解释：Windows = 图标已成功加入通知区、macOS = 状态项已创建且未隐藏、
    /// Linux = 图标被托盘宿主接收。
    /// </summary>
    /// <remarks>
    /// false 往往**不是代码出错**而是平台环境如此——Linux 上最常见：GNOME Shell 默认不显示托盘
    /// （需要 AppIndicator 扩展），Wayland 会话下多数合成器也不显示。用它来决定"要不要退回到
    /// 应用内提示"，而不是反复重试。
    /// </remarks>
    public bool IsVisible => _backend.IsVisible;

    /// <summary>
    /// 设置菜单。分隔线、禁用、勾选与子菜单在三平台都可用（底层分别是 Win32 弹出菜单、
    /// GtkMenu 与 NSMenu，都支持嵌套）。
    /// 加速键的**行为**按平台不同：macOS 上是真快捷键（系统拦下按键），Windows/Linux 上只作提示显示。
    /// </summary>
    public void SetMenu(IReadOnlyList<OrielMenuItem> items) => _backend.SetMenu(items);

    /// <summary>清空菜单（与 <c>SetMenu([])</c> 等价，仅表意更清楚）。</summary>
    public void ClearMenu() => _backend.SetMenu([]);

    /// <summary>显示托盘图标（默认就是显示状态；配合 <see cref="Hide"/> 做"暂时隐藏"）。</summary>
    public void Show() => _backend.Show();

    /// <summary>隐藏托盘图标（对象仍可用，<see cref="Show"/> 可再显示）。</summary>
    public void Hide() => _backend.Hide();

    /// <summary>移除托盘图标并释放平台资源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
    }
}
