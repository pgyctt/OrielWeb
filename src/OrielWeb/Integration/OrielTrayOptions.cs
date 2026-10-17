namespace OrielWeb;

/// <summary>
/// 托盘图标的创建选项（对标 Tauri 2 的 <c>TrayIconOptions</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 每个 <see cref="OrielApp"/> 可以有**多个**托盘图标，各自用 <see cref="Id"/> 区分
/// （Tauri 2 同样支持多托盘：<c>TrayIcon.new</c> / <c>getById</c> / <c>removeById</c>）。
/// 不显式给 id 时自动分配一个随机 id。
/// </para>
/// <para>
/// 菜单项与图标都可以在创建后改（见 <see cref="OrielTray.SetMenu"/> 与 <see cref="OrielTray.SetIcon(OrielImage?)"/>）。
/// </para>
/// </remarks>
public sealed class OrielTrayOptions
{
    /// <summary>
    /// 托盘标识（唯一）。不设置时自动分配一个随机 id。
    /// </summary>
    /// <remarks>
    /// 用于运行期按 id 取回句柄（<see cref="OrielApp.GetTray"/>）与移除（<see cref="OrielApp.RemoveTray(string)"/>），
    /// 也是托盘事件里 <see cref="OrielTrayEvent.TrayId"/> 的值。多托盘应用应当显式给 id。
    /// </remarks>
    public string? Id { get; set; }

    private string? _iconPath;
    private OrielImage? _icon;

    /// <summary>
    /// 图标文件路径（PNG / ICO），相对路径按可执行文件所在目录解析。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不在这里读盘或解码</b>：赋值只记住路径，真正的解码发生在创建托盘时
    /// （见 <see cref="ResolveIcon"/>）。这样"图标文件缺失/格式不对"不会让
    /// <c>AddTray</c> 这个**配置期**回调抛异常——那正是 0.3.5 修掉的缺陷类别
    /// （一个纯观感问题被升级成"程序打不开"）。
    /// </para>
    /// <para>
    /// 读回的是**解析后的绝对路径**（相对路径已按 exe 目录展开）；未设置时为 <c>null</c>。
    /// 要"路径写错就立刻失败"的严格行为，直接用 <see cref="Icon"/> 传
    /// <see cref="OrielImage.FromFile"/> 的结果——那是显式调用，抛异常是预期行为。
    /// </para>
    /// </remarks>
    public string? IconPath
    {
        get => _iconPath is null ? null : OrielPathResolver.Resolve(_iconPath);
        set
        {
            _iconPath = value;
            _icon = null; // 后赋值的生效（与 Icon 共用同一份"生效图标"）
        }
    }

    /// <summary>
    /// 已解码的图标（对标 Tauri 2 的 <c>TrayIconOptions.icon</c>）。
    /// </summary>
    /// <remarks>
    /// <b>首选入口</b>：调用方自行决定失败策略——<see cref="OrielImage.FromFile"/> 抛异常、
    /// <see cref="OrielImage.TryFromFile"/> 返回 false，也可以在多个托盘/窗口之间复用同一个实例。
    /// </remarks>
    public OrielImage? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            _iconPath = null;
        }
    }

    /// <summary>
    /// 解析出**最终要用的图标**：显式给的 <see cref="Icon"/> 优先，否则读
    /// <see cref="IconPath"/> 指定的文件并解码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 解码失败**不抛异常**：报一条诊断并返回 <c>null</c>（调用方据此回落平台默认图标）。
    /// 三平台后端都走这一个入口，于是"读文件 + 解码"只写一处，格式支持三平台一致——
    /// 包括 Windows 也能吃 PNG（此前它只认 ICO/BMP，是 <c>LoadImageW</c> 的限制）。
    /// </para>
    /// <para>
    /// 返回的实例可能被多个托盘/窗口共享，调用方不得改写其 RGBA 数据。
    /// </para>
    /// </remarks>
    internal OrielImage? ResolveIcon()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        if (_iconPath is null)
        {
            return null;
        }

        string resolved = OrielPathResolver.Resolve(_iconPath);
        if (OrielImage.TryFromFile(resolved, out OrielImage? image, out string? error))
        {
            return image;
        }

        OrielDiagnostics.Report(
            $"[OrielWeb] 托盘图标无法使用，回落平台默认图标：{resolved}（{error}）");
        return null;
    }

    /// <summary>
    /// 只返回存在的图标路径（缺失时返回 null），不抛异常。
    /// </summary>
    /// <remarks>"有就用、没有就回落平台默认"的写法；与 <see cref="ResolveIcon"/> 的区别是不解码。</remarks>
    public string? IconPathOrNull
    {
        get
        {
            if (_iconPath is null)
            {
                return null;
            }

            string resolved = OrielPathResolver.Resolve(_iconPath);
            return File.Exists(resolved) ? resolved : null;
        }
    }

    /// <summary>悬停提示文本。</summary>
    public string Tooltip { get; set; } = "OrielWeb";

    /// <summary>
    /// 图标旁显示的文字（对标 Tauri 2 的 <c>title</c>）。
    /// </summary>
    /// <remarks>
    /// <b>Linux 显示、Windows 不支持、macOS 显示在图标旁</b>。适合"未读数""状态数字"这类
    /// 频繁更新但不值得换图的信息。
    /// </remarks>
    public string? Title { get; set; }

    /// <summary>
    /// 是否把图标当模板图（仅 macOS 有效，对标 Tauri 2 的 <c>iconAsTemplate</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>默认 <c>true</c></b>：托盘图标以单色剪影为主，模板图会自动跟随菜单栏深浅色反色——
    /// 不这么做的话，深色菜单栏上是一块看不清的黑图。
    /// </para>
    /// <para>
    /// <b>彩色品牌图标必须显式置 false</b>：否则会被系统按 alpha 通道重绘成单色剪影，
    /// 颜色全丢。这是本库此前的一处缺陷——模板标记曾被**无条件**设为 true，调用方无从表达
    /// "我这个是彩色图"。
    /// </para>
    /// </remarks>
    public bool IconAsTemplate { get; set; } = true;

    /// <summary>
    /// 是否允许点击图标直接弹出菜单。
    /// </summary>
    /// <remarks>
    /// <b>默认 false</b>：Windows 下左键点击只触发 <see cref="OrielTray.Clicked"/>，菜单走右键；
    /// 置 true 后左键也弹菜单。<b>macOS 与 Linux 上此项无效</b>——那两个平台设了菜单就是左键弹，
    /// 是平台惯例（Tauri 2 的 <c>showMenuOnLeftClick</c> 同样标注 Linux 不支持）。
    /// </remarks>
    public bool MenuOnClick { get; set; }
}
