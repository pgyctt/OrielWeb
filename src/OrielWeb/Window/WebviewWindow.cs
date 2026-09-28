namespace OrielWeb;

/// <summary>Closing 事件的参数；把 <see cref="Cancel"/> 置为 true 可阻止窗口关闭。</summary>
public sealed class OrielCloseRequestEventArgs
{
    public bool Cancel { get; set; }
}

public enum OrielMessageBoxIcon { Info, Warning, Error, Question }

/// <summary>
/// 窗口公共门面。生命周期事件必须在 <c>OrielAppBuilder.Run()</c> 之前订阅。
/// 窗口方法（全屏/置顶/移动/对话框等）对齐 pywebview 基本面。
/// </summary>
public sealed class WebviewWindow
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

    internal IWindowBackend Backend => _backend ?? throw new InvalidOperationException(
        "窗口后端尚未初始化：请在 OrielAppBuilder.Run() 之后使用窗口能力。");

    internal void Attach(IWindowBackend backend)
    {
        lock (_gate)
        {
            _backend = backend;
        }
        backend.Loaded += () => Loaded?.Invoke();
        backend.Closing += args => Closing?.Invoke(args);
        backend.Closed += () => Closed?.Invoke();
        backend.TitleChanged += title => TitleChanged?.Invoke(title);
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

    /// <summary>最大化 / 还原切换。</summary>
    public void ToggleMaximize() => Backend.ToggleMaximize();
    /// <summary>全屏切换，返回切换后是否全屏。</summary>
    public bool ToggleFullscreen() => Backend.ToggleFullscreen();
    /// <summary>窗口置顶切换，返回切换后是否置顶。</summary>
    public bool ToggleOnTop() => Backend.ToggleOnTop();

    /// <summary>在页面当前文档上下文中执行 JS。返回 WebView2 风格的 JSON 编码结果字符串。</summary>
    public Task<string> EvaluateJs(string script) => Backend.ExecuteScriptAsync(script);

    // ---- 对话框（pywebview 基本面）----

    public void ShowMessage(string text, string? title = null, OrielMessageBoxIcon icon = OrielMessageBoxIcon.Info)
        => Backend.ShowMessageBox(text, title, icon);

    /// <summary>打开文件对话框。filter 形如 "文本文件|*.txt|所有文件|*.*"；取消返回 null。</summary>
    public string? ShowOpenFileDialog(string? title = null, string? filter = null, string? initialDirectory = null)
        => Backend.ShowOpenFileDialog(title, filter, initialDirectory);

    /// <summary>保存文件对话框。取消返回 null。</summary>
    public string? ShowSaveFileDialog(string? title = null, string? filter = null, string? defaultExtension = null)
        => Backend.ShowSaveFileDialog(title, filter, defaultExtension);
}
