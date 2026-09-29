namespace OrielWeb;

/// <summary>窗口初始配置（对齐 pywebview 基本面）。用 With* 方法链式配置。</summary>
public sealed class OrielWindowOptions
{
    public string Title { get; set; } = "Oriel";
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 700;
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    /// <summary>初始位置；未设置且 <see cref="Center"/> 为 true 时居中。</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Center { get; set; } = true;
    public bool Resizable { get; set; } = true;
    public bool Fullscreen { get; set; }
    public bool OnTop { get; set; }
    public bool Frameless { get; set; }
    public bool Hidden { get; set; }
    public bool Maximized { get; set; }
    /// <summary>直接加载的外部 URL（如 Vite dev server）。未设置时使用内嵌资产首页。</summary>
    public string? Url { get; set; }
    /// <summary>是否启用开发者工具（DevTools）。</summary>
    public bool Debug { get; set; }

    /// <summary>
    /// 窗口图标文件路径（PNG / ICO）。各平台落到最贴近的位置：
    /// Linux 设窗口图标（X11 下写入 <c>_NET_WM_ICON</c>）；Windows 用 <c>WM_SETICON</c> 覆盖 exe 图标；
    /// macOS **没有窗口级图标概念**，设置的是应用（Dock）图标 <c>NSApplication.applicationIconImage</c>。
    /// 未设置时 Windows 沿用 exe 自带图标、macOS 沿用 .app bundle 的图标。
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// 是否把页面的 console 输出转发给宿主（<see cref="WebviewWindow.ConsoleMessage"/> 事件）。
    /// 默认关闭：注入的 hook 会包装页面的 console 方法（改变其可观测行为，如 <c>console.log.toString()</c>），
    /// 且高频输出会变成持续的 IPC 流量。开发/调试时打开它能看到页面的日志。
    /// </summary>
    public bool ConsoleForwarding { get; set; }

    public OrielWindowOptions WithTitle(string title) { Title = title; return this; }
    public OrielWindowOptions WithSize(int width, int height) { Width = width; Height = height; return this; }
    public OrielWindowOptions WithMinSize(int width, int height) { MinWidth = width; MinHeight = height; return this; }
    public OrielWindowOptions At(int x, int y) { X = x; Y = y; Center = false; return this; }
    public OrielWindowOptions Centered(bool center = true) { Center = center; return this; }
    public OrielWindowOptions WithResizable(bool resizable = true) { Resizable = resizable; return this; }
    public OrielWindowOptions WithFullscreen(bool fullscreen = true) { Fullscreen = fullscreen; return this; }
    public OrielWindowOptions WithOnTop(bool onTop = true) { OnTop = onTop; return this; }
    public OrielWindowOptions WithFrameless(bool frameless = true) { Frameless = frameless; return this; }
    public OrielWindowOptions WithHidden(bool hidden = true) { Hidden = hidden; return this; }
    public OrielWindowOptions WithMaximized(bool maximized = true) { Maximized = maximized; return this; }
    public OrielWindowOptions WithUrl(string url) { Url = url; return this; }
    public OrielWindowOptions WithDebug(bool debug = true) { Debug = debug; return this; }

    /// <summary>设置窗口图标；平台差异见 <see cref="Icon"/>。</summary>
    public OrielWindowOptions WithIcon(string path) { Icon = path; return this; }

    /// <summary>开启/关闭 console 转发；见 <see cref="ConsoleForwarding"/>。</summary>
    public OrielWindowOptions WithConsoleForwarding(bool enabled = true) { ConsoleForwarding = enabled; return this; }
}
