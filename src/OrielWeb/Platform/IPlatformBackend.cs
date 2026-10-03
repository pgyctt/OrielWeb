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

    /// <summary>
    /// 把动作切回 UI 线程执行（已在 UI 线程则直接执行）。
    /// 用于跨 <c>await</c> 之后碰窗口：await 的续体会落到线程池，而 GTK/AppKit 只能在各自的主线程调用。
    /// </summary>
    void PostToUiThread(Action action);

    /// <summary>向页面推送自定义事件（页面侧 <c>oriel.on(name, …)</c> 接收）；jsonPayload 必须是合法 JSON 文本。</summary>
    void EmitEvent(string name, string jsonPayload);

    /// <summary>
    /// 在鼠标位置弹出一个上下文菜单。<b>Windows 上是阻塞的</b>（TrackPopupMenu 自带模态消息循环，
    /// 直到用户选择或取消才返回），macOS 与 Linux 是异步弹出——这一点写进文档，因为它影响调用时机。
    /// </summary>
    void ShowContextMenu(IReadOnlyList<OrielMenuItem> items);

    /// <summary>上下文菜单里的自定义项被点击，参数是该项的 <see cref="OrielMenuItem.Id"/>。</summary>
    event Action<string>? ContextMenuItemClicked;

    /// <summary>
    /// 渲染引擎**内建**右键菜单的策略（与 <see cref="ShowContextMenu"/> 是两条独立通道）。
    /// </summary>
    /// <remarks>
    /// 做成可写属性而不是只走构造参数：过滤发生在**每次弹出时**，所以改完立刻生效，
    /// 不需要重建窗口。三平台实现都只在弹出回调里读它，不缓存。
    /// </remarks>
    OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>外部文件被拖进窗口。</summary>
    event Action<OrielFileDropEventArgs>? FileDropped;

    Task<string> ExecuteScriptAsync(string script);
    void PostMessageAsJson(string json);

    /// <summary>
    /// 打开文件对话框。取消时返回**空数组**（空数组而不是 null：多选下"没选"与"选了一个"的区分
    /// 本来就在长度上，用 null 还要额外区分三种情况）。
    /// </summary>
    string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options);

    /// <summary>保存文件对话框；取消返回 null。</summary>
    string? ShowSaveFileDialog(OrielSaveFileDialogOptions options);

    /// <summary>
    /// 选择文件夹。取消返回 null。
    /// 三平台都有原生入口，但**没有一个是跨平台一致的**：Windows 是老式 shell 文件夹选择器
    /// （<c>SHBrowseForFolder</c>），GTK 是同一个 GtkFileChooserDialog 切到
    /// <c>SELECT_FOLDER</c>，Cocoa 是 NSOpenPanel 打开"可选目录"。
    /// </summary>
    string? ShowFolderDialog(string? title, string? initialDirectory);

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

    /// <summary>
    /// 当前线程是不是本平台的 UI 线程（GTK/AppKit/Win32 的窗口 API 都只能在它上面调用）。
    /// </summary>
    /// <remarks>
    /// 用于在**运行时新建窗口**这类"只能在 UI 线程做"的公开 API 入口给出明确报错，
    /// 而不是让它以原生崩溃或静默无效的形式暴露（见 <see cref="OrielApp.CreateWindow"/>）。
    /// </remarks>
    bool IsOnUiThread();
    /// <remarks>
    /// <c>assets</c> 是内嵌资源表（<c>UseEmbeddedAssets</c> 未启用时为 null）：后端用本平台的
    /// scheme 处理器按需应答，**不再写盘**——所以这里传的是资源表而不是当年的"解压目录"。
    /// </remarks>
    IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets);

    /// <summary>创建托盘图标（应用级，最多一个）。</summary>
    ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app);

    /// <summary>
    /// 本平台是否支持系统通知。用于让调用方决定"要不要退回到应用内提示"，
    /// 而不是发出一条永远不出现的通知。各平台的实际支持度见 README 平台矩阵。
    /// </summary>
    bool NotificationsSupported { get; }

    // 通知点击上报已从接口中移除：三平台都拿不到（见 OrielApp 里同处的说明与 API.md）。

    /// <summary>
    /// 启用开机自启。<paramref name="id"/> 是应用标识（注册表值名 / .desktop 文件名 / LaunchAgent Label），
    /// <paramref name="arguments"/> 是随自启一起传入的参数。返回是否写入成功。
    /// </summary>
    bool EnableAutoStart(string id, IReadOnlyList<string>? arguments);

    /// <summary>关闭开机自启。返回是否执行成功（本来就没启用也算成功）。</summary>
    bool DisableAutoStart(string id);

    /// <summary>当前是否已设为开机自启（读平台里实际存在的配置，不是内存里的标记）。</summary>
    bool IsAutoStartEnabled(string id);

    /// <summary>
    /// 发送系统通知。
    /// </summary>
    /// <param name="notification">通知内容与标识。</param>
    /// <param name="appId">
    /// 应用标识（已规范化）：Windows 上用作 AUMID，Linux 上用作 <c>notify-send --app-name</c>。
    /// 它是**应用级**信息（决定系统通知设置里怎么给应用分组），所以按参数传而不是塞进
    /// <see cref="OrielNotificationOptions"/>——后者是"这一条通知"的描述。
    /// macOS 没有对应概念，忽略此参数。
    /// </param>
    /// <returns>
    /// <c>true</c> 表示**已成功提交给系统**——注意它不等于"用户看见了"：横幅显示与否由系统的通知设置、
    /// 专注助手、免打扰时段决定，那些不在本库的控制范围内。失败**不抛异常**（通知失败不该影响业务），
    /// 只如实返回 <c>false</c>，让调用方能据此在应用内补一个提示。
    /// </returns>
    bool ShowNotification(OrielNotificationOptions notification, string appId);
}
