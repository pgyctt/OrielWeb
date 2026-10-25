using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>Closing 事件的参数；把 <see cref="Cancel"/> 置为 true 可阻止窗口关闭。</summary>
public sealed class OrielCloseRequestEventArgs
{
    public bool Cancel { get; set; }
}

public enum OrielMessageBoxIcon { Info, Warning, Error, Question }

/// <summary>导航完成事件的参数（成功与失败共用；失败时 <see cref="Error"/> 非空）。</summary>
public sealed class OrielNavigationCompletedEventArgs
{
    internal OrielNavigationCompletedEventArgs(bool success, string? url, string? error)
    {
        Success = success;
        Url = url ?? string.Empty;
        Error = error;
    }

    /// <summary>导航是否成功。</summary>
    public bool Success { get; }

    /// <summary>完成（或失败）的 URL；平台未提供时为空串。</summary>
    public string Url { get; }

    /// <summary>失败原因（成功时为 null）。各平台文案不同，仅用于日志与提示。</summary>
    public string? Error { get; }
}

/// <summary>页面 console 输出（需 <see cref="OrielWindowOptions.ConsoleForwarding"/> 打开）。</summary>
public sealed class OrielConsoleMessageEventArgs
{
    internal OrielConsoleMessageEventArgs(string level, string text)
    {
        Level = level;
        Text = text;
    }

    /// <summary>console 方法名：log / info / warn / error / debug。</summary>
    public string Level { get; }

    /// <summary>拼好的文本（多个参数以空格连接）。</summary>
    public string Text { get; }
}

/// <summary>页面经 <c>oriel.postMessage(name, payload)</c> 发来的消息。</summary>
public sealed class OrielMessageReceivedEventArgs
{
    internal OrielMessageReceivedEventArgs(string name, string json)
    {
        Name = name;
        Json = json;
    }

    /// <summary>消息名（页面侧 postMessage 的第一个参数）。</summary>
    public string Name { get; }

    /// <summary>payload 的原始 JSON 文本；页面未提供 payload 时是 <c>null</c>。反序列化由调用方按自己的类型做。</summary>
    public string Json { get; }
}

/// <summary>
/// 窗口公共门面。生命周期事件必须在 <c>OrielAppBuilder.Run()</c> 之前订阅。
/// 窗口方法（全屏/置顶/移动/对话框等）对齐 pywebview 基本面。
/// </summary>
/// <remarks>
/// 实现 <see cref="IOrielWindowControl"/> 是为了让内建的 <c>win.*</c> 命令能作用到窗口上
/// （见 <see cref="OrielBuiltInWindowCommands"/>）——那是库内部用的窄接口，不是给应用实现的。
/// </remarks>
public sealed class WebviewWindow : IOrielWindowControl
{
    private IWindowBackend? _backend;
    private readonly object _gate = new();

    /// <summary>页面首次导航完成（UI 已可用）。</summary>
    public event Action? Loaded;
    /// <summary>用户请求关闭窗口；置 <see cref="OrielCloseRequestEventArgs.Cancel"/>' = true 可取消。</summary>
    public event Action<OrielCloseRequestEventArgs>? Closing;
    /// <summary>窗口已销毁。</summary>
    public event Action? Closed;
    /// <summary>页面标题变化。</summary>
    public event Action<string>? TitleChanged;
    /// <summary>窗口最大化状态变化（用户在原生路径下最大化/还原时也会触发）。</summary>
    public event Action<bool>? MaximizedChanged;

    /// <summary>
    /// 窗口客户区尺寸变化（对标 Tauri 2 的 <c>onResized</c>）。
    /// </summary>
    /// <remarks>
    /// 用户拖边框、最大化/还原、程序调 <see cref="Resize"/> 都会触发。
    /// 宽高是**逻辑像素的客户区**（与 <see cref="Resize"/> 同一语义）。
    /// <para>
    /// <b>它不是页面 <c>window.resize</c> 的替代品</b>，而是补充：页面事件只在客户区变化时触发，
    /// 拿不到"窗口被移到另一台不同缩放的显示器"这类信息；而宿主事件在原生侧产生，
    /// 三平台都会到。
    /// </para>
    /// </remarks>
    public event Action<OrielSizeChangedEventArgs>? Resized;

    /// <summary>
    /// 窗口位置变化（对标 Tauri 2 的 <c>onMoved</c>）。
    /// </summary>
    /// <remarks>
    /// 参数是窗口**左上角**的屏幕逻辑坐标（与 <see cref="MoveTo"/> 同一原点）。
    /// 页面拿不到可靠的窗口位置（<c>window.screenX</c> 在多屏与 HiDPI 下三平台行为不一致），
    /// 所以只能由宿主上报。
    /// </remarks>
    public event Action<OrielPositionChangedEventArgs>? Moved;

    /// <summary>
    /// 窗口获得或失去焦点（对标 Tauri 2 的 <c>onFocusChanged</c>）；参数 <c>true</c> 表示获得。
    /// </summary>
    /// <remarks>
    /// 只上报焦点变化，**不动焦点归属**：Windows 的 <c>WM_KILLFOCUS</c> 刻意不把焦点塞回宿主
    /// （那会让页面的 caret/选区/IME 错乱），本事件只做通知。
    /// </remarks>
    public event Action<bool>? FocusChanged;

    /// <summary>
    /// 窗口所在显示器的缩放比变了（对标 Tauri 2 的 <c>onScaleChanged</c>）；参数是新的缩放比。
    /// </summary>
    /// <remarks>
    /// 页面侧同名事件（<c>scale.changed</c>）的 payload 多带一个物理尺寸，见
    /// <see cref="IWindowBackend.ScaleFactorChanged"/>。
    /// </remarks>
    public event Action<double>? ScaleFactorChanged;
    /// <summary>导航开始（新文档开始加载）；参数为即将加载的 URL（平台未提供时为空串）。</summary>
    public event Action<string>? NavigationStarting;
    /// <summary>导航完成（成功与失败都触发，失败时 <see cref="OrielNavigationCompletedEventArgs.Error"/> 非空）。</summary>
    public event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted;
    /// <summary>页面 console 输出（需 <see cref="OrielWindowOptions.ConsoleForwarding"/> 打开）。</summary>
    public event Action<OrielConsoleMessageEventArgs>? ConsoleMessage;
    /// <summary>页面经 <c>oriel.postMessage(name, payload)</c> 发来的消息。</summary>
    public event Action<OrielMessageReceivedEventArgs>? MessageReceived;
    /// <summary>上下文菜单里的自定义项被点击；参数是该项的 <see cref="OrielMenuItem.Id"/>。</summary>
    public event Action<string>? ContextMenuItemClicked;

    /// <summary>
    /// 外部文件被拖进窗口；<see cref="OrielFileDropEventArgs.Paths"/> 是本地路径（不是 URI）。
    /// </summary>
    /// <remarks>
    /// 路径必须由原生侧给：页面自己的 <c>drop</c> 事件拿不到文件路径（浏览器的安全模型如此），
    /// 所以页面若想自己处理拖放外观（高亮、预览），仍应订阅 <c>dragover</c>/<c>drop</c> 做视觉反馈，
    /// 真正的路径从这里来。
    /// </remarks>
    public event Action<OrielFileDropEventArgs>? FileDropped;

    /// <summary>
    /// 拖放的四个阶段（enter / over / drop / leave），带客户区物理像素坐标。
    /// </summary>
    /// <remarks>
    /// internal 而不是 public：这是库**自己**推页面事件用的（<see cref="OrielApp"/> 订阅它，
    /// 再编成 <c>drag.*</c> 事件发给页面）。应用侧要观测拖放应当用公开的
    /// <see cref="FileDropped"/>，或者干脆在页面里订阅 <c>onDragDropEvent</c>——
    /// 多一个公开面就多一套要长期维持的语义。
    /// </remarks>
    internal event Action<OrielDragEventArgs>? DragDropReceived;

    /// <summary>
    /// 在鼠标位置弹出上下文菜单（项里的 <see cref="OrielMenuItem.Role"/> 走平台语义，
    /// 自定义项在用户选择后触发 <see cref="ContextMenuItemClicked"/>）。
    /// </summary>
    /// <remarks>
    /// <b>Windows 上这个调用会阻塞</b>，直到用户选择或取消——原生弹出菜单自带模态消息循环。
    /// <b>macOS 同样阻塞</b>（AppKit 的 <c>popUpMenuPositioningItem:</c> 跟踪到用户选择）；
    /// 只有 Linux 是异步弹出（调用立即返回，之后事件才来）。
    /// 因此不要从"需要立刻继续"的路径里调用它（例如页面命令处理里同步等结果）；三平台的共同保证只有
    /// "用户选中自定义项后会触发事件"，没有"调用返回时用户已选完"。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
        => ShowContextMenu(items, null);

    /// <summary>
    /// 在指定位置弹出上下文菜单（对标 Tauri 2 的 <c>Menu.popup(at?)</c>）。
    /// </summary>
    /// <param name="items">菜单树。</param>
    /// <param name="position">
    /// 弹出位置（**逻辑像素**，相对窗口客户区左上角）；<c>null</c> 表示鼠标位置。
    /// </param>
    /// <remarks>
    /// 与单参版本的区别只有位置来源：单参版本等价于传 <c>null</c>（跟随鼠标）。
    /// 两个重载而不是把参数并进一个，是为了不破坏既有调用点——
    /// <see cref="ShowContextMenu(IReadOnlyList{OrielMenuItem})"/> 是 0.1 起的公开 API。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items, OrielPosition? position)
        => ShowContextMenu(items, position, null);

    /// <summary>
    /// 在指定位置弹出，并可选地在若干毫秒后**自动关闭**。
    /// </summary>
    /// <param name="items">菜单树。</param>
    /// <param name="position">弹出位置（逻辑像素）；<c>null</c> 表示鼠标位置。</param>
    /// <param name="autoDismissAfter">
    /// 自动关闭延时；<c>null</c> 表示等用户选择。
    /// </param>
    /// <remarks>
    /// 这个重载主要为**无交互的自检与自动化测试**存在：Windows 的弹出会阻塞到用户选择，
    /// 而 CI 上没有人点，没有自动关闭就会一直挂着。
    /// 应用代码通常不需要它——用户点菜单外面就取消了。
    /// </remarks>
    public void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        Backend.ShowContextMenu(items, position, autoDismissAfter);
    }

    /// <summary>
    /// 关掉当前正在显示的上下文菜单（没有菜单在屏上时是空操作）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 主要用途是<b>把 Windows 上那个阻塞的弹出提前结束</b>：<see cref="ShowContextMenu(IReadOnlyList{OrielMenuItem}, OrielPosition?)"/>
    /// 在用户选择之前不返回，而 UI 线程被占着时页面上的按钮点不到——想"用代码关掉菜单"
    /// 只能从这里进来（页面侧是 <c>menu.close()</c>）。
    /// </para>
    /// <para>
    /// macOS 与 Linux 的弹出本来就不阻塞，这条对它们是"用户按 Esc"的等价物。
    /// </para>
    /// </remarks>
    public void CloseContextMenu() => Backend.CloseContextMenu();

    /// <summary>
    /// 这个窗口是否**显式**设过菜单栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用来实现 Tauri 的 "had one set explicitly" 语义：<see cref="SetAppMenuBar"/> 只覆盖
    /// **没有**显式设过菜单栏的窗口。没有这个标志的话，应用菜单会把页面自己给某个窗口定的
    /// 菜单栏悄悄换掉，而页面没有任何办法发现这件事。
    /// </para>
    /// <para>
    /// 移除菜单栏（传空集合）也算"设过"：调用方的意图是"这个窗口不要菜单栏"，
    /// 而不是"回到应用默认"。
    /// </para>
    /// </remarks>
    public bool HasExplicitMenuBar { get; private set; }

    /// <summary>
    /// 设置这个窗口的菜单栏（页面侧 <c>Menu.setAsWindowMenu</c>）。
    /// </summary>
    /// <param name="items">顶层项；空集合表示移除菜单栏。</param>
    /// <exception cref="NotSupportedException">
    /// macOS：菜单在 macOS 是应用级的，没有"某个窗口的菜单栏"这回事（与 Tauri 一致）。
    /// </exception>
    /// <remarks>
    /// 菜单栏占客户区高度——页面看到的 <c>innerSize</c> 会比整窗矮一个菜单栏，
    /// 见 <see cref="IWindowBackend.SetWindowMenuBar"/> 里的说明。
    /// </remarks>
    public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        HasExplicitMenuBar = true;
        Backend.SetWindowMenuBar(items);
    }

    /// <summary>
    /// 让这个窗口反映应用级菜单栏（页面侧 <c>Menu.setAsAppMenu</c>）。
    /// </summary>
    /// <param name="items">应用菜单的顶层项。</param>
    /// <remarks>
    /// 已经用 <see cref="SetWindowMenuBar"/> 显式设过菜单栏的窗口**不被覆盖**——
    /// 这个判断在这里做而不是在后端：后端只知道"怎么装"，不知道"该不该装"。
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (HasExplicitMenuBar)
        {
            return;
        }

        Backend.SetAppMenuBar(items);
    }

    /// <summary>
    /// 渲染引擎**内建**右键菜单的策略，可随时改（**下次右键**即生效，无需重建窗口）。
    /// 初始值来自 <see cref="OrielWindowOptions.ContextMenuPolicy"/>，默认
    /// <see cref="OrielContextMenuPolicy.Editing"/>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ShowContextMenu(IReadOnlyList{OrielMenuItem})"/> 无关：那是宿主自己构造并弹出的菜单，走另一条通道，
    /// 两者互不干涉（可以同时用）。
    /// </remarks>
    public OrielContextMenuPolicy ContextMenuPolicy
    {
        get => Backend.ContextMenuPolicy;
        set => Backend.ContextMenuPolicy = value;
    }

    internal IWindowBackend Backend => _backend ?? throw new InvalidOperationException(
        "窗口后端尚未初始化：请在 OrielAppBuilder.Run() 之后使用窗口能力。");

    /// <summary>
    /// 本窗口所属的应用，用来访问应用级能力（主题、单实例等）。
    /// </summary>
    /// <remarks>
    /// 窗口事件回调（如 <c>OrielAppBuilder.AddWindow</c> 的 <c>onCreated</c>）里拿不到自己创建的
    /// <see cref="OrielApp"/> 变量——那个赋值要等 <c>Build()/Run()</c> 返回。经窗口反查应用即可绕开
    /// 这个先有鸡还是先有蛋的问题。
    /// </remarks>
    public OrielApp App => Backend.App;

    /// <summary>
    /// 本窗口的 label（对标 Tauri 2 的 <c>Window.label</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在窗口后端创建时由 <see cref="OrielWindowOptions.Label"/> 定下，之后不变——它是寻址单位，
    /// 改名会让"按 label 找到的窗口"与"页面以为自己是谁"这两件事脱节。
    /// </para>
    /// <para>
    /// 窗口后端尚未就位（<c>Run()</c> 之前、或 <c>onCreated</c> 回调里）时读它返回 null，
    /// 而不是抛异常：那种时机读 label 属于"还没准备好"，不该让宿主在回调里崩掉。
    /// </para>
    /// </remarks>
    public string? Label => _backend?.Label;

    /// <summary>
    /// 本窗口绑定的数据存储标识符（B23）；<c>null</c> = 用默认存储。
    /// </summary>
    /// <remarks>
    /// 只有 macOS 真正生效（见 <see cref="OrielWindowOptions.DataStoreIdentifier"/>）。
    /// 记在窗口上是为了 <see cref="OrielApp.RemoveDataStore"/> 能回答"还有谁在用"——
    /// 删掉别人正在写的存储，结果会是"删了但立刻又被写回来"。
    /// </remarks>
    internal Guid? DataStoreIdentifier { get; set; }

    /// <summary>
    /// 关闭时的簿记：由 <see cref="OrielApp"/> 设置（把本窗口从应用窗口列表里摘掉），
    /// 在公共 <see cref="Closed"/> 事件**之前**执行。
    /// </summary>
    internal Action? ClosedBookkeeping { get; set; }

    internal void Attach(IWindowBackend backend)
    {
        lock (_gate)
        {
            _backend = backend;
        }
        backend.Loaded += () => Loaded?.Invoke();
        backend.Closing += args => Closing?.Invoke(args);
        backend.Closed += () =>
        {
            // 簿记在公共事件**之前**：否则用户在处理 Closed 时遍历 Windows，
            // 还会看到这个刚关闭的窗口（"关掉所有窗口"这类代码会因此重复关一个死窗口）。
            ClosedBookkeeping?.Invoke();
            Closed?.Invoke();
        };
        backend.TitleChanged += title => TitleChanged?.Invoke(title);
        backend.MaximizedChanged += maximized => MaximizedChanged?.Invoke(maximized);
        backend.Resized += args => Resized?.Invoke(args);
        backend.Moved += args => Moved?.Invoke(args);
        backend.FocusChanged += focused => FocusChanged?.Invoke(focused);
        backend.ScaleFactorChanged += scale => ScaleFactorChanged?.Invoke(scale);
        backend.NavigationStarting += url => NavigationStarting?.Invoke(url);
        backend.NavigationCompleted += args => NavigationCompleted?.Invoke(args);
        backend.ConsoleMessage += args => ConsoleMessage?.Invoke(args);
        backend.MessageReceived += args => MessageReceived?.Invoke(args);
        backend.ContextMenuItemClicked += id => ContextMenuItemClicked?.Invoke(id);
        backend.FileDropped += args => FileDropped?.Invoke(args);
        backend.DragDrop += args => DragDropReceived?.Invoke(args);

        // 补发一次初始几何。为什么必须：后端建窗是同步的（Windows 的 CreateWindowEx +
        // ShowWindow 期间 WM_SIZE/WM_MOVE 就到了），而本方法在那之后才挂转发——建窗期
        // 触发的 Resized/Moved 打在没有转发器的后端事件上，且后端的去重记账
        // （_lastReportedSize）已把那个尺寸记为"已上报"，之后同尺寸不再触发。
        // 于是"AddWindow(..., win => win.Resized += ...)"这种在创建前订阅的调用方
        // 永远等不到第一次通知。补发把语义钉成：**订阅方至少收到一次当前值**。
        // 只补几何两条：TitleChanged/Loaded 等其余事件要么创建期必然还没发生、
        // 要么本来就有自己的补推（主题在每次 NavigationCompleted 后补推）。
        try
        {
            OrielSize size = backend.InnerSize;
            Resized?.Invoke(new OrielSizeChangedEventArgs(size.Width, size.Height));

            OrielPosition position = backend.InnerPosition;
            Moved?.Invoke(new OrielPositionChangedEventArgs(position.X, position.Y));
        }
        catch (Exception ex)
        {
            // 补发是尽力而为：查询在个别后端/时机下失败，不该让建窗本身失败
            // （原始的建窗期丢失也只是"少一次通知"，这里不能比它更糟）。
            OrielDiagnostics.Report($"[OrielWeb] 补发初始几何失败（忽略）：{ex.Message}");
        }
    }

    // ---- 显示状态 ----

    public void Show() => Backend.Show();
    public void Hide() => Backend.Hide();
    public void Close() => Backend.Close();

    /// <summary>
    /// 强制销毁窗口：**绕过 <see cref="Closing"/> 与页面接管**（对标 Tauri 2 的 <c>destroy</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 页面侧实现"确认后关闭"用的就是它：收到 <c>close-requested</c>、用户点了「确认」，
    /// 调它真正关掉。若这里也能被 <see cref="Closing"/> 拦下，那条路会被宿主否决逻辑
    /// 无声地变成"关不掉"，而页面以为用户已经确认过了。
    /// </para>
    /// <para>
    /// 与 <see cref="Close"/> 的分工：<c>Close</c> 是"请求关闭"（可被拦），
    /// <c>Destroy</c> 是"就是这么办"（不可拦）。后者是逃生口，不要用它绕过
    /// 应用的未保存检查——那是 <see cref="Closing"/> 该管的事。
    /// </para>
    /// </remarks>
    public void Destroy() => Backend.Destroy();

    /// <summary>
    /// 是否让**页面**接管关闭。初始值来自 <see cref="OrielWindowOptions.InterceptClose"/>，运行期可改。
    /// </summary>
    /// <remarks>
    /// 页面常常只在"有未保存内容"时接管，所以它是个状态而不是常量。
    /// 完整语义（含"宿主 <see cref="Closing"/> 优先""加载完成前不生效"）见
    /// <see cref="OrielWindowOptions.InterceptClose"/>。
    /// </remarks>
    public bool CloseInterceptEnabled
    {
        get => Backend.CloseInterceptEnabled;
        set => Backend.CloseInterceptEnabled = value;
    }

    /// <summary>
    /// 哪几类宿主事件转发给页面（尺寸 / 位置 / 焦点）。默认 <see cref="OrielWindowEventKind.None"/>。
    /// </summary>
    /// <remarks>
    /// JS 包在页面 <c>listen</c> 到对应事件名时按需打开它。默认全关是因为尺寸与位置事件
    /// 在拖动时每个像素来一次（宿主侧已去重，但一次跨屏拖动仍有上千条），
    /// 而推给页面在 macOS/Linux 上是一次 JS 求值。完整理由见 <see cref="OrielWindowEventKind"/>。
    /// </remarks>
    public OrielWindowEventKind EventForwarding
    {
        get => Backend.EventForwarding;
        set => Backend.EventForwarding = value;
    }

    /// <summary>
    /// 窗口所在显示器的缩放比（1.0 = 100%）。
    /// </summary>
    /// <remarks>
    /// 页面用它把 <c>PhysicalSize</c> 换算成逻辑尺寸。取**窗口所在显示器**的值，
    /// 窗口被拖到另一块不同缩放的显示器后它会变——完整说明见
    /// <see cref="IWindowBackend.ScaleFactor"/>。
    /// </remarks>
    public double ScaleFactor => Backend.ScaleFactor;

    /// <summary>
    /// 本窗口所在的显示器（对标 Tauri 2 的 <c>currentMonitor()</c>）；平台报不出来时 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由平台按"相交面积最大"判定（Windows 的 <c>MonitorFromWindow</c>、macOS 的
    /// <c>-[NSWindow screen]</c>、Linux 的 <c>gdk_display_get_monitor_at_window</c>），
    /// 不是"窗口中心点落在哪块屏"——窗口拖到两块屏中间时这两种判法的答案会不同，
    /// 而用户与系统都按前者理解。
    /// </para>
    /// <para>
    /// 返回值的 <see cref="OrielMonitor.Position"/> / <see cref="OrielMonitor.Size"/> 是
    /// **物理像素**，与本窗口 <see cref="OuterPosition"/> / <see cref="OuterSize"/> 的逻辑像素不同。
    /// </para>
    /// </remarks>
    public OrielMonitor? CurrentMonitor => App.MonitorBackend.MonitorFromWindow(Backend.NativeWindowHandle);

    /// <summary>
    /// 当前窗口标题（对标 Tauri 2 的 <c>Window.title()</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="TitleChanged"/> **不是**同一件事：那个事件只在页面文档标题变化时触发，
    /// 程序调 <see cref="SetTitle"/> 不会触发它（宿主自己知道自己改了）。
    /// 要"标题现在是什么"读这个属性，别等事件——完整说明见 <see cref="IWindowBackend.Title"/>。
    /// </remarks>
    public string Title => Backend.Title;

    /// <summary>
    /// **客户区**尺寸（逻辑像素）——即 <see cref="Resize"/> 的逆运算。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Resize"/> / <see cref="OrielWindowOptions.Width"/> 同一单位（逻辑像素的客户区），
    /// 所以 <c>Resize(InnerSize)</c> 接近恒等操作。
    /// <para>
    /// 与 Tauri 的差别（如实标注）：Tauri 的 <c>innerSize()</c> 返回 <c>PhysicalSize</c>，
    /// 本库返回 <see cref="OrielSize"/>（逻辑像素）——**库对页面只暴露逻辑像素**，
    /// 混入物理像素会让"这个值是哪种单位"变成每个调用点都要想一遍的问题。
    /// 两个字段名（<c>Width</c> / <c>Height</c>）都还在，迁移代码取字段本身不受影响。
    /// </para>
    /// </remarks>
    public OrielSize InnerSize => Backend.InnerSize;

    /// <summary>
    /// **整窗**尺寸（逻辑像素，含边框与标题栏）。
    /// </summary>
    /// <remarks>
    /// Linux/Wayland 下可能与 <see cref="InnerSize"/> 相等：合成器不告诉客户端装饰尺寸。
    /// 完整说明见 <see cref="IWindowBackend.OuterSize"/>。
    /// </remarks>
    public OrielSize OuterSize => Backend.OuterSize;

    /// <summary>窗口**左上角**（含装饰）的屏幕逻辑坐标。</summary>
    public OrielPosition OuterPosition => Backend.OuterPosition;

    /// <summary>
    /// **客户区**左上角的屏幕逻辑坐标。
    /// </summary>
    /// <remarks>
    /// Linux 上目前恒等于 <see cref="OuterPosition"/>（GTK 不暴露装饰偏移）。
    /// 完整说明见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </remarks>
    public OrielPosition InnerPosition => Backend.InnerPosition;

    /// <summary>当前是否最小化（对标 Tauri 的 `Window.isMinimized()`）。</summary>
    /// <remarks>
    /// **与 <see cref="IsVisible"/> 是两件事**：最小化的窗口仍然"可见"，
    /// 它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了"。
    /// </remarks>
    public bool IsMinimized => Backend.IsMinimized;

    /// <summary>当前是否拥有键盘焦点（对标 Tauri 的 `Window.isFocused()`）。</summary>
    /// <remarks>按**窗口**判而不是按应用判：多窗口应用里只有一个是 key window。</remarks>
    public bool IsFocused => Backend.IsFocused;

    /// <summary>
    /// 当前是否全屏（对标 Tauri 的 `Window.isFullscreen()`）。
    /// </summary>
    /// <remarks>
    /// 取的是 <c>ToggleFullscreen()</c> 记住的状态，不是去比对窗口与显示器矩形——
    /// 后者在用户手动把窗口拖成刚好铺满屏幕时会给出与库不一致的答案。
    /// </remarks>
    public bool IsFullscreen => Backend.IsFullscreen;

    /// <summary>当前是否置顶（对标 Tauri 的 `Window.isAlwaysOnTop()`）。</summary>
    public bool IsAlwaysOnTop => Backend.IsAlwaysOnTop;

    /// <summary>当前是否有系统装饰（对标 Tauri 的 `Window.isDecorated()`）。</summary>
    /// <remarks>
    /// 取创建时的选项（`Frameless` 的取反）：它在窗口生命期内不变。
    /// </remarks>
    public bool IsDecorated => Backend.IsDecorated;

    /// <summary>当前是否可缩放（对标 Tauri 的 `Window.isResizable()`）。</summary>
    public bool IsResizable => Backend.IsResizable;

    /// <summary>
    /// 当前是否可最大化（对标 Tauri 的 `Window.isMaximizable()`）。
    /// </summary>
    /// <remarks>
    /// **Linux 与 macOS 上等于 <see cref="IsResizable"/>**：那两个平台没有独立的
    /// "可最大化"概念（GTK 跟着 resizable 走、AppKit 的 zoom 以 resizable 为前提）。
    /// Windows 上是独立的样式位。
    /// </remarks>
    public bool IsMaximizable => Backend.IsMaximizable;

    /// <summary>当前是否可最小化（对标 Tauri 的 `Window.isMinimizable()`）。</summary>
    public bool IsMinimizable => Backend.IsMinimizable;

    /// <summary>
    /// 当前是否可关闭（对标 Tauri 的 `Window.isClosable()`）。
    /// </summary>
    /// <remarks>**Linux 上恒为 `true`**（GTK 没有这个概念），见后端接口的说明。</remarks>
    public bool IsClosable => Backend.IsClosable;

    /// <summary>当前是否接收输入（对标 Tauri 的 `Window.isEnabled()`）。</summary>
    /// <remarks>macOS 上只挡鼠标、不挡键盘，见后端接口的说明。</remarks>
    public bool IsEnabled => Backend.IsEnabled;

    /// <summary>
    /// 启用/禁用窗口的输入（对标 Tauri 的 `Window.setEnabled()`）。
    /// </summary>
    public void SetEnabled(bool enabled) => Backend.SetEnabled(enabled);

    /// <summary>
    /// 这个窗口的主题覆写（<c>window.setTheme</c>）。<c>null</c> = 跟随系统。
    /// </summary>
    /// <remarks>
    /// 记住当前覆写，供 <c>window.theme</c> 按"这个窗口显示成什么"回答；
    /// 真正的应用动作（三平台的原生落点）由 <see cref="IWindowBackend.SetTheme"/> 承担。
    /// 平台差异与"系统主题不被覆写搅混"的说明见 <see cref="OrielApp.SetTheme"/>。
    /// </remarks>
    public OrielTheme? ThemeOverride
    {
        get => _themeOverride;
        set
        {
            _themeOverride = value;
            Backend.SetTheme(value);
        }
    }

    private OrielTheme? _themeOverride;

    /// <summary>
    /// 设置这个窗口的缩放倍率（对标 Tauri 2 的 <c>Webview.setZoom</c>）。
    /// </summary>
    /// <remarks><c>1.0</c> = 100%。三平台语义一致，见 <see cref="IWindowBackend.SetZoom"/>。
    /// 有效性（有限正数）由 <c>window.setZoom</c> 命令层校验。</remarks>
    public void SetZoom(double scale) => Backend.SetZoom(scale);

    /// <summary>
    /// 清除本应用 profile 的全部浏览数据（对标 Tauri 2 的
    /// <c>Webview.clearAllBrowsingData</c>）。
    /// </summary>
    /// <remarks>
    /// 返回时清除已**发起**（三平台的原生 API 都是异步的，完成只进诊断日志），
    /// 见 <see cref="IWindowBackend.ClearAllBrowsingData"/>。
    /// 需要能力声明（<c>window.clearAllBrowsingData</c>）——它销毁登录态与本地存储，
    /// 不该被一个被注入的页面随手调用。
    /// </remarks>
    public void ClearAllBrowsingData() => Backend.ClearAllBrowsingData();

    /// <summary>
    /// 只读的覆写值（<see cref="IOrielWindowControl"/> 需要，但公开 API 上不给 setter——
    /// 外部只能经 <see cref="ThemeOverride"/> 改，那是有意的）。
    /// </summary>
    OrielTheme? IOrielWindowControl.ThemeOverride => _themeOverride;

    /// <summary>
    /// 设置窗口最大尺寸（逻辑像素，对标 Tauri 的 `Window.setMaxSize()`）。传 0 表示不限制。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SetMinSize"/> 是同一套机制的两端：Windows 在 <c>WM_GETMINMAXINFO</c>
    /// 里同时处理、Cocoa 是 <c>setContentMaxSize:</c>、GTK 走几何提示（只声明 max）。
    /// </remarks>
    public void SetMaxSize(int width, int height) => Backend.SetMaxSize(width, height);

    // ---- 光标（B21）----

    /// <summary>把光标限制在窗口内（对标 Tauri 的 <c>Window.setCursorGrab()</c>）。</summary>
    /// <remarks>
    /// <b>Linux 不支持</b>（抛 <see cref="NotSupportedException"/>）；macOS 的形态是
    /// "把光标锁死"而不是"限制在矩形内"。见 <see cref="IWindowBackend.SetCursorGrab"/>。
    /// </remarks>
    public void SetCursorGrab(bool grab) => Backend.SetCursorGrab(grab);

    /// <summary>显示/隐藏光标（对标 Tauri 的 <c>Window.setCursorVisible()</c>）。</summary>
    public void SetCursorVisible(bool visible) => Backend.SetCursorVisible(visible);

    /// <summary>设置光标形状（对标 Tauri 的 <c>Window.setCursorIcon()</c>）。</summary>
    /// <remarks>设置后页面的 CSS <c>cursor</c> 不再生效，见 <see cref="IWindowBackend.SetCursorIcon"/>。</remarks>
    public void SetCursorIcon(OrielCursorIcon icon) => Backend.SetCursorIcon(icon);

    /// <summary>把光标移到客户区内的某个位置（逻辑像素；对标 Tauri 的 <c>Window.setCursorPosition()</c>）。</summary>
    public void SetCursorPosition(double x, double y) => Backend.SetCursorPosition(x, y);

    /// <summary>设置窗口是否忽略鼠标事件（对标 Tauri 的 <c>Window.setIgnoreCursorEvents()</c>）。</summary>
    public void SetIgnoreCursorEvents(bool ignore) => Backend.SetIgnoreCursorEvents(ignore);

    /// <summary>从窗口某条边/角开始一次 resize 拖动（对标 Tauri 的 <c>Window.startResizeDragging()</c>）。</summary>
    /// <remarks>
    /// <b>macOS 不支持</b>；Windows/Linux 会进入模态循环，必须在鼠标按下期间调用。
    /// 见 <see cref="IWindowBackend.StartResizeDragging"/>。
    /// </remarks>
    public void StartResizeDragging(OrielResizeDirection direction) => Backend.StartResizeDragging(direction);

    // ---- 平台开关 / 特效 / 任务栏（B22）----
    //
    // 十九条全部转给后端；支持矩阵在 IPlatformBackend 的 B22 注释块里，
    // 逐个方法的平台事实在各后端实现的 <remarks> 上。这里只做转发与一句话说明。

    /// <summary>是否在任务栏里隐藏这个窗口（对标 Tauri 的 <c>Window.setSkipTaskbar()</c>）。</summary>
    /// <remarks><b>macOS 不支持</b>（Dock 里的是整个应用）。见 <see cref="IWindowBackend.SetSkipTaskbar"/>。</remarks>
    public void SetSkipTaskbar(bool skip) => Backend.SetSkipTaskbar(skip);

    /// <summary>是否在所有工作区可见（对标 Tauri 的 <c>Window.setVisibleOnAllWorkspaces()</c>）。</summary>
    /// <remarks><b>Windows 不支持</b>。见 <see cref="IWindowBackend.SetVisibleOnAllWorkspaces"/>。</remarks>
    public void SetVisibleOnAllWorkspaces(bool visible) => Backend.SetVisibleOnAllWorkspaces(visible);

    /// <summary>是否阻止内容被截屏（对标 Tauri 的 <c>Window.setContentProtected()</c>）。</summary>
    /// <remarks><b>Linux 不支持</b>。见 <see cref="IWindowBackend.SetContentProtected"/>。</remarks>
    public void SetContentProtected(bool contentProtected) => Backend.SetContentProtected(contentProtected);

    /// <summary>简化全屏（对标 Tauri 的 <c>Window.setSimpleFullscreen()</c>）。</summary>
    /// <remarks>只有 macOS 有独立语义，另两个平台等同 <see cref="SetFullscreen"/>。</remarks>
    public void SetSimpleFullscreen(bool fullscreen) => Backend.SetSimpleFullscreen(fullscreen);

    /// <summary>在包含给定屏幕物理坐标的显示器上全屏（对标 Tauri 的 <c>Window.setFullscreenOnMonitor()</c>）。</summary>
    /// <remarks>坐标不在任何显示器上时什么都不做（与 Tauri 一致）。</remarks>
    public void SetFullscreenOnMonitor(int x, int y) => Backend.SetFullscreenOnMonitor(x, y);

    /// <summary>标题栏样式（对标 Tauri 的 <c>Window.setTitleBarStyle()</c>）。</summary>
    /// <remarks><b>只有 macOS 实现</b>。见 <see cref="IWindowBackend.SetTitleBarStyle"/>。</remarks>
    public void SetTitleBarStyle(OrielTitleBarStyle style) => Backend.SetTitleBarStyle(style);

    /// <summary>是否给窗口画阴影（对标 Tauri 的 <c>Window.setShadow()</c>）。</summary>
    /// <remarks><b>Linux 不支持</b>。见 <see cref="IWindowBackend.SetShadow"/>。</remarks>
    public void SetShadow(bool enable) => Backend.SetShadow(enable);

    /// <summary>是否永远压在别的窗口之下（对标 Tauri 的 <c>Window.setAlwaysOnBottom()</c>）。</summary>
    public void SetAlwaysOnBottom(bool alwaysOnBottom) => Backend.SetAlwaysOnBottom(alwaysOnBottom);

    /// <summary>窗口能否获得焦点（对标 Tauri 的 <c>Window.setFocusable()</c>）。</summary>
    /// <remarks><b>macOS 不支持</b>（只有私有 API）。见 <see cref="IWindowBackend.SetFocusable"/>。</remarks>
    public void SetFocusable(bool focusable) => Backend.SetFocusable(focusable);

    /// <summary>是否显示系统装饰（对标 Tauri 的 <c>Window.setDecorations()</c>）。</summary>
    /// <remarks>运行期切换，与创建期的 <see cref="OrielWindowOptions.Frameless"/> 无关。</remarks>
    public void SetDecorations(bool decorations) => Backend.SetDecorations(decorations);

    /// <summary>应用窗口材质（对标 Tauri 的 <c>Window.setEffects()</c>）。</summary>
    /// <remarks>
    /// 第一个本平台支持的材质生效，其余忽略；**一个都不支持时抛
    /// <see cref="NotSupportedException"/>**（Linux 上整条命令都会抛）。
    /// </remarks>
    public void SetEffects(OrielEffects effects) => Backend.SetEffects(effects);

    /// <summary>清除已应用的材质（对标 Tauri 的 <c>Window.clearEffects()</c>）。</summary>
    public void ClearEffects() => Backend.ClearEffects();

    /// <summary>任务栏进度条（对标 Tauri 的 <c>Window.setProgressBar()</c>）。</summary>
    /// <remarks><b>只有 Windows 实现</b>。见 <see cref="IWindowBackend.SetProgressBar"/>。</remarks>
    public void SetProgressBar(OrielProgressBarStatus status, double? progress)
        => Backend.SetProgressBar(status, progress);

    /// <summary>任务栏图标上的角标覆盖图（对标 Tauri 的 <c>Window.setOverlayIcon()</c>）。</summary>
    /// <remarks><b>只有 Windows 实现</b>；<c>null</c> 表示清除。</remarks>
    public void SetOverlayIcon(OrielImage? icon) => Backend.SetOverlayIcon(icon);

    /// <summary>Dock 徽标数字（对标 Tauri 的 <c>Window.setBadgeCount()</c>）。</summary>
    /// <remarks><b>只有 macOS 实现</b>（应用级）。</remarks>
    public void SetBadgeCount(int? count) => Backend.SetBadgeCount(count);

    /// <summary>Dock 徽标短文本（对标 Tauri 的 <c>Window.setBadgeLabel()</c>）。</summary>
    /// <remarks><b>只有 macOS 实现</b>（应用级）。</remarks>
    public void SetBadgeLabel(string? label) => Backend.SetBadgeLabel(label);

    /// <summary>
    /// 请求用户注意（对标 Tauri 的 `Window.requestUserAttention()`）。
    /// </summary>
    /// <param name="type"><c>null</c> 表示取消已发出的请求。</param>
    /// <remarks>Linux 上 Critical 与 Informational 没有区别（GTK 只有开/关）。</remarks>
    public void RequestUserAttention(OrielUserAttention? type) => Backend.RequestUserAttention(type);

    /// <summary>设置是否可最大化（对标 Tauri 的 `Window.setMaximizable()`）。</summary>
    /// <remarks>
    /// <b>macOS 上与 <see cref="SetResizable"/> 是同一个操作</b>（AppKit 只有
    /// resizable 位，没有独立的最大化位），<b>Linux 上是空操作</b>（GTK 无对应入口）。
    /// </remarks>
    public void SetMaximizable(bool enabled) => Backend.SetMaximizable(enabled);

    /// <summary>设置是否可最小化（对标 Tauri 的 `Window.setMinimizable()`）。</summary>
    /// <remarks>Linux 上是空操作，理由见 <see cref="SetMaximizable"/>。</remarks>
    public void SetMinimizable(bool enabled) => Backend.SetMinimizable(enabled);

    /// <summary>设置是否可关闭（对标 Tauri 的 `Window.setClosable()`）。</summary>
    /// <remarks>
    /// <b>三平台落点不同</b>：Windows 灰化系统菜单的"关闭"项、macOS 切
    /// <c>NSWindowStyleMaskClosable</c>、<b>Linux 空操作</b>（要"关不掉"得拦
    /// close-requested 事件）。见 <see cref="IWindowBackend.SetClosable"/>。
    /// </remarks>
    public void SetClosable(bool enabled) => Backend.SetClosable(enabled);

    /// <summary>设置窗口图标（对标 Tauri 的 `Window.setIcon()`）。</summary>
    /// <param name="path">图像文件路径（PNG / ICO）。</param>
    /// <remarks>
    /// **macOS 上没有窗口级图标**——那里设的是应用（Dock）图标，会影响整个应用。
    /// Linux 上 Wayland 通常忽略窗口图标（由合成器决定）。见
    /// <see cref="IWindowBackend.SetIcon"/>。
    /// </remarks>
    public void SetIcon(string path) => Backend.SetIcon(path);

    /// <summary>从内存里的图像字节设置窗口图标（对标 Tauri 的 `setIcon` 收字节形式）。</summary>
    /// <param name="bytes">PNG 或 ICO 的原始字节。</param>
    /// <remarks>落点与平台差异同 <see cref="SetIcon"/>。见 <see cref="IWindowBackend.SetIconFromBytes"/>。</remarks>
    public void SetIconFromBytes(byte[] bytes) => Backend.SetIconFromBytes(bytes);

    /// <summary>
    /// 从**裸 RGBA** 设置窗口图标（`Image.new(rgba, w, h)` 那条路，不需要先编码成 PNG）。
    /// </summary>
    /// <param name="rgba">行优先、自上而下的 RGBA，长度必须正好 <paramref name="width"/> * <paramref name="height"/> * 4。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <remarks>落点与平台差异同 <see cref="SetIcon"/>。见 <see cref="IWindowBackend.SetIconFromRgba"/>。</remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
        => Backend.SetIconFromRgba(rgba, width, height);

    /// <summary>设置窗口底色（对标 Tauri 的 `Window.setBackgroundColor()`）。</summary>
    /// <param name="argb"><c>0xAARRGGBB</c>。</param>
    /// <remarks>
    /// 这是"页面透明、或内核还没加载完时露出来"的颜色。三平台都落到 webview 自己的底色，
    /// 语义一致。见 <see cref="IWindowBackend.SetBackgroundColor"/>。
    /// </remarks>
    public void SetBackgroundColor(uint argb) => Backend.SetBackgroundColor(argb);

    public void Focus() => Backend.Focus();
    public void Maximize() => Backend.Maximize();
    public void Minimize() => Backend.Minimize();
    public void Restore() => Backend.Restore();
    public void SetFullscreen(bool enabled) => Backend.SetFullscreen(enabled);
    public void SetOnTop(bool enabled) => Backend.SetOnTop(enabled);
    public void SetTitle(string title) => Backend.SetTitle(title);
    public void SetResizable(bool enabled) => Backend.SetResizable(enabled);
    public void SetMinSize(int width, int height) => Backend.SetMinSize(width, height);
    public void MoveTo(int x, int y) => Backend.MoveTo(x, y);
    public void Resize(int width, int height) => Backend.Resize(width, height);
    public void Center() => Backend.Center();

    /// <summary>开始窗口拖动（无边框场景：页面在 mousedown 时调用，进入系统标题栏拖动循环，松开鼠标返回）。</summary>
    public void BeginDrag() => Backend.BeginDrag();

    /// <summary>
    /// 流式拖动起始（macOS 路径；Windows 上为 no-op）。
    /// px/py = 指针屏幕坐标（CSS 点）；wx/wy = 窗口左上角屏幕坐标；ww/wh = 窗口尺寸；sh = 屏高。
    /// </summary>
    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
        => Backend.BeginDragStreaming(px, py, wx, wy, ww, wh, sh);

    /// <summary>流式拖动增量（CSS 点，屏幕 y 向下为正）。</summary>
    public void DragTo(double dx, double dy) => Backend.DragTo(dx, dy);

    /// <summary>流式拖动结束。</summary>
    public void EndDrag() => Backend.EndDrag();

    /// <summary>当前是否最大化。</summary>
    public bool IsMaximized => Backend.IsMaximized;

    /// <summary>
    /// 窗口当前是否"显示中"（<see cref="Hide"/> 之后为 false；**最小化仍算显示中**）。
    /// </summary>
    /// <remarks>
    /// 三平台语义一致。存在的理由：Windows 的组合宿主下，"隐藏"曾只 <c>SW_HIDE</c> 宿主、漏掉
    /// WebView2 的顶层渲染窗口，宿主自身看起来已隐藏、实际仍浮在桌面上吃点击——只靠窗口句柄
    /// 判断不出这一点（<c>Process.MainWindowHandle</c> 已为 0，但残余窗口仍在）。有了它，
    /// 宿主可以在 <see cref="Hide"/> 之后自查"是不是真藏住了"，而不必从外部用 <c>WindowFromPoint</c> 探测。
    /// </remarks>
    public bool IsVisible => Backend.IsVisible;

    /// <summary>最大化 / 还原切换，返回切换后是否最大化。</summary>
    public bool ToggleMaximize() => Backend.ToggleMaximize();
    /// <summary>全屏切换，返回切换后是否全屏。</summary>
    public bool ToggleFullscreen() => Backend.ToggleFullscreen();
    /// <summary>窗口置顶切换，返回切换后是否置顶。</summary>
    public bool ToggleOnTop() => Backend.ToggleOnTop();

    /// <summary>是否有可后退的历史记录。</summary>
    public bool CanGoBack => Backend.CanGoBack;

    /// <summary>是否有可前进的历史记录。</summary>
    public bool CanGoForward => Backend.CanGoForward;

    /// <summary>后退一页（无历史时为空操作）。</summary>
    public void GoBack() => Backend.GoBack();

    /// <summary>前进一页（无历史时为空操作）。</summary>
    public void GoForward() => Backend.GoForward();

    /// <summary>重新加载当前页面（会重新触发导航事件与 <see cref="Loaded"/> 之外的导航流程）。</summary>
    public void Reload() => Backend.Reload();

    /// <summary>
    /// 把动作切回 UI 线程执行（已在 UI 线程则直接执行）。
    /// </summary>
    /// <remarks>
    /// 异步编排时必备：<c>await</c> 之后的续体不在 UI 线程上，而 GTK（Linux）与 AppKit（macOS）
    /// 只允许在各自的主线程调用窗口 API，从线程池调用会直接崩溃。典型用法：
    /// <code>
    /// await window.EvaluateJs("...");
    /// window.PostToUiThread(() => window.SetTitle("完成"));   // 回到 UI 线程再碰窗口
    /// </code>
    /// <b>投递失败时不再静默丢弃</b>：返回 <c>false</c> 表示调度通道已失效（应用正在退出、
    /// 消息窗口已销毁），回调不会执行，同时 <see cref="OrielDiagnostics.Reported"/> 会有一条记录。
    /// 需要"等它做完"用 <see cref="InvokeOnUiThread"/>。
    /// </remarks>
    /// <returns>是否已成功投递到 UI 线程队列。</returns>
    public bool PostToUiThread(Action action) => Backend.PostToUiThread(action);

    /// <summary>当前线程是不是 UI 线程；见 <see cref="OrielApp.IsOnUiThread"/>。</summary>
    public bool IsOnUiThread => Backend.IsOnUiThread();

    /// <summary>
    /// 在 UI 线程上**同步**执行动作：已在 UI 线程则直接执行，否则投递过去并阻塞等待完成。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="PostToUiThread"/> 的区别是"等不等"，语义与死锁注意事项见
    /// <see cref="OrielApp.InvokeOnUiThread"/>（同一套实现）——**退出已经开始时会立刻失败**，
    /// 因为消息循环停下后投递的动作不会被处理，干等就是永久阻塞。
    /// </para>
    /// </remarks>
    /// <param name="action">要在 UI 线程上执行的动作。</param>
    /// <param name="timeout">
    /// 等待上限；<c>null</c>（默认）表示无限等待。超时抛 <see cref="TimeoutException"/>，
    /// 语义同 <see cref="OrielApp.InvokeOnUiThread"/>。
    /// </param>
    public void InvokeOnUiThread(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Backend.IsOnUiThread())
        {
            action();
            return;
        }

        if (App.IsExiting)
        {
            throw new InvalidOperationException(
                "应用正在退出：消息循环已停止，InvokeOnUiThread 投递的动作不会被处理。" +
                "退出途中请只做与 UI 无关的收尾，或用 PostToUiThread 做尽力投递。");
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        if (!Backend.PostToUiThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            }))
        {
            throw new InvalidOperationException(
                "InvokeOnUiThread 失败：调度通道已失效（应用正在退出或窗口已销毁）。");
        }

        if (!done.Wait(timeout ?? Timeout.InfiniteTimeSpan))
        {
            throw new TimeoutException(
                $"InvokeOnUiThread 在 {timeout} 内没有完成：UI 线程可能被长任务占用" +
                "（一条慢命令、一个不返回的模态原生调用）。");
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>向页面推送一个自定义事件（页面侧 <c>window.oriel.on(name, handler)</c> 接收）。</summary>
    /// <param name="name">事件名。</param>
    /// <param name="jsonPayload">
    /// payload 的 <b>JSON 文本</b>，例如 <c>{"count":3}</c>、<c>"hi"</c>、<c>null</c>。
    /// 库只负责投递，不解析也不改写——想要"传对象"就用下面那个带 <see cref="JsonTypeInfo{T}"/> 的重载。
    /// </param>
    public void EmitEvent(string name, string jsonPayload) => Backend.EmitEvent(name, jsonPayload);

    /// <summary>
    /// 向页面推送一个自定义事件，payload 用给定的 STJ 类型信息序列化。
    /// 走 <see cref="JsonTypeInfo{T}"/>（通常来自源生成上下文）而非反射，因此在 Native AOT 下安全。
    /// </summary>
    public void EmitEvent<T>(string name, T payload, JsonTypeInfo<T> typeInfo)
        => Backend.EmitEvent(name, JsonSerializer.Serialize(payload, typeInfo));

    /// <summary>在页面当前文档上下文中执行 JS。返回 WebView2 风格的 JSON 编码结果字符串。</summary>
    public Task<string> EvaluateJs(string script) => Backend.ExecuteScriptAsync(script);

    // ---- 对话框（pywebview 基本面）----

    /// <summary>消息框（模态，阻塞到用户关闭）。</summary>
    public void ShowMessage(string text, string? title = null, OrielMessageBoxIcon icon = OrielMessageBoxIcon.Info)
        => Backend.ShowMessageBox(text, title, icon);

    /// <summary>
    /// 询问框（模态）：返回用户点了哪个语义按钮，或 <c>null</c>（被外部关掉）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ShowMessage"/> 的分工：那个只"告诉用户一件事"（返回 void），
    /// 这个要"问用户一件事"（要知道点了哪个）。两者共用底下的原生对话框。
    /// </remarks>
    public MessageResult? ShowDialog(
        string message,
        string? title = null,
        string kind = "info",
        MessageButtons buttons = MessageButtons.Ok,
        string okLabel = "确定",
        string cancelLabel = "取消")
        => Backend.ShowMessageDialog(message, title, kind, buttons, okLabel, cancelLabel);

    /// <summary>
    /// <see cref="IOrielWindowControl"/> 上的显式实现：转发到 <see cref="ShowDialog"/>。
    /// </summary>
    /// <remarks>
    /// 显式实现而不是把 <see cref="ShowDialog"/> 直接叫 <c>ShowMessageDialog</c>：
    /// 公开 API 那边叫 <c>ShowDialog</c> 更好读（它是个询问框，不是"显示一个消息"），
    /// 而接口上的名字要表达"这是消息对话框家族的一员"。两个名字各自贴自己的语境。
    /// </remarks>
    MessageResult? IOrielWindowControl.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
        => ShowDialog(message, title, kind, buttons, okLabel, cancelLabel);

    /// <summary>
    /// 打开文件对话框（可用 <see cref="OrielOpenFileDialogOptions.AllowMultiple"/> 多选）。
    /// 取消返回**空数组**。
    /// </summary>
    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Backend.ShowOpenFileDialog(options);
    }

    /// <summary>
    /// 打开单个文件。取消返回 null。
    /// filter 形如 <c>"文本文件|*.txt;*.md|所有文件|*.*"</c>（等价于
    /// <see cref="OrielFileFilter.Parse"/> 之后走选项重载）。
    /// </summary>
    public string? ShowOpenFileDialog(string? title = null, string? filter = null, string? initialDirectory = null)
    {
        string[] paths = Backend.ShowOpenFileDialog(new OrielOpenFileDialogOptions
        {
            Title = title,
            Filters = OrielFileFilter.Parse(filter),
            InitialDirectory = initialDirectory,
        });

        return paths.Length > 0 ? paths[0] : null;
    }

    /// <summary>保存文件对话框（结构化过滤器）。取消返回 null。</summary>
    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Backend.ShowSaveFileDialog(options);
    }

    /// <summary>保存文件对话框（字符串过滤器，旧写法）。取消返回 null。</summary>
    public string? ShowSaveFileDialog(string? title = null, string? filter = null, string? defaultExtension = null)
        => Backend.ShowSaveFileDialog(new OrielSaveFileDialogOptions
        {
            Title = title,
            Filters = OrielFileFilter.Parse(filter),
            DefaultExtension = defaultExtension,
        });

    /// <summary>选择文件夹。取消返回 null。</summary>
    public string? ShowFolderDialog(string? title = null, string? initialDirectory = null)
        => Backend.ShowFolderDialog(title, initialDirectory);
}
