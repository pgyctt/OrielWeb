namespace OrielWeb;

/// <summary>窗口平台后端契约（OrielWeb 内部）。macOS/Linux 里程碑分别实现。</summary>
internal interface IWindowBackend
{
    IntPtr NativeWindowHandle { get; }

    /// <summary>本窗口所属的应用（窗口创建时注入）。</summary>
    OrielApp App { get; }

    event Action? Loaded;
    event Action<OrielCloseRequestEventArgs>? Closing;
    event Action? Closed;
    event Action<string>? TitleChanged;
    /// <summary>窗口最大化状态变化（用户拖边框最大化、双击标题栏等原生路径也会触发）。</summary>
    event Action<bool>? MaximizedChanged;
    /// <summary>导航开始（新文档开始加载）；参数为即将加载的 URL（平台未提供时为空串）。</summary>
    event Action<string>? NavigationStarting;
    /// <summary>导航完成——成功与失败都走这里，失败时带错误信息。页面内跳转与整页刷新同样触发。</summary>
    event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted;
    /// <summary>页面 console 输出（需窗口选项打开 console 转发）。</summary>
    event Action<OrielConsoleMessageEventArgs>? ConsoleMessage;
    /// <summary>页面经 <c>oriel.postMessage(name, payload)</c> 发来的消息。</summary>
    event Action<OrielMessageReceivedEventArgs>? MessageReceived;

    /// <summary>当前是否处于最大化状态。</summary>
    bool IsMaximized { get; }

    void Show();
    void Hide();
    void Close();
    void Focus();
    void Maximize();
    void Minimize();
    void Restore();
    void SetFullscreen(bool enabled);
    void SetOnTop(bool enabled);
    void SetTitle(string title);
    void SetResizable(bool enabled);
    void SetMinSize(int width, int height);
    void MoveTo(int x, int y);
    void Resize(int width, int height);
    void Center();

    /// <summary>开始窗口拖动（无边框场景：在页面上按下并调用，进入系统标题栏拖动循环）。</summary>
    void BeginDrag();
    /// <summary>
    /// 流式拖动起始：记录指针起点与窗口 cocoa 原点（macOS 用；Windows 原生拖动为 no-op）。
    /// px/py = 指针屏幕坐标（CSS 点）；wx/wy = 窗口左上角屏幕坐标（CSS 点）；
    /// ww/wh = 窗口尺寸；sh = 窗口所在屏高。
    /// </summary>
    void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh);
    /// <summary>流式拖动增量（CSS 点；屏幕 y 向下为正）。</summary>
    void DragTo(double dx, double dy);
    /// <summary>流式拖动结束。</summary>
    void EndDrag();
    /// <summary>最大化/还原切换，返回切换后是否最大化。</summary>
    bool ToggleMaximize();
    /// <summary>全屏切换，返回切换后是否全屏。</summary>
    bool ToggleFullscreen();
    /// <summary>置顶切换，返回切换后是否置顶。</summary>
    bool ToggleOnTop();

    /// <summary>是否有可后退的历史记录。</summary>
    bool CanGoBack { get; }
    /// <summary>是否有可前进的历史记录。</summary>
    bool CanGoForward { get; }
    /// <summary>后退一页（无历史时为空操作）。</summary>
    void GoBack();
    /// <summary>前进一页（无历史时为空操作）。</summary>
    void GoForward();
    /// <summary>重新加载当前页面。</summary>
    void Reload();

    /// <summary>读剪贴板文本；没有文本时返回 null。</summary>
    string? GetClipboardText();
    /// <summary>写剪贴板文本（替换现有内容）。</summary>
    void SetClipboardText(string text);
    /// <summary>读剪贴板 HTML；没有 HTML 时返回 null。</summary>
    string? GetClipboardHtml();
    /// <summary>写剪贴板 HTML，并同时写一份纯文本回退（供只认文本的应用粘贴）。</summary>
    void SetClipboardHtml(string html, string? plainTextFallback);

    /// <summary>
    /// 把动作切回 UI 线程执行（已在 UI 线程则直接执行）。
    /// 用于跨 <c>await</c> 之后碰窗口：await 的续体会落到线程池，而 GTK/AppKit 只能在各自的主线程调用。
    /// </summary>
    void PostToUiThread(Action action);

    /// <summary>向页面推送自定义事件（页面侧 <c>oriel.on(name, …)</c> 接收）；jsonPayload 必须是合法 JSON 文本。</summary>
    void EmitEvent(string name, string jsonPayload);

    Task<string> ExecuteScriptAsync(string script);
    void PostMessageAsJson(string json);

    string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory);
    string? ShowSaveFileDialog(string? title, string? filter, string? defaultExtension);
    void ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon);
}

/// <summary>平台后端契约（消息循环 + 主线程调度 + 窗口工厂）。</summary>
internal interface IPlatformBackend : IDisposable
{
    /// <summary>当前系统主题。</summary>
    OrielTheme CurrentTheme { get; }

    /// <summary>系统主题变化（用户切换深/浅色时触发；检测不到变化通道的平台不会触发）。</summary>
    event Action<OrielTheme>? ThemeChanged;

    void RunMessageLoop();
    void Quit();
    /// <summary>把动作切回 UI 线程执行（已在 UI 线程则直接执行）。</summary>
    void PostToMainThread(Action action);
    IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory);
}
