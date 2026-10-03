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
    /// <summary>
    /// 直接加载的 URL。未设置时使用内嵌资产首页（<c>&lt;资源目录&gt;/index.html</c>）。
    /// </summary>
    /// <remarks>
    /// 取值指向内嵌资源（<c>oriel://&lt;UseEmbeddedAssets 的 host&gt;/…</c>，兼容别名
    /// <c>https://&lt;host&gt;/…</c>）时，三个平台都落到同一份内嵌资源上，由该 scheme 的处理器按需应答。
    /// 其它 URL（如 Vite dev server 的 <c>http://localhost:5173</c>）原样加载——注意那种页面的来源
    /// 不在可信前缀里，需要 <c>UseCapabilities(AllowOrigin)</c> 显式放行，否则桥接脚本不安装。
    /// </remarks>
    public string? Url { get; set; }
    /// <summary>是否启用开发者工具（DevTools）。</summary>
    public bool Debug { get; set; }

    private string? _icon;

    /// <summary>
    /// 窗口图标文件路径。各平台落到最贴近的位置：
    /// Linux 设窗口图标（X11 下写入 <c>_NET_WM_ICON</c>）；Windows 用 <c>WM_SETICON</c> 覆盖 exe 图标；
    /// macOS **没有窗口级图标概念**，设置的是应用（Dock）图标 <c>NSApplication.applicationIconImage</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Linux 与 macOS 必须显式给这个值，否则就是通用图标。</b>那两个平台没有"从可执行文件里取图标"
    /// 这回事——ELF 与 Mach-O 都不带图标，macOS 的图标在 <c>.app</c> bundle 里。Windows 则相反：
    /// 不设它也会主动从 exe 取（见 README「应用图标」），所以这条差异在 Windows 上根本看不出来。
    /// </para>
    /// <para>
    /// 格式：<b>PNG 在 Linux/macOS 上最稳</b>；Windows 的 <c>LoadImageW</c> 只认 ICO/BMP，
    /// 而它的 exe 图标走 <c>ApplicationIcon</c>，所以 Windows 侧通常不需要设这个值。
    /// </para>
    /// <para>
    /// 路径请用**绝对路径**：三个平台都按当前工作目录解析相对路径，而 macOS 从 <c>.app</c> 启动时
    /// cwd 是 <c>/</c>——相对路径在那里必然失效。
    /// </para>
    /// <para>
    /// 赋值时**校验文件存在**，不存在直接抛 <see cref="FileNotFoundException"/>。三个平台在加载失败时
    /// 都是**静默**的（GTK 丢掉 GError、Cocoa 拿到 nil 就跳过、Win32 的 <c>LoadImageW</c> 返回 0），
    /// 不在这里拦住，表现就是"图标没生效，且没有任何提示"。
    /// </para>
    /// </remarks>
    public string? Icon
    {
        get => _icon;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                if (!File.Exists(value))
                {
                    throw new FileNotFoundException(
                        $"图标文件不存在：{value}。相对路径按**当前工作目录**解析，" +
                        "而 macOS 上从 .app 启动时 cwd 是 /，打包分发请传绝对路径" +
                        "（例如 Path.Combine(AppContext.BaseDirectory, \"app.png\")）。",
                        value);
                }
            }

            _icon = value;
        }
    }

    /// <summary>
    /// 是否把页面的 console 输出转发给宿主（<see cref="WebviewWindow.ConsoleMessage"/> 事件）。
    /// 默认关闭：注入的 hook 会包装页面的 console 方法（改变其可观测行为，如 <c>console.log.toString()</c>），
    /// 且高频输出会变成持续的 IPC 流量。开发/调试时打开它能看到页面的日志。
    /// </summary>
    public bool ConsoleForwarding { get; set; }

    /// <summary>
    /// webview **内建**右键菜单的策略；默认 <see cref="OrielContextMenuPolicy.Editing"/>（只留剪切/复制/粘贴）。
    /// </summary>
    /// <remarks>
    /// 只作用于渲染引擎自己弹的菜单。宿主用 <see cref="WebviewWindow.ShowContextMenu"/> 弹的自建菜单
    /// 走的是另一条通道，不受影响。
    /// <para>
    /// 这是**初始值**；运行时可以改（下次右键就生效，无需重建窗口），见
    /// <see cref="WebviewWindow.ContextMenuPolicy"/>。
    /// </para>
    /// </remarks>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; } = OrielContextMenuPolicy.Editing;

    public OrielWindowOptions WithTitle(string title) { Title = title; return this; }
    public OrielWindowOptions WithSize(int width, int height) { Width = width; Height = height; return this; }
    public OrielWindowOptions WithMinSize(int width, int height) { MinWidth = width; MinHeight = height; return this; }
    public OrielWindowOptions At(int x, int y) { X = x; Y = y; Center = false; return this; }
    public OrielWindowOptions Centered(bool center = true) { Center = center; return this; }
    public OrielWindowOptions WithResizable(bool resizable = true) { Resizable = resizable; return this; }
    public OrielWindowOptions WithFullscreen(bool fullscreen = true) { Fullscreen = fullscreen; return this; }
    public OrielWindowOptions WithOnTop(bool onTop = true) { OnTop = onTop; return this; }
    public OrielWindowOptions WithFrameless(bool frameless = true) { Frameless = frameless; return this; }

    /// <summary>
    /// 拖动区域选择器（无边框窗口用）：命中的元素由库接管拖动与双击。
    /// </summary>
    /// <remarks>
    /// 不设它也能用——页面在自己的标题栏元素上写 <c>data-oriel-drag-region</c> 属性即可；
    /// 本选项用于"区域由宿主指定"的场景（页面不必知道有拖动这回事）。
    /// </remarks>
    internal string? DragRegionSelector { get; private set; }

    /// <summary>指定拖动区域的选择器（可多次调用，最后一次生效）。</summary>
    public OrielWindowOptions WithDragRegion(string selector) { ArgumentException.ThrowIfNullOrWhiteSpace(selector); DragRegionSelector = selector; return this; }
    public OrielWindowOptions WithHidden(bool hidden = true) { Hidden = hidden; return this; }
    public OrielWindowOptions WithMaximized(bool maximized = true) { Maximized = maximized; return this; }
    public OrielWindowOptions WithUrl(string url) { Url = url; return this; }
    public OrielWindowOptions WithDebug(bool debug = true) { Debug = debug; return this; }

    /// <summary>设置窗口图标；平台差异见 <see cref="Icon"/>。</summary>
    public OrielWindowOptions WithIcon(string path) { Icon = path; return this; }

    /// <summary>开启/关闭 console 转发；见 <see cref="ConsoleForwarding"/>。</summary>
    public OrielWindowOptions WithConsoleForwarding(bool enabled = true) { ConsoleForwarding = enabled; return this; }

    /// <summary>设置内建右键菜单策略；见 <see cref="ContextMenuPolicy"/>。</summary>
    public OrielWindowOptions WithContextMenuPolicy(OrielContextMenuPolicy policy) { ContextMenuPolicy = policy; return this; }
}
