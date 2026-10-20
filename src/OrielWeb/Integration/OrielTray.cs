namespace OrielWeb;

/// <summary>
/// 一个托盘图标的句柄（对标 Tauri 2 的 <c>TrayIcon</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="OrielAppBuilder.AddTray"/> 或 <see cref="OrielApp.CreateTray"/> 创建，
/// 经 <see cref="OrielApp.Trays"/> / <see cref="OrielApp.GetTray"/> 取回。一个应用可以有多个托盘。
/// </para>
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
    private string? _title;
    private OrielImage? _icon;
    private string? _iconPath;
    private bool _iconAsTemplate;
    private bool _disposed;

    internal OrielTray(ITrayBackend backend, OrielTrayOptions options)
    {
        _backend = backend;
        _tooltip = options.Tooltip;
        _title = options.Title;
        // 取**解码后**的图标：后端已经用它建好了原生资源，句柄要如实反映"当前是哪个图标"
        _icon = options.ResolveIcon();
        _iconPath = options.IconPath;
        _iconAsTemplate = options.IconAsTemplate;

        backend.Clicked += () => Clicked?.Invoke();
        backend.EventReceived += e => TrayEvent?.Invoke(e);
        backend.MenuItemClicked += id => MenuItemClicked?.Invoke(id);
        backend.RawEvent += text => RawEvent?.Invoke(text);
    }

    /// <summary>本托盘的标识（用于 <see cref="OrielApp.GetTray"/> / <see cref="OrielApp.RemoveTray(string)"/>）。</summary>
    public string Id => _backend.Id;

    /// <summary>
    /// 是否允许**左键点击弹出菜单**（默认 false：左键只触发 <see cref="Clicked"/>，菜单走右键）。
    /// </summary>
    /// <remarks>
    /// 只有 Windows 真正支持运行期切换；Linux 与 macOS 的左键行为是平台惯例，
    /// setter 在那两个平台上抛 <see cref="NotSupportedException"/>（命令层转成 <c>not_supported</c>）。
    /// </remarks>
    public bool MenuOnClick
    {
        get => _backend.MenuOnClick;
        set => _backend.MenuOnClick = value;
    }

    /// <summary>图标被点击（Windows 左键抬起、macOS 状态项按钮点击、Linux 的 activate 信号）。</summary>
    /// <remarks>
    /// 无参数的退化形态（历史 API）。需要按键/状态/坐标时订阅 <see cref="TrayEvent"/>——
    /// 两者在同一次交互里都会触发。
    /// </remarks>
    public event Action? Clicked;

    /// <summary>
    /// 一次托盘事件（对标 Tauri 2 的 <c>TrayIconEvent</c>）：类型、按键、按键状态、坐标。
    /// </summary>
    /// <remarks>
    /// 各平台能给的粒度不同，未提供的字段为 <c>null</c>；详见 <see cref="OrielTrayEvent"/>。
    /// </remarks>
    public event Action<OrielTrayEvent>? TrayEvent;

    /// <summary>菜单项被点击；参数是该项的 <see cref="OrielMenuItem.Id"/>（没有 id 的项不上报）。</summary>
    public event Action<string>? MenuItemClicked;

    /// <summary>
    /// 托盘收到的原始平台事件（诊断用）。排查"点了图标没反应"时订阅它：
    /// 有事件说明送达正常（问题在识别或弹菜单），一个都没有说明回调通道本身有问题。
    /// </summary>
    public event Action<string>? RawEvent;

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

    /// <summary>
    /// 图标旁显示的文字（对标 Tauri 2 的 <c>setTitle</c>）。
    /// </summary>
    /// <remarks>
    /// <b>Windows 不支持（空操作）、Linux 与 macOS 显示</b>。传 <c>null</c> 表示清除。
    /// </remarks>
    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            _backend.SetTitle(value);
        }
    }

    /// <summary>
    /// 当前图标是否为"模板图"（仅 macOS 有意义，对标 Tauri 2 的 <c>setIconAsTemplate</c>）。
    /// </summary>
    /// <remarks>
    /// 置 false 用于彩色品牌图标——否则会被系统反色成单色剪影。其它平台是空操作。
    /// </remarks>
    public bool IconAsTemplate
    {
        get => _iconAsTemplate;
        set
        {
            _iconAsTemplate = value;
            _backend.SetIconAsTemplate(value);
        }
    }

    /// <summary>当前图标的路径（用 <see cref="SetIcon(string)"/> 设置过时才有值）。</summary>
    public string? IconPath => _iconPath;

    /// <summary>当前已解码的图标（用 <see cref="SetIcon(OrielImage)"/> 设置过时才有值）。</summary>
    public OrielImage? Icon => _icon;

    /// <summary>
    /// 替换图标文件（托盘常见需求：按状态换图）。传 null 表示回到平台默认。
    /// </summary>
    /// <remarks>
    /// 相对路径按可执行文件所在目录解析；文件不存在或解码失败会**抛异常**
    /// （见 <see cref="OrielTrayOptions.IconPath"/>）。要"失败就保留原图标"用
    /// <see cref="SetIcon(OrielImage)"/> 配合 <see cref="OrielImage.TryFromFile"/>。
    /// </remarks>
    public void SetIcon(string? iconPath)
    {
        if (iconPath is null)
        {
            _iconPath = null;
            _icon = null;
            _backend.SetIcon(null);
            return;
        }

        OrielImage image = OrielImage.FromFile(iconPath);
        _iconPath = OrielPathResolver.Resolve(iconPath);
        _icon = image;
        _backend.SetIcon(image);
    }

    /// <summary>
    /// 替换图标（已解码对象；对标 Tauri 2 的 <c>setIcon(Image)</c>）。传 null 表示回到平台默认。
    /// </summary>
    /// <remarks>
    /// 这是**首选入口**：解码时机与失败策略都由调用方掌握。
    /// </remarks>
    public void SetIcon(OrielImage? icon)
    {
        _icon = icon;
        _iconPath = null;
        _backend.SetIcon(icon);
    }

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
    /// 加速键的**行为**按平台不同：macOS 上由 AppKit 在菜单打开时匹配，Windows/Linux 上只作提示显示。
    /// </summary>
    public void SetMenu(IReadOnlyList<OrielMenuItem> items) => _backend.SetMenu(items);

    /// <summary>清空菜单（与 <c>SetMenu([])</c> 等价，仅表意更清楚）。</summary>
    public void ClearMenu() => _backend.SetMenu([]);

    /// <summary>显示托盘图标（默认就是显示状态；配合 <see cref="Hide"/> 做"暂时隐藏"）。</summary>
    public void Show() => _backend.Show();

    /// <summary>隐藏托盘图标（对象仍可用，<see cref="Show"/> 可再显示）。</summary>
    public void Hide() => _backend.Hide();

    /// <summary>
    /// 移除托盘图标并释放平台资源（对标 Tauri 2 的 <c>TrayIcon.close</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Hide"/> 的区别：这个是**永久移除**（原生资源被撤销），之后该句柄不可再用。
    /// 从应用的托盘表里摘除请用 <see cref="OrielApp.RemoveTray(string)"/>——它会先调本方法。
    /// </remarks>
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
