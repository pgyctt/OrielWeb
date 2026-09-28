namespace OrielWeb;

/// <summary>窗口平台后端契约（OrielWeb 内部）。macOS/Linux 里程碑分别实现。</summary>
internal interface IWindowBackend
{
    IntPtr NativeWindowHandle { get; }

    event Action? Loaded;
    event Action<OrielCloseRequestEventArgs>? Closing;
    event Action? Closed;
    event Action<string>? TitleChanged;

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
    /// <summary>最大化/还原切换。</summary>
    void ToggleMaximize();
    /// <summary>全屏切换，返回切换后是否全屏。</summary>
    bool ToggleFullscreen();
    /// <summary>置顶切换，返回切换后是否置顶。</summary>
    bool ToggleOnTop();

    Task<string> ExecuteScriptAsync(string script);
    void PostMessageAsJson(string json);

    string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory);
    string? ShowSaveFileDialog(string? title, string? filter, string? defaultExtension);
    void ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon);
}

/// <summary>平台后端契约（消息循环 + 主线程调度 + 窗口工厂）。</summary>
internal interface IPlatformBackend : IDisposable
{
    void RunMessageLoop();
    void Quit();
    /// <summary>把动作切回 UI 线程执行（已在 UI 线程则直接执行）。</summary>
    void PostToMainThread(Action action);
    IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory);
}
