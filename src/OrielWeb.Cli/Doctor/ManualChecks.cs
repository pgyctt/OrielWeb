namespace OrielWeb.Cli.Doctor;

/// <summary>
/// 按当前平台列出**只能人眼判定**的那些验证项，以及怎么验、该看到什么。
/// </summary>
/// <remarks>
/// <para>
/// 这一节的定位：ROADMAP 的「待真机验证清单」里，有些项本质上是"看到才算数"
/// （托盘图标有没有出现在托盘区、通知横幅弹没弹、对话框长得对不对、拖放能不能真的拖进去）。
/// 工具**不假装**能判定它们——它只把该跑什么、该看到什么摆出来，省掉"翻文档找步骤"这一步。
/// </para>
/// <para>
/// <see cref="ManualStep.RoadmapSection"/> 必须与 ROADMAP 里那一节的标题**逐字符**相同：
/// 有单测（<c>DoctorTests</c>）拿 ROADMAP.md 去核对这些名字。否则这里改个措辞、文档改个措辞，
/// 两边就会各自漂移——而"文档里写的检查项在工具里找不到"正是要避免的失配。
/// </para>
/// </remarks>
internal static class ManualChecks
{
    // 清单项的标题（逐字符对应 ROADMAP 的 #### 小节）
    internal const string WindowIcon = "窗口图标（三平台）";
    internal const string ClipboardInterop = "剪贴板的跨进程互操作（未验证）";
    internal const string ThemeRealtime = "主题切换的实时性（未验证）";
    internal const string DevTools = "DevTools 开关（macOS / Linux）";
    internal const string MacHiddenStart = "macOS 隐藏启动";
    internal const string LinuxHiDpiDrag = "Linux 高 DPI 下的拖动是否跟手（宿主侧已确认无需折算，只剩页面那一半）";
    internal const string DragBehavior = "标题栏拖动与双击（改为库接管之后）";
    internal const string MultiWindow = "多窗口（运行时新建的窗口）";
    internal const string TrayIcon = "托盘图标的可见性";
    internal const string Notifications = "通知的展示";
    internal const string ContextMenu = "上下文菜单的外观与交互";
    internal const string Dialogs = "对话框的外观与交互";
    internal const string FileDrop = "文件拖放（整体未验证）";
    internal const string BuiltInContextMenu = "内建右键菜单（接管式）";

    private static readonly ManualStep MultiWindowStep = new(
        MultiWindow,
        "跑 demo --manual-check，点「打开 todo 窗口」（可连点几次）；关掉其中一个；" +
        "在任一窗口里执行 oriel.invoke('win.close')",
        "新窗口正常加载同一个 todo 页面（标题栏显示「窗口 N」）；关掉一个窗口应用不退出；" +
        "win.close 只关掉发起调用的那个窗口。Windows 侧已有 --selftest multiwindow 机器断言");

    private static readonly ManualStep DragStep = new(
        DragBehavior,
        "按住标题栏拖动；双击标题栏；单击标题栏上的最小化/关闭按钮；在标题栏里的输入框上拖选文字",
        "拖动跟手、双击切换最大化/还原、按钮照常可点、可交互元素不被当成拖动区域");

    private static readonly ManualStep WindowIconStep = new(
        WindowIcon,
        "直接运行 demo（不传 --icon），Linux 看任务栏、macOS 看 Dock、Windows 看任务栏与 Alt-Tab；" +
        "GNOME 上还要按 ROADMAP 的步骤装一份 .desktop（按 WM_CLASS 匹配）",
        "任务栏/Dock 显示项目图标（GNOME 上装完 .desktop 后）；xprop -id <窗口id> _NET_WM_ICON 能看到图标数据");

    private static readonly ManualStep ClipboardStep = new(
        ClipboardInterop,
        "OrielDemo --selftest clipboard（它会写系统剪贴板），然后在别的应用里粘贴；" +
        "反过来在别的应用里复制带格式内容，再用 window.ClipboardHtml 读",
        "文本能互相粘贴；HTML 粘到富文本编辑器（Word / LibreOffice）应保留粗体等格式");

    private static readonly ManualStep ThemeStep = new(
        ThemeRealtime,
        "不带参数运行 demo，然后在系统设置里切换深色/浅色",
        "一秒内页面主题跟随（app.js 会把主题写到 <html data-theme>），宿主侧 ThemeChanged 触发一次");

    private static readonly ManualStep DevToolsStep = new(
        DevTools,
        "以 Debug = true 启动（samples/OrielDemo 默认 .UseDebug()），" +
        "macOS：Safari → 开发 → 选该进程的 webview；Linux：页面内右键 → 「检查元素」",
        "能打开 Web Inspector；把 Debug 置 false 后同一入口不再出现");

    private static readonly ManualStep MacHiddenStep = new(
        MacHiddenStart,
        "OrielDemo --hidden（macOS 上跑），用 CGWindowList 断言窗口不在 on-screen 列表里",
        "窗口不上屏，但页面照常加载、IPC 照常往返（Linux 侧已在 WSL 验证过同样的语义）");

    private static readonly ManualStep HiDpiStep = new(
        LinuxHiDpiDrag,
        "把桌面缩放设成 200% 后跑 demo（X11 与 Wayland 各一次；GDK_SCALE=2 只在 X11 下生效），" +
        "按住标题栏拖动、双击标题栏",
        "拖动跟手；双击最大化正常、单击不移动窗口（拖动与双击由库实现，页面只标注了拖动区域）。" +
        "若窗口比指针快约 scale 倍，说明增量被设备像素污染——拖动逻辑在库侧" +
        "（src/OrielWeb/Bridge/oriel-bridge.js），修法不要写成在宿主侧乘 scale");

    private static readonly ManualStep TrayStep = new(
        TrayIcon,
        "OrielDemo --selftest shell（会建托盘并设一份含分隔线/勾选/禁用/子菜单的菜单），" +
        "然后在托盘区找到图标点开",
        "图标出现、悬停有 tooltip、菜单按设置渲染（禁用项灰、勾选项带勾、子菜单能展开）、点 Quit 退出");

    private static readonly ManualStep NotificationStep = new(
        Notifications,
        "OrielDemo --selftest shell，然后看通知横幅",
        "横幅显示标题与正文。点击横幅不会有任何回调——该能力已整体移除（三平台都拿不到）");

    private static readonly ManualStep ContextMenuStep = new(
        ContextMenu,
        "用 devtools 控制台执行 oriel.invoke('win.contextMenu')",
        "菜单按设置渲染（子菜单可展开、勾选项带勾、禁用项灰、分隔线正确）；role 里的 Close 能关掉窗口");

    private static readonly ManualStep DialogStep = new(
        Dialogs,
        "在示例里调 ShowOpenFileDialog（开 AllowMultiple）、ShowSaveFileDialog、ShowFolderDialog",
        "过滤器下拉显示传入名称；多选后每一项都在返回数组里；保存时输入不带扩展名的名字，落盘会补上 DefaultExtension");

    private static readonly ManualStep FileDropStep = new(
        FileDrop,
        "从文件管理器往窗口里拖 1 个文件 / 多个文件 / 一个文件夹 / 一段选中的文本",
        "前三种都触发 FileDropped 且路径逐字符正确（文件夹也会出现在列表里），" +
        "拖文本**不触发**（只受理文件 URL）");

    private static readonly ManualStep BuiltInMenuStep = new(
        BuiltInContextMenu,
        "在操作台页面普通区域右键；再切到「平台原样」「完全不弹」各试一次；" +
        "最后**连点右键 5～6 次**（WebKitGTK 那处内存破坏的回归项）",
        "默认策略下只出现剪切/复制/粘贴，且在输入框里对选中内容真的有效；" +
        "「平台原样」恢复出后退/刷新/另存为等；「完全不弹」什么都不出现；连点不崩");

    /// <summary>当前平台上需要人眼验证的项（按平台挑，Linux 项不摆到 Windows 上）。</summary>
    internal static IEnumerable<ManualStep> ForCurrentPlatform()
    {
        yield return DragStep;
        yield return MultiWindowStep;
        yield return WindowIconStep;
        yield return ClipboardStep;

        if (OperatingSystem.IsLinux())
        {
            yield return ThemeStep;
            yield return DevToolsStep;
            yield return HiDpiStep;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ThemeStep;
            yield return DevToolsStep;
            yield return MacHiddenStep;
        }

        yield return TrayStep;
        yield return NotificationStep;
        yield return ContextMenuStep;
        yield return DialogStep;
        yield return FileDropStep;
        yield return BuiltInMenuStep;
    }
}
