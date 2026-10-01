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
    /// 在鼠标位置弹出上下文菜单（项里的 <see cref="OrielMenuItem.Role"/> 走平台语义，
    /// 自定义项在用户选择后触发 <see cref="ContextMenuItemClicked"/>）。
    /// </summary>
    /// <remarks>
    /// <b>Windows 上这个调用会阻塞</b>，直到用户选择或取消——原生弹出菜单自带模态消息循环。
    /// macOS 与 Linux 是异步弹出（调用立即返回，之后事件才来）。
    /// 因此不要从"需要立刻继续"的路径里调用它（例如页面命令处理里同步等结果）；三平台的共同保证只有
    /// "用户选中自定义项后会触发事件"，没有"调用返回时用户已选完"。
    /// </remarks>
    public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        Backend.ShowContextMenu(items);
    }

    /// <summary>
    /// 渲染引擎**内建**右键菜单的策略，可随时改（**下次右键**即生效，无需重建窗口）。
    /// 初始值来自 <see cref="OrielWindowOptions.ContextMenuPolicy"/>，默认
    /// <see cref="OrielContextMenuPolicy.Editing"/>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ShowContextMenu"/> 无关：那是宿主自己构造并弹出的菜单，走另一条通道，
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
        backend.NavigationStarting += url => NavigationStarting?.Invoke(url);
        backend.NavigationCompleted += args => NavigationCompleted?.Invoke(args);
        backend.ConsoleMessage += args => ConsoleMessage?.Invoke(args);
        backend.MessageReceived += args => MessageReceived?.Invoke(args);
        backend.ContextMenuItemClicked += id => ContextMenuItemClicked?.Invoke(id);
        backend.FileDropped += args => FileDropped?.Invoke(args);
    }

    // ---- 显示状态 ----

    public void Show() => Backend.Show();
    public void Hide() => Backend.Hide();
    public void Close() => Backend.Close();
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

    // ---- 剪贴板 ----

    /// <summary>剪贴板文本；没有文本时返回 null。</summary>
    public string? ClipboardText => Backend.GetClipboardText();

    /// <summary>写入剪贴板文本（替换现有内容）。</summary>
    public void SetClipboardText(string text) => Backend.SetClipboardText(text);

    /// <summary>剪贴板 HTML；没有 HTML 时返回 null。</summary>
    public string? ClipboardHtml => Backend.GetClipboardHtml();

    /// <summary>
    /// 写入剪贴板 HTML；同时写一份纯文本回退（<paramref name="plainTextFallback"/> 为 null 时用 HTML 本身），
    /// 这样只认文本的应用也能粘贴到内容。
    /// </summary>
    public void SetClipboardHtml(string html, string? plainTextFallback = null)
        => Backend.SetClipboardHtml(html, plainTextFallback);

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
    /// </remarks>
    public void PostToUiThread(Action action) => Backend.PostToUiThread(action);

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
