namespace OrielWeb;

/// <summary>窗口初始配置（对齐 pywebview 基本面）。用 With* 方法链式配置。</summary>
public sealed class OrielWindowOptions
{
    /// <summary>
    /// 窗口的稳定标识（对标 Tauri 2 的窗口 label）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <c>"main"</c>。它是**寻址单位**：页面侧 <c>getCurrentWindow()</c> 拿到的是自己，
    /// <c>GetByLabel("settings")</c> 拿到指定的那一个，<c>emitTo("settings", …)</c> 把事件
    /// 只发给它。没有 label 时这些都做不了——多窗口应用只能靠"我记住那个对象引用"，
    /// 而对象引用过不了 IPC。
    /// </para>
    /// <para>
    /// <b>同一应用内必须唯一</b>：重复时 <see cref="OrielApp.CreateWindow"/> 与
    /// <c>OrielAppBuilder.Run()</c> 会抛 <see cref="InvalidOperationException"/>。
    /// 不在这里抛是因为构建器期还没有应用实例，无法知道已有哪些 label；
    /// 而且"默认都是 main"这件事只有在真正建第二个窗口时才成为问题。
    /// </para>
    /// <para>
    /// 字符限制与 <c>UseEmbeddedAssets</c> 的 host 相同（字母、数字、<c>-</c>、<c>.</c>、<c>_</c>）：
    /// 它会出现在命令参数与将来的 capability 配置里，含空格/斜杠/中文会让配置无法逐字节匹配。
    /// </para>
    /// </remarks>
    public string Label
    {
        get => _label;
        set => _label = ValidateLabel(value);
    }

    private string _label = DefaultLabel;

    /// <summary>未显式指定 label 时的默认值。单窗口应用因此不必写它。</summary>
    public const string DefaultLabel = "main";

    /// <summary>设置窗口 label；语义与限制见 <see cref="Label"/>。</summary>
    public OrielWindowOptions WithLabel(string label) { Label = label; return this; }

    /// <summary>
    /// 校验并归一 label：非空、只含允许字符。
    /// </summary>
    /// <remarks>
    /// 归一为**小写**：label 会进命令参数与配置，而三平台的文件系统/配置比对大小写规则不同
    /// （Windows 不敏感、Linux 敏感）。统一小写之后，"Main" 与 "main" 不会被当成两个窗口，
    /// 也不会出现"配置里写 main、页面报 Main"这种只在某个平台发生的错配。
    /// </remarks>
    internal static string ValidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException(
                $"窗口 label 不能为空。默认值是 \"{DefaultLabel}\"，多窗口请各给不同的值。", nameof(label));
        }

        foreach (char c in label)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            {
                throw new ArgumentException(
                    $"窗口 label「{label}」含非法字符「{c}」：它要出现在命令参数与能力配置里，" +
                    "只能用字母、数字、'-'、'.'、'_'。", nameof(label));
            }
        }

        return label.ToLowerInvariant();
    }

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

    /// <summary>
    /// 是否允许用户缩放页面（Ctrl/Cmd + 滚轮、Ctrl/Cmd + 加减号/0、触摸板捏合）。默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 固定布局的工业界面通常要关掉它：整屏被误缩放会导致布局错乱，而且没有复位入口
    /// （下游反馈 2026-10-11 的 P0-1）。
    /// </para>
    /// <para>
    /// 实现分两层：宿主侧设各引擎的专有开关（Windows <c>IsZoomControlEnabled</c> +
    /// <c>IsPinchZoomEnabled</c>、macOS <c>allowsMagnification</c>），文档层再补一道捕获期拦截
    /// （见 <c>Bridge/oriel-bridge.js</c> 的缩放闸门）——**Linux 的 WebKitGTK 没有等价的单开关**，
    /// 那一侧完全靠文档层这道拦截。只影响"用户手势缩放"，页面自己实现的缩放（画布缩放一类）不受影响。
    /// </para>
    /// </remarks>
    public bool Zoom { get; set; } = true;

    /// <summary>
    /// 页面渲染出来之前窗口的底色（<c>0xAARRGGBB</c>）。<c>null</c>（默认）表示用平台默认（通常是白）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 解决的是所有 webview 应用的共同问题：内核就绪前后那一帧的**闪色**——工业界面上尤其刺眼，
    /// 而页面 CSS 这时还没生效，谁都管不了那一帧（下游反馈 2026-10-11 的 P1-1）。
    /// </para>
    /// <para>
    /// 三平台各落到引擎的等价开关：Windows 用 WebView2 的 <c>DefaultBackgroundColor</c>、
    /// macOS 用窗口底色 + <c>WKWebView.underPageBackgroundColor</c>（12+，取不到就只设窗口底色）、
    /// Linux 用 WebKitGTK 的 <c>webkit_web_view_set_background_color</c>。
    /// </para>
    /// <para>
    /// 只管"底色"，不是"页面背景"：页面自己的 CSS 一生效就由页面说了算。alpha 只有在平台把窗口
    /// 设成透明时才有意义——本库不主动那么做。
    /// </para>
    /// </remarks>
    public uint? BackgroundColor { get; set; }

    private string? _icon;
    private string? _iconSource;
    private bool _requireIcon;
    private bool _iconWarned;

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
    /// <b>相对路径按可执行文件所在目录（<see cref="AppContext.BaseDirectory"/>）解析</b>，不是当前
    /// 工作目录：工控机现场常从快捷方式、计划任务、开机自启启动，cwd 往往不是程序目录；macOS 从
    /// <c>.app</c> 启动时 cwd 更是 <c>/</c>。要按 cwd 解析请自行传 <see cref="Path.GetFullPath(string)"/>
    /// 的结果（绝对路径原样使用）。
    /// </para>
    /// <para>
    /// <b>文件不存在时降级，不抛异常</b>：图标是纯观感，缺失只该回落到平台默认图标（Windows 的 exe
    /// 图标、Linux/macOS 的 <c>app.png</c>），不该让整个程序起不来——此前它在 <c>AddWindow</c> 的配置
    /// 回调里抛 <see cref="FileNotFoundException"/>，用户看到的是"双击没反应"（下游反馈 2026-10-14 的 P1-2）。
    /// 降级时会经 <see cref="OrielDiagnostics.Reported"/> 报一条警告，因此仍然可观测。
    /// 需要"路径写错就启动失败"的严格行为，置 <see cref="RequireIcon"/>。
    /// </para>
    /// <para>
    /// 读回的是**生效值**：文件不存在且未开严格模式时为 <c>null</c>（表示走平台默认图标）；
    /// 原始请求路径见 <see cref="RequestedIcon"/>。
    /// </para>
    /// </remarks>
    public string? Icon
    {
        get => _icon;
        set
        {
            if (value is null)
            {
                _iconSource = null;
                _icon = null;
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _iconSource = OrielPathResolver.Resolve(value);
            _iconWarned = false;
            EvaluateIcon();
        }
    }

    /// <summary>
    /// 原始请求的图标路径（已按可执行文件目录解析），无论文件是否存在；未设置时为 <c>null</c>。
    /// 用于排障：<see cref="Icon"/> 在降级后是 <c>null</c>，这里仍能看到当初写的是什么。
    /// </summary>
    public string? RequestedIcon => _iconSource;

    /// <summary>
    /// 是否要求图标文件必须存在（默认 <c>false</c>）。置 <c>true</c> 时，图标文件缺失会在配置期
    /// 抛 <see cref="FileNotFoundException"/>，而不是降级成平台默认图标。
    /// </summary>
    /// <remarks>
    /// 默认关闭：图标缺失属于观感问题，把它升级成"启动失败"会让一个笔误变成"程序打不开"
    /// （见 <see cref="Icon"/>）。需要尽早暴露打包漏文件时才打开它。
    /// 可在 <see cref="Icon"/> 之前或之后设置，两种顺序都会重新求值。
    /// </remarks>
    public bool RequireIcon
    {
        get => _requireIcon;
        set
        {
            _requireIcon = value;
            EvaluateIcon();
        }
    }

    /// <summary>按当前策略求值图标：存在则生效；缺失时严格模式抛错、否则降级为 null 并报一条诊断。</summary>
    private void EvaluateIcon()
    {
        if (_iconSource is null)
        {
            _icon = null;
            return;
        }

        if (File.Exists(_iconSource))
        {
            _icon = _iconSource;
            return;
        }

        if (_requireIcon)
        {
            throw new FileNotFoundException(
                $"图标文件不存在：{_iconSource}。相对路径按可执行文件所在目录解析" +
                $"（{AppContext.BaseDirectory}），请确认打包产物里有该文件，或传绝对路径。",
                _iconSource);
        }

        _icon = null;
        if (!_iconWarned)
        {
            _iconWarned = true;
            OrielDiagnostics.Report(
                $"[OrielWeb] 窗口图标不存在，已降级为平台默认图标：{_iconSource}" +
                $"（相对路径按可执行文件所在目录解析：{AppContext.BaseDirectory}）。" +
                "需要启动即失败请设 OrielWindowOptions.RequireIcon = true。");
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

    /// <summary>窗口标题；见 <see cref="Title"/>。</summary>
    public OrielWindowOptions WithTitle(string title) { Title = title; return this; }

    /// <summary>初始尺寸（逻辑像素，三平台一致）；见 <see cref="Width"/>。</summary>
    public OrielWindowOptions WithSize(int width, int height) { Width = width; Height = height; return this; }

    /// <summary>最小尺寸（逻辑像素）；见 <see cref="MinWidth"/>。</summary>
    public OrielWindowOptions WithMinSize(int width, int height) { MinWidth = width; MinHeight = height; return this; }

    /// <summary>初始位置：窗口**左上角**落在屏幕逻辑坐标 (x, y)，并顺带关掉居中；见 <see cref="X"/>。</summary>
    public OrielWindowOptions At(int x, int y) { X = x; Y = y; Center = false; return this; }

    /// <summary>是否居中显示；见 <see cref="Center"/>。</summary>
    public OrielWindowOptions Centered(bool center = true) { Center = center; return this; }

    /// <summary>是否允许用户调整窗口大小；见 <see cref="Resizable"/>。</summary>
    public OrielWindowOptions WithResizable(bool resizable = true) { Resizable = resizable; return this; }

    /// <summary>启动即全屏；见 <see cref="Fullscreen"/>。</summary>
    public OrielWindowOptions WithFullscreen(bool fullscreen = true) { Fullscreen = fullscreen; return this; }

    /// <summary>窗口置顶；见 <see cref="OnTop"/>。</summary>
    public OrielWindowOptions WithOnTop(bool onTop = true) { OnTop = onTop; return this; }

    /// <summary>无边框窗口（页面自绘标题栏）；见 <see cref="Frameless"/> 与 API.md §2。</summary>
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
    /// <summary>窗口不上屏（页面照常加载、IPC 照常往返）；见 <see cref="Hidden"/>。</summary>
    public OrielWindowOptions WithHidden(bool hidden = true) { Hidden = hidden; return this; }

    /// <summary>开启/关闭用户缩放页面；见 <see cref="Zoom"/>。</summary>
    public OrielWindowOptions WithZoom(bool enabled = true) { Zoom = enabled; return this; }

    /// <summary>设置内核就绪前的窗口底色（<c>0xAARRGGBB</c>）；见 <see cref="BackgroundColor"/>。</summary>
    public OrielWindowOptions WithBackgroundColor(uint argb) { BackgroundColor = argb; return this; }

    /// <summary>设置内核就绪前的窗口底色（不透明）；见 <see cref="BackgroundColor"/>。</summary>
    public OrielWindowOptions WithBackgroundColor(byte red, byte green, byte blue)
        => WithBackgroundColor(0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue);

    /// <summary>
    /// 底色拆成 (A, R, G, B)；未设置时返回 false。
    /// </summary>
    /// <remarks>
    /// 三个平台的实现都从这里取分量：ARGB 的拆法只写一处，免得某平台把 R 与 B 搞反
    /// （那种错在深色底上不容易一眼看出来）。
    /// </remarks>
    internal bool TryGetBackgroundArgb(out byte a, out byte r, out byte g, out byte b)
    {
        a = r = g = b = 0;
        if (BackgroundColor is not { } argb)
        {
            return false;
        }

        a = (byte)(argb >> 24);
        r = (byte)(argb >> 16);
        g = (byte)(argb >> 8);
        b = (byte)argb;
        return true;
    }

    /// <summary>启动即最大化；见 <see cref="Maximized"/>。</summary>
    public OrielWindowOptions WithMaximized(bool maximized = true) { Maximized = maximized; return this; }

    /// <summary>直接加载的 URL；见 <see cref="Url"/>。</summary>
    public OrielWindowOptions WithUrl(string url) { Url = url; return this; }

    /// <summary>打开 DevTools（只"允许"检查，不自动弹面板）；见 <see cref="Debug"/>。</summary>
    public OrielWindowOptions WithDebug(bool debug = true) { Debug = debug; return this; }

    /// <summary>设置窗口图标；平台差异与"缺失即降级"的语义见 <see cref="Icon"/>。</summary>
    public OrielWindowOptions WithIcon(string path) { Icon = path; return this; }

    /// <summary>
    /// 要求图标文件必须存在（默认关闭）：缺失时在配置期抛异常，而不是降级为平台默认图标；
    /// 见 <see cref="RequireIcon"/>。
    /// </summary>
    public OrielWindowOptions WithRequireIcon(bool require = true) { RequireIcon = require; return this; }

    /// <summary>开启/关闭 console 转发；见 <see cref="ConsoleForwarding"/>。</summary>
    public OrielWindowOptions WithConsoleForwarding(bool enabled = true) { ConsoleForwarding = enabled; return this; }

    /// <summary>设置内建右键菜单策略；见 <see cref="ContextMenuPolicy"/>。</summary>
    public OrielWindowOptions WithContextMenuPolicy(OrielContextMenuPolicy policy) { ContextMenuPolicy = policy; return this; }
}
