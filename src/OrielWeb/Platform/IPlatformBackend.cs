namespace OrielWeb;

/// <summary>窗口平台后端契约（OrielWeb 内部）。macOS/Linux 里程碑分别实现。</summary>
internal interface IWindowBackend
{
    IntPtr NativeWindowHandle { get; }

    /// <summary>本窗口所属的应用（窗口创建时注入）。</summary>
    OrielApp App { get; }

    /// <summary>
    /// 本窗口的 label（由 <see cref="OrielWindowOptions.Label"/> 定下，创建后不变）。
    /// </summary>
    /// <remarks>
    /// 放在后端契约上而不是只放 <see cref="WebviewWindow"/>：注入脚本要用它给页面装
    /// <c>oriel.label</c>，而注入脚本是后端在建窗时生成的（见 <c>OrielBridgeTemplate.Create</c>）。
    /// </remarks>
    string Label { get; }

    event Action? Loaded;
    event Action<OrielCloseRequestEventArgs>? Closing;
    event Action? Closed;
    event Action<string>? TitleChanged;
    /// <summary>窗口最大化状态变化（用户拖边框最大化、双击标题栏等原生路径也会触发）。</summary>
    event Action<bool>? MaximizedChanged;

    /// <summary>
    /// 窗口客户区尺寸变化（用户拖边框、最大化、程序调 <c>Resize</c> 都会触发）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 参数是**逻辑像素的客户区**尺寸，与 <see cref="IWindowBackend.Resize"/> 的入参同一语义
    /// （见 docs/API.md 的尺寸语义说明）。用它而不是物理像素：调用方（页面）关心的永远是
    /// CSS 布局尺寸，而各平台的物理像素基准不同。
    /// </para>
    /// <para>
    /// <b>为什么必须有它</b>：页面的 <c>window.resize</c> 事件只在**客户区**变化时触发，
    /// 而窗口被移动、被别的窗口遮挡、或进入全屏时都不会触发。宿主要做布局重排、
    /// 或者要判断"这个尺寸是不是用户拖出来的"，只能靠这个宿主侧事件。
    /// </para>
    /// </remarks>
    event Action<OrielSizeChangedEventArgs>? Resized;

    /// <summary>
    /// 窗口位置变化（用户拖动、最大化/还原、程序调 <c>MoveTo</c> 都会触发）。
    /// </summary>
    /// <remarks>
    /// 参数是窗口**左上角**的屏幕逻辑坐标（与 <see cref="IWindowBackend.MoveTo"/> 同一原点与单位）。
    /// 页面拿不到窗口位置（<c>window.screenX</c> 在多屏与 HiDPI 下不可靠），所以只能由宿主上报。
    /// </remarks>
    event Action<OrielPositionChangedEventArgs>? Moved;

    /// <summary>
    /// 窗口获得/失去焦点（对标 Tauri 2 的 <c>onFocusChanged</c>）。
    /// </summary>
    /// <remarks>
    /// <b>只上报焦点变化，不动焦点归属</b>：Windows 的 <c>WM_KILLFOCUS</c> 此前刻意不处理
    /// （见 Win32WindowHost 的说明——失焦时把焦点塞回宿主会让页面的 caret/选区/IME 错乱）。
    /// 这里加的是"通知"，不是"转移"，两者互不干涉。
    /// </remarks>
    event Action<bool>? FocusChanged;
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

    /// <summary>
    /// 窗口当前是否"显示中"（被 <see cref="Hide"/> 藏住后为 false；**最小化仍算显示中**）。
    /// </summary>
    /// <remarks>
    /// 三平台语义一致：问的都是"窗口（含渲染面）是否可见"，最小化不清可见位。
    /// 存在的理由：Windows 的组合宿主下，隐藏若只 <c>SW_HIDE</c> 宿主、漏掉 WebView2 的顶层
    /// 渲染窗口，宿主自身看起来已隐藏、实际仍在吃点击——调用方需要一个手段确认"真藏住了"。
    /// </remarks>
    bool IsVisible { get; }

    // ---- 只读状态查询（对标 Tauri 的 Window.isMinimized / isFocused / …）----

    /// <summary>当前是否最小化（图标化）。</summary>
    /// <remarks>
    /// <b>与 <see cref="IsVisible"/> 是两件事</b>：最小化的窗口仍然"可见"
    /// （<c>WS_VISIBLE</c> / 控件可见性都没变），它只是被收进了任务栏。
    /// 把它们混为一谈的后果是"最小化之后页面以为窗口被藏了"。
    /// </remarks>
    bool IsMinimized { get; }

    /// <summary>当前是否拥有键盘焦点。</summary>
    /// <remarks>
    /// 三平台的判据不同（Windows 比前台窗口、GTK 问 <c>gtk_window_is_active</c>、
    /// Cocoa 问 <c>isKeyWindow</c>），但语义一致：**这个窗口是不是用户正在操作的那个**。
    /// 注意"应用在前台"不等于"这个窗口有焦点"——多窗口应用里必须按窗口判。
    /// </remarks>
    bool IsFocused { get; }

    /// <summary>当前是否全屏。</summary>
    /// <remarks>
    /// 取的是**库自己记住的状态**（<see cref="ToggleFullscreen"/> 设置的），
    /// 而不是去问原生窗口：三平台判断"是不是全屏"都要比对窗口矩形与显示器矩形，
    /// 而那在用户手动调整窗口大小时会给出与"库认为的状态"不一致的答案
    /// （比如用户把窗口拖成刚好铺满屏幕）。
    /// 记住的状态与 <c>ToggleFullscreen</c> 的返回值一致，调用方看到的是一个自洽的世界。
    /// </remarks>
    bool IsFullscreen { get; }

    /// <summary>当前是否置顶。</summary>
    /// <remarks>同 <see cref="IsFullscreen"/>：取库记住的状态。</remarks>
    bool IsAlwaysOnTop { get; }

    /// <summary>当前是否有系统装饰（标题栏与边框）。</summary>
    /// <remarks>
    /// <para>
    /// 初值来自创建选项（<c>Frameless</c> 的取反），但**运行期可被
    /// <see cref="SetDecorations"/> 改**（B22 起），所以它读的是"当前状态"而不是选项。
    /// </para>
    /// <para>
    /// 三平台的取法不同：Windows 读宿主记下的状态（原生样式位里有 <c>WS_CAPTION</c> 也
    /// 不代表有装饰——本库的 frameless 靠 <c>WM_NCCALCSIZE</c> 把客户区铺满整个窗口），
    /// macOS 读 <c>styleMask</c> 的 Titled 位，Linux 读宿主记下的状态（GTK 的 getter 报的是
    /// **请求值**，Wayland 下合成器可能无视它）。
    /// </para>
    /// </remarks>
    bool IsDecorated { get; }

    /// <summary>
    /// 当前是否可缩放。
    /// </summary>
    /// <remarks>
    /// 这条**从原生动实际读取**（Windows 读 <c>WS_THICKFRAME</c>、GTK <c>gtk_window_get_resizable</c>、
    /// Cocoa <c>styleMask</c> 的 resizable 位），而不是记库里的状态：
    /// <c>SetResizable</c> 本来就是改原生样式，读回来才能反映"系统实际接受的是什么"。
    /// </remarks>
    bool IsResizable { get; }

    /// <summary>
    /// 当前是否可最大化。
    /// </summary>
    /// <remarks>
    /// <b>Linux 与 macOS 上是 <see cref="IsResizable"/> 的同义值</b>：GTK 没有"可最大化"
    /// 这个独立概念（最大化跟着 resizable 走），AppKit 同样把 <c>NSWindowStyleMaskResizable</c>
    /// 当作最大化的前提。Windows 有独立的 <c>WS_MAXIMIZEBOX</c>，所以那边是真读。
    /// 这个差异如实写出来，而不是在三平台上假装它们一致。
    /// </remarks>
    bool IsMaximizable { get; }

    /// <summary>当前是否可最小化。</summary>
    /// <remarks>
    /// 同 <see cref="IsMaximizable"/>：Windows 读 <c>WS_MINIMIZEBOX</c>，
    /// Linux 回落到 <see cref="IsResizable"/>（GTK 无独立概念），
    /// macOS 读 <c>NSWindowStyleMaskMiniaturizable</c> 位。
    /// </remarks>
    bool IsMinimizable { get; }

    /// <summary>
    /// 当前是否可关闭。
    /// </summary>
    /// <remarks>
    /// Windows 读**两件事**：窗口带 <c>WS_SYSMENU</c>（关闭按钮存在）且系统菜单里的
    /// <c>SC_CLOSE</c> 未被灰化（可用）——由 <see cref="SetClosable"/> 的落点决定，
    /// 少了后半句"写进去就读不回来"。macOS 读 <c>NSWindowStyleMaskClosable</c>；
    /// <b>Linux 恒为 <c>true</c></b>——GTK 没有"可关闭"这个概念，窗口总能收到
    /// delete-event（本库的关闭接管是在那之上做的拦截）。给一个恒真的值比给一个猜测的值诚实。
    /// </remarks>
    bool IsClosable { get; }

    /// <summary>
    /// 当前是否接收输入。
    /// </summary>
    /// <remarks>
    /// "禁用"在三个平台上的表现一致：窗口仍在、仍绘制，但**收不到鼠标与键盘**。
    /// Windows 是 <c>EnableWindow</c>、GTK 是 <c>gtk_widget_set_sensitive</c>、
    /// Cocoa **没有这个概念**——用 <c>setIgnoresMouseEvents:</c> 近似（只挡鼠标、不挡键盘）。
    /// 这个差别如实写出来：macOS 上"禁用"挡不住键盘，需要挡键盘的应用要自己处理。
    /// </remarks>
    bool IsEnabled { get; }

    /// <summary>启用/禁用窗口的输入（见 <see cref="IsEnabled"/>）。</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// 覆写**这个窗口**的主题（`null` = 恢复跟随系统），对标 Tauri 2 的
    /// `Window.setTheme`。三个平台的落点都存在，只是作用粒度不同（见
    /// <see cref="OrielApp.SetTheme"/> 里的说明——那边写的是同一套平台事实）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **与 <see cref="IPlatformBackend.CurrentTheme"/> 是两件事**：后者是
    /// "系统当前是什么主题"（只读、应用级），这里写的是"这个窗口要显示成什么主题"。
    /// 覆写之后 <see cref="IPlatformBackend.CurrentTheme"/> 与 <c>theme.changed</c> 仍然
    /// 跟着**系统**走——那两个回答的都是"系统现在什么样"，不该被一个窗口的覆写搅混。
    /// 页面想知道"我这个窗口显示成什么"，用 `window.theme()`（`window.theme` 命令），
    /// 那条会按窗口的覆写状态回答。
    /// </para>
    /// <para>
    /// 后端记不住的实现也**要如实抛 <see cref="NotSupportedException"/>**，
    /// 而不是静默 no-op——"设了但没生效"比"设了报错"难查得多（见 B15 的护栏）。
    /// </para>
    /// </remarks>
    void SetTheme(OrielTheme? theme);

    /// <summary>
    /// 设置这个窗口的缩放倍率（<c>1.0</c> = 100%），对标 Tauri 2 的
    /// <c>Webview.setZoom</c>。三平台落点：<b>Windows</b> 是
    /// <c>ICoreWebView2Controller.ZoomFactor</c>、<b>macOS</b> 是
    /// <c>WKWebView.pageZoom</c>、<b>Linux</b> 是 <c>webkit_web_view_set_zoom_level</c>
    /// ——三者语义一致（页面布局视口的缩放倍率），没有平台差异要登记。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与浏览器 Ctrl+滚轮的缩放是**同一个东西**（改的是布局视口宽度，
    /// 不是 CSS transform），所以 <c>100vw</c>、媒体查询、设备像素比都会跟着变。
    /// </para>
    /// <para>
    /// 有效性约束：<c>scale</c> 必须是有限正数，由**命令层**校验（在到达这里之前）。
    /// Windows 的 ZoomFactor 引擎内部另有 0.02–10.0 的接受范围，越界值会被引擎钳制
    /// 并通过 <c>ZoomFactorChanged</c> 事件回报实际值；本层不做二次钳制——
    /// "给 20 得到 10"是引擎的事实，库替用户猜会猜错。
    /// </para>
    /// </remarks>
    void SetZoom(double scale);

    /// <summary>
    /// 清除这个窗口所属 profile 的**全部**浏览数据（缓存、Cookie、localStorage、
    /// IndexedDB、Service Worker……），对标 Tauri 2 的 <c>Webview.clearAllBrowsingData</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三平台落点：<b>Windows</b> 是
    /// <c>ICoreWebView2Profile.ClearBrowsingDataAsync(All)</c>、<b>macOS</b> 是
    /// <c>WKWebsiteDataStore.removeData(ofTypes: allWebsiteDataTypes,
    /// modifiedSince: distantPast)</c>、<b>Linux</b> 是
    /// <c>webkit_website_data_manager_clear(ALL, timespan: 0)</c>。
    /// 三家清的都是**本应用自己的**数据目录/数据存储——不涉及其他应用，也没有
    /// 跨窗口的歧义（本库一个窗口一个 profile 的默认布局下，清的就是应用数据）。
    /// </para>
    /// <para>
    /// **完成时机是"已发起"而不是"已清完"**：三家的清除 API 都是异步的
    /// （WebView2 返回 <c>IAsyncAction</c>、WebKitGTK 是 GTask、macOS 要 completionHandler 块），
    /// 而本库的命令层是同步契约——这里发起清除后立刻返回，完成回调只进诊断日志。
    /// 页面在紧随其后的读取里看到旧数据（理论上可能）属于这个已知窗口，
    /// 文档已写明。macOS 的 completionHandler 传 nil（与 evaluateJavaScript 同一姿态）。
    /// </para>
    /// </remarks>
    void ClearAllBrowsingData();

    /// <summary>
    /// 设置窗口的最大尺寸（逻辑像素）。传 0 表示不限制。
    /// </summary>
    /// <remarks>
    /// <b>与 <c>SetMinSize</c> 是同一套机制的两端</b>，但三平台落点不同：
    /// Windows 在 <c>WM_GETMINMAXINFO</c> 里同时处理 min 与 max（那边本来就有这个处理）、
    /// Cocoa 是 <c>setContentMaxSize:</c>、GTK 走 <c>gtk_window_set_geometry_hints</c>
    /// ——**只声明 max**，不动既有的 <c>set_size_request</c> min 机制。
    /// </remarks>
    void SetMaxSize(int width, int height);

    // ---- 光标（B21）----
    //
    // 六个方法对应对标 Tauri 的 Window 上的同名 API。**支持矩阵是逐平台不同的**，
    // 不自造"统一语义"（那正是 B21 之前把这组登记为 A·跨平台不一致的理由）：
    //
    // | 方法                | Windows | macOS | Linux            |
    // |---------------------|---------|-------|------------------|
    // | SetCursorGrab       | ✅      | ✅    | ⛔ not_supported |
    // | SetCursorVisible    | ✅      | ✅    | ✅               |
    // | SetCursorIcon       | ✅      | ✅    | ✅（CSS 名）      |
    // | SetCursorPosition   | ✅      | ✅    | ✅ X11 / ⛔ Wayland |
    // | SetIgnoreCursorEvents | ✅    | ✅    | ✅ X11 / ⛔ Wayland |
    // | StartResizeDragging | ✅      | ⛔    | ✅               |
    //
    // 不支持的一律抛 **NotSupportedException**（命令层翻成 not_supported），
    // 不做静默 no-op：`await setCursorGrab(true)` 成功返回却什么都没发生，比报错难查得多。

    /// <summary>
    /// 把光标限制在窗口内（<c>setCursorGrab</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Linux 不支持</b>（Wayland 没有对应协议，X11 要 XGrabPointer 且会带来 grab 生命周期问题）——
    /// Tauri 文档同样标注 Linux 不支持。这里抛 <see cref="NotSupportedException"/>。
    /// </para>
    /// <para>
    /// macOS 的实现是 <c>CGAssociateMouseAndMouseCursorPosition</c>：光标会被**锁死**在原地
    /// （连移动都不响应），不是"限制在窗口矩形内"。两者不是一回事，但这是 macOS 能给的唯一形态。
    /// </para>
    /// </remarks>
    void SetCursorGrab(bool grab);

    /// <summary>显示/隐藏光标（<c>setCursorVisible</c>）。</summary>
    /// <remarks>
    /// 三平台都靠"显示计数器"，所以宿主侧**按状态去重**：连续两次隐藏只减一次计数，
    /// 否则一次多余的显示请求就会让光标永远回不来。
    /// </remarks>
    void SetCursorVisible(bool visible);

    /// <summary>设置光标形状（<c>setCursorIcon</c>）。</summary>
    /// <remarks>
    /// <b>设置之后页面的 CSS <c>cursor</c> 不再生效</b>（Windows 上会忽略 WebView2 报来的光标、
    /// macOS/Linux 上覆盖 webview 的光标）。这是"应用显式指定"应有的语义，但确实会盖掉
    /// 文本框的 I 形光标之类的东西——调用方要自己想清楚再用。
    /// </remarks>
    void SetCursorIcon(OrielCursorIcon icon);

    /// <summary>
    /// 把光标移到窗口客户区内的某个位置（<c>setCursorPosition</c>）。
    /// </summary>
    /// <param name="x">客户区**逻辑**像素横坐标（左上原点）。</param>
    /// <param name="y">客户区逻辑像素纵坐标。</param>
    /// <remarks>
    /// 只收逻辑像素：本库对页面只暴露逻辑坐标（见 <c>window.innerSize</c> 的说明），
    /// 物理换算在平台后端里做（它知道自己的 DPI/缩放）。
    /// </remarks>
    void SetCursorPosition(double x, double y);

    /// <summary>让窗口是否忽略鼠标事件（<c>setIgnoreCursorEvents</c>，即"点击穿透"）。</summary>
    /// <remarks>
    /// <b>Linux 只在 X11 下有效</b>：GTK3 的 <c>gtk_widget_input_shape_combine_region</c>
    /// 在 Wayland 下是 no-op（文档明说），所以 Wayland 上抛 <see cref="NotSupportedException"/>。
    /// </remarks>
    void SetIgnoreCursorEvents(bool ignore);

    /// <summary>从窗口某条边/角开始一次 resize 拖动（<c>startResizeDragging</c>）。</summary>
    /// <remarks>
    /// <para>
    /// <b>macOS 不支持</b>：AppKit 没有公开的"以编程方式开始 resize 拖动"入口
    /// （<c>performWindowDragWithEvent:</c> 只能移动窗口，不能改尺寸）。Tauri 同样不支持。
    /// </para>
    /// <para>
    /// <b>Windows/Linux 会进入一个模态循环</b>（跟着鼠标动、松开才结束），所以必须在
    /// 鼠标按下的处理过程中调用——从一个"稍后执行"的定时器里调用会立刻失败或卡住。
    /// 页面侧通常是在 <c>mousedown</c> 里调。
    /// </para>
    /// </remarks>
    void StartResizeDragging(OrielResizeDirection direction);

    // ---- 平台开关 / 特效 / 任务栏（B22）----
    //
    // 19 个方法对应 Tauri 2 的 Window 上的同名 API。**支持矩阵逐平台不同**，而且这次
    // "做"的方式是统一的：**能做的平台真做，做不了的抛 NotSupportedException**
    // （命令层翻成 not_supported），不做静默 no-op——这一批的护栏与 B21 同一套。
    //
    // | 方法                        | Windows                   | macOS                      | Linux                        |
    // |-----------------------------|---------------------------|----------------------------|------------------------------|
    // | SetSkipTaskbar              | ✅ ITaskbarList::DeleteTab | ⛔ Tauri 同样不支持        | ✅ gtk_window_set_skip_taskbar_hint |
    // | SetVisibleOnAllWorkspaces   | ⛔ 无公开入口              | ✅ collectionBehavior       | ✅ gtk_window_stick / unstick |
    // | SetContentProtected         | ✅ SetWindowDisplayAffinity | ✅ sharingType             | ⛔ 无对应协议                |
    // | SetSimpleFullscreen         | ✅ = SetFullscreen         | ✅ 独立于原生全屏          | ✅ = SetFullscreen            |
    // | SetFullscreenOnMonitor      | ✅ 移到该屏再全屏           | ✅ 移到该屏再全屏          | ✅ gtk_window_fullscreen_on_monitor |
    // | SetTitleBarStyle            | ⛔ 用 Frameless 表达       | ✅ titlebarAppearsTransparent | ⛔ 无对应概念             |
    // | SetShadow                   | ✅ DWMWA_NCRENDERING_POLICY | ✅ hasShadow              | ⛔ GTK 无阴影 API            |
    // | SetAlwaysOnBottom           | ✅ HWND_BOTTOM              | ✅ window.level            | ✅ gtk_window_set_keep_below |
    // | SetFocusable                | ✅ WS_EX_NOACTIVATE         | ⛔ 只有私有 API            | ✅ set_accept_focus / can_focus |
    // | SetDecorations              | ✅ 改样式位 + FRAMECHANGED  | ✅ styleMask               | ✅ gtk_window_set_decorated  |
    // | SetEffects / ClearEffects   | ✅ Mica/Acrylic/Blur/Tabbed | ✅ NSVisualEffectView      | ⛔ 无桌面无关的材质 API      |
    // | SetProgressBar              | ✅ ITaskbarList3            | ⛔ Tauri 同样不实现        | ⛔ 需 libunity，本库不链接   |
    // | SetOverlayIcon              | ✅ ITaskbarList3            | ⛔                         | ⛔                           |
    // | SetBadgeCount / SetBadgeLabel | ⛔ 用 SetOverlayIcon 代替 | ✅ NSDockTile              | ⛔ 需 libunity，本库不链接   |
    //
    // 为什么 macOS 的 SetSkipTaskbar 是 ⛔ 而 Linux 是 ✅：Tauri 文档明写 macOS Unsupported
    // （macOS 没有"任务栏"这个概念，Dock 里的是整个应用），而 Linux 的
    // gtk_window_set_skip_taskbar_hint 是真实存在且被主流 WM 尊重的。

    /// <summary>是否在任务栏/Dock 里隐藏这个窗口（<c>setSkipTaskbar</c>）。</summary>
    /// <remarks>
    /// <b>macOS 抛 <see cref="NotSupportedException"/></b>：Dock 里显示的是整个应用，
    /// 没有"按窗口隐藏"这回事（Tauri 同样标注 macOS 不支持）。
    /// Linux 的 <c>gtk_window_set_skip_taskbar_hint</c> 是**提示**，Wayland 下由合成器决定。
    /// </remarks>
    void SetSkipTaskbar(bool skip);

    /// <summary>是否在所有工作区/虚拟桌面上可见（<c>setVisibleOnAllWorkspaces</c>）。</summary>
    /// <remarks>
    /// <b>Windows 抛 <see cref="NotSupportedException"/></b>：Win32 没有公开的
    /// "加入所有虚拟桌面"接口（Tauri 同样标注 Windows 不支持）。
    /// Linux 走 <c>gtk_window_stick</c>（X11 的 <c>_NET_WM_STATE_STICKY</c>）；
    /// Wayland 下该 hint 不被协议覆盖，主流合成器会忽略——文档写明。
    /// </remarks>
    void SetVisibleOnAllWorkspaces(bool visible);

    /// <summary>是否阻止窗口内容被别的应用截取（<c>setContentProtected</c>）。</summary>
    /// <remarks>
    /// Windows 是 <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>（Win10 2004+；
    /// 更早的系统退回 <c>WDA_MONITOR</c>，那会让窗口在截屏里显示为黑块），
    /// macOS 是 <c>NSWindow.sharingType = NSWindowSharingNone</c>。
    /// <b>Linux 抛 <see cref="NotSupportedException"/></b>：X11 没有这个能力，
    /// Wayland 的相关协议 GTK3 不暴露。
    /// </remarks>
    void SetContentProtected(bool contentProtected);

    /// <summary>简化全屏（macOS 的"不占新 Space 的全屏"，<c>setSimpleFullscreen</c>）。</summary>
    /// <remarks>
    /// <b>只有 macOS 有独立语义</b>：改 <c>styleMask</c> 去掉标题栏并把窗口铺满当前屏幕，
    /// 不创建新的 Space（Lion 之前的全屏就是这样）。**Windows / Linux 上等同于
    /// <see cref="SetFullscreen"/>**——这与 Tauri 的文档一致（"On other platforms,
    /// this is the same as setFullscreen"）。
    /// </remarks>
    void SetSimpleFullscreen(bool fullscreen);

    /// <summary>在包含给定物理坐标的那台显示器上全屏（<c>setFullscreenOnMonitor</c>）。</summary>
    /// <param name="x">屏幕**物理**像素横坐标（取显示器矩形内的任意一点，通常是左上角）。</param>
    /// <param name="y">屏幕物理像素纵坐标。</param>
    /// <remarks>
    /// <para>
    /// 与 Tauri 一致：**该坐标不在任何显示器上时什么都不做**（不是错误）。
    /// 落点分两步：先把窗口移到那台显示器，再进入全屏——三平台的全屏都以
    /// "窗口当前所在显示器"为准，没有"直接指定显示器全屏"的通用入口
    /// （GTK 有 <c>gtk_window_fullscreen_on_monitor</c>，那一步用它一次到位）。
    /// </para>
    /// <para>
    /// 全屏状态下改显示器由平台各自处理：Windows 先退出全屏再移动再进入，
    /// macOS 只能"退出全屏 → 移动 → 再进入"（原生全屏切换是异步动画）。
    /// </para>
    /// </remarks>
    void SetFullscreenOnMonitor(int x, int y);

    /// <summary>标题栏样式（<c>setTitleBarStyle</c>）。</summary>
    /// <remarks>
    /// <b>只有 macOS 实现</b>：<see cref="OrielTitleBarStyle.Transparent"/> 设
    /// <c>titlebarAppearsTransparent</c>，<see cref="OrielTitleBarStyle.Overlay"/> 再加
    /// <c>titleVisibility = hidden</c> + <c>fullSizeContentView</c>。
    /// Windows/Linux 抛 <see cref="NotSupportedException"/>——那边的等价能力是
    /// "创建时 <see cref="OrielWindowOptions.Frameless"/> + 页面自绘"，没有运行期切换。
    /// </remarks>
    void SetTitleBarStyle(OrielTitleBarStyle style);

    /// <summary>是否给窗口画阴影（<c>setShadow</c>）。</summary>
    /// <remarks>
    /// <para>
    /// Windows 落点是 DWM 的非客户区渲染策略（<c>DWMWA_NCRENDERING_POLICY</c>）。
    /// 与 Tauri 文档一致的两条平台事实：**装饰窗口关不掉阴影**（DWM 总会画边框阴影），
    /// 而给无边框窗口打开阴影会在 Win11 上带来圆角与 1px 边框。
    /// </para>
    /// <para>
    /// <b>Linux 抛 <see cref="NotSupportedException"/></b>：GTK 没有阴影 API，
    /// 阴影由合成器按窗口类型给（<c>_GTK_FRAME_EXTENTS</c> 那套），应用改不了。
    /// </para>
    /// </remarks>
    void SetShadow(bool enable);

    /// <summary>是否永远压在别的窗口之下（<c>setAlwaysOnBottom</c>）。</summary>
    /// <remarks>
    /// Windows 是 <c>SetWindowPos(HWND_BOTTOM)</c>（只在**置顶关闭**时有意义：
    /// 两者同时开时后设的那个赢），macOS 把 <c>window.level</c> 降到普通窗口之下，
    /// Linux 是 <c>gtk_window_set_keep_below</c>（同样是 WM 提示）。
    /// </remarks>
    void SetAlwaysOnBottom(bool alwaysOnBottom);

    /// <summary>窗口能否获得焦点（<c>setFocusable</c>）。</summary>
    /// <remarks>
    /// <b>macOS 抛 <see cref="NotSupportedException"/></b>：AppKit 没有公开的
    /// "这个窗口不能成为 key window"的入口（只有私有的 <c>_setPreventsActivation</c>，
    /// 用它等于把自己绑到未公开 API 上）。
    /// Windows 切 <c>WS_EX_NOACTIVATE</c>，Linux 走 <c>gtk_window_set_accept_focus</c>
    /// + <c>gtk_widget_set_can_focus</c>（Wayland 下同样只是提示）。
    /// </remarks>
    void SetFocusable(bool focusable);

    /// <summary>是否显示系统装饰（标题栏与边框，<c>setDecorations</c>）。</summary>
    /// <remarks>
    /// <para>
    /// 运行期切换，三平台都做：Windows 改样式位后必须
    /// <c>SetWindowPos(SWP_FRAMECHANGED)</c> 让非客户区重算（否则"改了但看着没变"），
    /// macOS 切 <c>NSWindowStyleMaskTitled</c>，Linux 是 <c>gtk_window_set_decorated</c>
    /// （Wayland 下由合成器决定，通常会被尊重）。
    /// </para>
    /// <para>
    /// <b>与创建期的 <see cref="OrielWindowOptions.Frameless"/> 是两件事</b>：
    /// 后者决定宿主自己的 <c>WM_NCCALCSIZE</c> 分支与拖拽热区，创建后不再变；
    /// 这个方法只改原生装饰，不影响页面的自绘标题栏。
    /// </para>
    /// </remarks>
    void SetDecorations(bool decorations);

    /// <summary>应用一组窗口材质（<c>setEffects</c>）。</summary>
    /// <param name="effects">材质列表 + 状态/圆角/色调（见 <see cref="OrielEffects"/>）。</param>
    /// <remarks>
    /// <para>
    /// <b>取列表里第一个本平台支持的那个，其余忽略</b>（与 Tauri 一致），所以一份代码
    /// 可以同时列 Windows 与 macOS 的材质。**一个都不支持时抛
    /// <see cref="NotSupportedException"/>**，消息里列出本平台支持的取值——
    /// "设了没生效"必须能被看见。
    /// </para>
    /// <para>
    /// <b>Linux 抛 <see cref="NotSupportedException"/></b>：窗口材质是 Windows DWM 与
    /// macOS AppKit 的概念，GTK3 没有对应 API（GTK4 的 <c>GtkWindow</c> 也没有；
    /// 各桌面环境自己的模糊协议不在 GTK3 里）。
    /// </para>
    /// <para>
    /// <b>需要页面自己是透明的</b>：材质画在 webview 之后，页面不透明就什么也看不见。
    /// macOS 上本库会把窗口与 webview 的背景放开（与 Tauri 的 <c>transparent: true</c>
    /// 同一姿态），Windows 上 DWM 材质由系统画在客户区之后，页面需要有透明区域才可见。
    /// </para>
    /// </remarks>
    void SetEffects(OrielEffects effects);

    /// <summary>清掉已应用的材质（<c>clearEffects</c>）。</summary>
    /// <remarks>
    /// 没设过时是**空操作**（不报错、不抛异常）：清一个不存在的状态不该失败。
    /// Linux 上同样抛 <see cref="NotSupportedException"/>（与 <see cref="SetEffects"/> 一致：
    /// 那个能力整个不存在，而不是"清的时候才不存在"）。
    /// </remarks>
    void ClearEffects();

    /// <summary>任务栏进度条（<c>setProgressBar</c>）。</summary>
    /// <param name="status">状态；<see cref="OrielProgressBarStatus.None"/> 表示隐藏。</param>
    /// <param name="progress">0–100 的百分比；<c>null</c> 表示不给数值（不确定状态或隐藏）。</param>
    /// <remarks>
    /// <b>只有 Windows 实现</b>（<c>ITaskbarList3::SetProgressState</c> /
    /// <c>SetProgressValue</c>）。macOS 的 Dock 磁贴没有进度条 API（Tauri 同样不实现），
    /// Linux 需要 <c>libunity</c> 的 LauncherEntry（本库不链接那个库，不假装支持）。
    /// 两者都抛 <see cref="NotSupportedException"/>。
    /// </remarks>
    void SetProgressBar(OrielProgressBarStatus status, double? progress);

    /// <summary>任务栏图标上的角标覆盖图（<c>setOverlayIcon</c>）。</summary>
    /// <param name="icon">16×16 的图标（PNG/ICO 字节经 <see cref="OrielImage"/> 解码）；<c>null</c> 表示清除。</param>
    /// <remarks>
    /// <b>只有 Windows 有这个概念</b>（<c>ITaskbarList3::SetOverlayIcon</c>）。
    /// macOS 的等价物是 Dock 徽标（<see cref="SetBadgeCount"/> / <see cref="SetBadgeLabel"/>），
    /// 形状不同，不互相顶替；Linux 两个都没有。两者都抛 <see cref="NotSupportedException"/>。
    /// </remarks>
    void SetOverlayIcon(OrielImage? icon);

    /// <summary>Dock 徽标上的数字（<c>setBadgeCount</c>）。</summary>
    /// <param name="count">要显示的数字；<c>null</c> 或 0 表示清除。</param>
    /// <remarks>
    /// <b>只有 macOS 实现</b>（<c>NSApp.dockTile.badgeLabel</c>，**应用级**而不是按窗口）。
    /// Windows 没有徽标概念（用 <see cref="SetOverlayIcon"/> 代替），
    /// Linux 需要 <c>libunity</c>（本库不链接）。两者都抛 <see cref="NotSupportedException"/>。
    /// </remarks>
    void SetBadgeCount(int? count);

    /// <summary>Dock 徽标上的短文本（<c>setBadgeLabel</c>）。</summary>
    /// <param name="label">要显示的文本；<c>null</c> 表示清除。</param>
    /// <remarks><b>只有 macOS 实现</b>（与 <see cref="SetBadgeCount"/> 同一个落点）。</remarks>
    void SetBadgeLabel(string? label);

    /// <summary>
    /// 请求用户注意（任务栏闪烁 / Dock 弹跳）。
    /// </summary>
    /// <param name="type">
    /// <see cref="OrielUserAttention.Critical"/> 或 <see cref="OrielUserAttention.Informational"/>；
    /// <c>null</c> 表示**取消**已发出的请求。
    /// </param>
    /// <remarks>
    /// <b>Linux 上两档退化成同一件事</b>：GTK 只有 <c>gtk_window_set_urgency_hint</c>
    /// 这个开/关，没有紧急程度的分级。如实记录，而不是在那里假装分档。
    /// </remarks>
    void RequestUserAttention(OrielUserAttention? type);

    /// <summary>设置是否可最大化（见 <see cref="IsMaximizable"/>）。</summary>
    /// <remarks>
    /// <b>Linux 上是空操作</b>：GTK 没有控制最大化按钮的入口，允许的动作由窗口管理器
    /// 自己按 <c>_NET_WM_ALLOWED_ACTIONS</c> 决定，应用改不了（Tauri 在那边同样是空操作）。
    /// 状态也不会被记住——<see cref="IsMaximizable"/> 在 Linux 上继续回落
    /// <see cref="IsResizable"/>，不会因为调过这个方法就撒谎说"已禁止最大化"。
    /// </remarks>
    void SetMaximizable(bool enabled);

    /// <summary>设置是否可最小化（见 <see cref="IsMinimizable"/>）。</summary>
    /// <remarks>
    /// <b>Linux 上同样是空操作</b>，理由见 <see cref="SetMaximizable"/>。
    /// </remarks>
    void SetMinimizable(bool enabled);

    /// <summary>
    /// 设置是否可关闭（见 <see cref="IsClosable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>三平台落点不同，而且不是同一件事</b>：
    /// <list type="bullet">
    ///   <item>Windows：<b>灰化系统菜单里的"关闭"项</b>（<c>SC_CLOSE</c>）。关闭按钮在
    ///     Win32 里属于系统菜单，没有独立样式位；用清 <c>WS_SYSMENU</c> 代替会把
    ///     三个按钮和整个系统菜单一起干掉，那是"删按钮"而不是"按钮不可用"。</item>
    ///   <item>macOS：切 <c>NSWindowStyleMaskClosable</c>，红色按钮随之消失/出现。</item>
    ///   <item>Linux：<b>空操作</b>——GTK 没有这一属性，窗口总能收到 delete-event。
    ///     要"关不掉"得拦 <c>close-requested</c> 事件。</item>
    /// </list>
    /// </remarks>
    void SetClosable(bool enabled);

    /// <summary>
    /// 设置窗口图标。<paramref name="path"/> 是图像文件路径（PNG / ICO）。
    /// </summary>
    /// <remarks>
    /// <b>三平台的"窗口图标"落点不完全相同，这条如实写出来</b>：
    /// <list type="bullet">
    ///   <item>Windows：<c>WM_SETICON</c>（大小两个尺寸），作用在**本窗口**上。
    ///     注意 <c>LoadImageW</c> 只认 ICO/BMP，PNG 需要先解码（本库走
    ///     <see cref="OrielImage"/> 那条路，见实现）。</item>
    ///   <item>Linux：<c>gtk_window_set_icon_from_file</c>，X11 下写入
    ///     <c>_NET_WM_ICON</c>；<b>Wayland 下由合成器决定，通常直接忽略</b>。</item>
    ///   <item>macOS：<b>没有窗口级图标</b>，能设的只有应用（Dock）图标
    ///     （<c>setApplicationIconImage:</c>）——所以这个调用在 macOS 上会影响
    ///     **整个应用**，而不只是这一个窗口。这是平台事实，不是实现取舍。</item>
    /// </list>
    /// <para>
    /// 这个差异与 <c>setMaximizable</c> 那类不同：三平台上"设置图标"这件事**都存在**
    /// 且都生效，只是 macOS 的作用域更大。所以本库提供它，并把作用域差异写明。
    /// </para>
    /// </remarks>
    void SetIcon(string path);

    /// <summary>
    /// 设置窗口图标，图像数据直接来自内存（PNG / ICO 字节）。
    /// </summary>
    /// <param name="bytes">PNG 或 ICO 的原始字节（ICON 与 PNG 都收，见 <see cref="OrielImage.FromBytes"/>）。</param>
    /// <remarks>
    /// 与 <see cref="SetIcon(string)"/> 是同一个落点、同一套平台差异（macOS 影响整个应用、
    /// Linux 的 Wayland 通常忽略），区别只在数据从哪来：
    /// 这条把字节交给各平台的"RGBA → 原生图像"工厂
    /// （Windows <c>Win32IconFactory</c>、macOS <c>MacOSImageFactory</c>、Linux <c>GtkPixbufFactory</c>），
    /// 而那三个工厂本来就是给托盘图标用的——这里只是把它们接到窗口图标上。
    /// <para>
    /// 新增这一条是因为 Tauri 的 <c>JsImage</c> 里字符串（路径）只占一种，
    /// 另外三种都是字节/数组形式。
    /// </para>
    /// </remarks>
    void SetIconFromBytes(byte[] bytes);

    /// <summary>
    /// 设置窗口图标，像素数据是**裸 RGBA**（不解码任何格式）。
    /// </summary>
    /// <param name="rgba">行优先、自上而下的 RGBA，长度必须正好 <paramref name="width"/> * <paramref name="height"/> * 4。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <remarks>
    /// 这是 `@orielweb/api/image` 的 `Image.new(rgba, w, h)` 那条路。
    /// **不需要先把 RGBA 编码成 PNG**：三平台的图标工厂本来就收
    /// <see cref="OrielImage"/>（那是个 RGBA 容器，构造时不做解码），
    /// 所以这里直接 <c>new OrielImage(rgba, w, h)</c> 再交给工厂即可。
    /// <para>
    /// 平台差异同 <see cref="SetIcon(string)"/>（macOS 影响整个应用、Linux 的 Wayland 通常忽略）。
    /// </para>
    /// </remarks>
    void SetIconFromRgba(byte[] rgba, int width, int height);

    /// <summary>
    /// 设置窗口底色（<c>0xAARRGGBB</c>）——页面透明、或内核还没加载完时看到的那个颜色。
    /// </summary>
    /// <remarks>
    /// 三平台都落到**引擎的底色**（而不是窗口客户区的画刷）：
    /// Windows <c>ICoreWebView2Controller2.DefaultBackgroundColor</c>、
    /// macOS <c>WKWebView.underPageBackgroundColor</c>（12+；取不到时回落
    /// <c>NSWindow.backgroundColor</c>）、Linux <c>webkit_web_view_set_background_color</c>。
    /// <para>
    /// 之所以是三平台一致的：这三处都是"webview 自己的底色"，语义相同。
    /// 这也与 <see cref="OrielWindowOptions.BackgroundColor"/> 的约定一致——
    /// 该方法与它**共用同一套 ARGB 拆解**（<c>TryGetBackgroundArgb</c>），
    /// 免得某平台把 R 与 B 搞反（那种错在深色底上不容易一眼看出来）。
    /// </para>
    /// </remarks>
    void SetBackgroundColor(uint argb);

    void Show();
    void Hide();
    void Close();

    /// <summary>
    /// 强制销毁窗口：**绕过 <see cref="Closing"/> 与页面接管**（对标 Tauri 2 的 <c>Window.destroy</c>）。
    /// </summary>
    /// <remarks>
    /// 页面的"可阻止关闭"需要一个不受阻止的出口：页面自己同意关闭时调的就是它。
    /// 若它也能被 <see cref="Closing"/> 拦下，"确认后关闭"这条路会被宿主否决逻辑
    /// 无声地变成"关不掉"——而调用方以为用户已经确认过了。
    /// </remarks>
    void Destroy();

    /// <summary>
    /// 是否让**页面**接管关闭（置真后关闭请求会推 <c>close-requested</c> 并挂起）。
    /// </summary>
    /// <remarks>
    /// 详见 <see cref="OrielWindowOptions.InterceptClose"/>。做成可写属性而不是只读选项，
    /// 是因为页面往往只在"有未保存内容"时接管——那是个运行期状态。
    /// </remarks>
    bool CloseInterceptEnabled { get; set; }

    /// <summary>
    /// 哪几类宿主事件转发给页面（默认 <see cref="OrielWindowEventKind.None"/>）。
    /// </summary>
    /// <remarks>
    /// 详见 <see cref="OrielWindowEventKind"/>：默认全关是因为尺寸/位置事件在拖动时
    /// 每个像素来一次，而推给页面在 macOS/Linux 上是一次 JS 求值。
    /// </remarks>
    OrielWindowEventKind EventForwarding { get; set; }

    /// <summary>
    /// 窗口所在显示器的缩放比（1.0 = 100%）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 页面用它把 <c>PhysicalSize</c> 换算成逻辑尺寸（Tauri 的 <c>setSize</c> 两种都收）。
    /// 各平台取值：Windows <c>GetDpiForWindow/96</c>、Linux <c>gtk_widget_get_scale_factor</c>、
    /// macOS <c>NSScreen.backingScaleFactor</c>。
    /// </para>
    /// <para>
    /// <b>取窗口所在显示器而不是系统值</b>：窗口被拖到另一块不同缩放的显示器后它会变。
    /// 用系统值的话，副屏上的换算会整体差一个倍数——而那表现为"窗口大小不对"，很难联想到缩放。
    /// </para>
    /// <para>
    /// macOS 上这是 <c>backingScaleFactor</c>（Retina 上为 2.0），而窗口 API 收的是**点**：
    /// 点与逻辑像素一一对应，所以换算时除以它正是想要的。
    /// </para>
    /// </remarks>
    double ScaleFactor { get; }

    /// <summary>
    /// 窗口所在显示器的缩放比发生了变化（见 <see cref="ScaleFactor"/>）；参数是**新的缩放比**。
    /// </summary>
    /// <remarks>
    /// <b>按窗口而不是按应用广播</b>：同一时刻两个窗口可以待在不同缩放的显示器上，
    /// 所以"缩放变了"必须按窗口说——一个全局事件会让副屏上的窗口收到不属于它的值。
    /// <para>
    /// 三平台的触发点：Windows <c>WM_DPICHANGED</c>、macOS
    /// <c>NSWindowDidChangeBackingPropertiesNotification</c>、Linux GDK 的
    /// <c>notify::scale-factor</c>。
    /// </para>
    /// </remarks>
    event Action<double>? ScaleFactorChanged;

    /// <summary>
    /// **客户区**尺寸（逻辑像素）——即 <see cref="IWindowBackend.Resize"/> 的逆运算。
    /// </summary>
    /// <remarks>
    /// 三平台都能可靠给出（Windows <c>GetClientRect</c>、Linux <c>gtk_window_get_size</c>、
    /// macOS <c>contentView.frame</c>）。取不到时返回 <c>default</c>（0x0）而不是抛异常：
    /// 窗口正在销毁时读几何是正常时序，让它抛只会逼调用方到处包 try。
    /// </remarks>
    OrielSize InnerSize { get; }

    /// <summary>
    /// **整窗**尺寸（逻辑像素，含边框与标题栏）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows <c>GetWindowRect</c>、macOS <c>NSWindow.frame</c>、Linux
    /// <c>gdk_window_get_frame_extents</c>。
    /// </para>
    /// <para>
    /// <b>Linux/Wayland 下可能与 <see cref="InnerSize"/> 相等</b>：合成器不告诉客户端装饰尺寸，
    /// GDK 只能报它自己知道的那部分。这是平台事实（Tauri 同样如此），不是实现取巧。
    /// </para>
    /// </remarks>
    OrielSize OuterSize { get; }

    /// <summary>窗口**左上角**（含装饰）的屏幕逻辑坐标。</summary>
    OrielPosition OuterPosition { get; }

    /// <summary>
    /// **客户区**左上角的屏幕逻辑坐标。
    /// </summary>
    /// <remarks>
    /// <b>Linux 上目前恒等于 <see cref="OuterPosition"/></b>：GTK 不暴露"客户区相对窗口原点的偏移"
    /// （装饰由 WM 画，客户端看不到它的尺寸）。Windows 用 <c>ClientToScreen</c> 精确给出，
    /// macOS 用 <c>contentRectForFrameRect:</c>。与其编一个偏移值，不如如实相等——
    /// 调用方据此判断"这个平台上内外位置是否可区分"。
    /// </remarks>
    OrielPosition InnerPosition { get; }

    void Focus();
    void Maximize();
    void Minimize();
    void Restore();
    void SetFullscreen(bool enabled);
    void SetOnTop(bool enabled);
    void SetTitle(string title);

    /// <summary>
    /// 当前窗口标题（对标 Tauri 2 的 <c>Window.title()</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 读的是宿主记下的值，而不是去问原生窗口（<c>GetWindowTextW</c> / <c>gtk_window_get_title</c> /
    /// <c>title</c>）：三者语义有细微差别（页面 &lt;title&gt; 与程序设置会互相覆盖），
    /// 而"库自己对外的标题是什么"只有一个答案，就是它记下的那个。
    /// </para>
    /// <para>
    /// 它与 <see cref="IWindowBackend.TitleChanged"/> **不是**同一件事：那个事件只在页面文档标题变化时
    /// 触发（标题栏跟随页面），而程序调 <c>SetTitle</c> 不会触发它——宿主自己知道自己改了。
    /// 需要"标题现在是什么"请读这个属性，不要等事件。
    /// </para>
    /// </remarks>
    string Title { get; }
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
    /// <returns>是否已成功投递（详见 <see cref="IPlatformBackend.PostToMainThread"/>）。</returns>
    bool PostToUiThread(Action action);

    /// <summary>
    /// 当前线程是不是本窗口所属平台的 UI 线程（GTK/AppKit/Win32 的窗口 API 都只能在它上面调用）。
    /// </summary>
    /// <remarks>
    /// 用于宿主自己的后台线程里判断"要不要编组"，从而不必再自己维护一份主线程 ID
    /// （下游反馈 2026-10-14 的 P2-6）。语义与 <see cref="IPlatformBackend.IsOnUiThread"/> 一致。
    /// </remarks>
    bool IsOnUiThread();

    /// <summary>向页面推送自定义事件（页面侧 <c>oriel.on(name, …)</c> 接收）；jsonPayload 必须是合法 JSON 文本。</summary>
    void EmitEvent(string name, string jsonPayload);

    /// <summary>
    /// 弹出一个上下文菜单。<b>Windows 上是阻塞的</b>（TrackPopupMenu 自带模态消息循环，
    /// 直到用户选择或取消才返回），macOS 同样阻塞（<c>popUpMenuPositioningItem:</c> 跟踪到用户选择），
    /// 只有 Linux 是异步弹出（GTK 的菜单不阻塞）——这一点写进文档，因为它影响调用时机。
    /// </summary>
    /// <param name="items">菜单树。</param>
    /// <param name="position">
    /// 弹出位置（**逻辑像素**，相对窗口客户区左上角）；<c>null</c> 表示"当前鼠标位置"。
    /// 页面侧 <c>menu.popup(at)</c> 的物理坐标在进入这里之前已由命令层换算成逻辑像素——
    /// 后端只认一种单位，免得三平台各自再判一次。
    /// </param>
    /// <param name="autoDismissAfter">
    /// 自动关闭延时；<c>null</c>（默认）表示等用户选择。
    /// 无交互的自检（CI）靠它才能在没人点的情况下跑完——见 Windows 实现里的说明。
    /// </param>
    void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null);

    /// <summary>
    /// 关掉当前正在显示的上下文菜单（没有菜单在屏上时是空操作）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：<b>Windows 的弹出是阻塞的</b>——<see cref="ShowContextMenu"/> 在用户选择
    /// 之前不返回。这意味着两件事：页面上的"关闭菜单"按钮点不到（UI 线程被占着），
    /// 无交互的自检也跑不完。Tauri 那边的 <c>Menu</c> 是资源，Rust 侧可以在另一条线程上
    /// 结束它；本库没有资源表，所以显式给一条命令。
    /// </para>
    /// <para>
    /// 三平台的落点：Windows 发 <c>EndMenu</c>（结束 TrackPopupMenu 的模态循环，等价于用户按 Esc）；
    /// macOS <c>cancelTracking</c>；GTK <c>gtk_menu_popdown</c>。
    /// </para>
    /// </remarks>
    void CloseContextMenu();

    /// <summary>
    /// 设置**这个窗口**的菜单栏（页面侧 <c>Menu.setAsWindowMenu</c>）。
    /// </summary>
    /// <param name="items">菜单栏的顶层项；空集合表示**移除**菜单栏。</param>
    /// <exception cref="NotSupportedException">
    /// macOS：菜单在 macOS 是应用级的，没有"某个窗口的菜单栏"这回事。
    /// 页面侧 <c>setAsWindowMenu</c> 在 macOS 上抛错，与 Tauri 的行为一致。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ShowContextMenu"/> 的区别是**寿命**：弹出是一次性的，菜单栏装上之后一直
    /// 在那儿，直到被替换或移除。这也意味着它比弹出更"重"——它改变窗口的外观与可用命令面。
    /// </para>
    /// <para>
    /// <b>菜单栏占不占客户区</b>：占。三平台都把菜单栏画在客户区之上，页面看到的
    /// <c>innerSize</c> 会比整窗矮一个菜单栏的高度（Windows 装饰窗口由 DefWindowProc 自动处理；
    /// 无边框窗口要宿主自己在 <c>WM_NCCALCSIZE</c> 里让出来；GTK 要重排成
    /// <c>GtkBox{menubar, webview}</c>）。这一点直接影响客户区尺寸契约，见 API.md。
    /// </para>
    /// </remarks>
    void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items);

    /// <summary>
    /// 让这个窗口反映**应用级**菜单栏（页面侧 <c>Menu.setAsAppMenu</c>）。
    /// </summary>
    /// <param name="items">应用菜单的顶层项；空集合表示移除。</param>
    /// <remarks>
    /// <para>
    /// 为什么放在窗口后端上而不是一个 app 级接口：三平台的落点形状完全不同，而它们都已经
    /// 有窗口宿主这个抓手——
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>macOS</b>：菜单是应用级的（<c>[NSApp setMainMenu:]</c>），
    ///     从哪个窗口调都一样，重复调是幂等的。</description></item>
    ///   <item><description><b>Windows / Linux</b>：菜单栏属于窗口，所以这个方法的语义是
    ///     "**把这一份内容装到我这个窗口上**"，同时由 <see cref="OrielApp"/> 记作新建窗口的默认值。</description></item>
    /// </list>
    /// <para>
    /// 已经用 <see cref="SetWindowMenuBar"/> 显式设过菜单栏的窗口**不被覆盖**（Tauri 的语义
    /// "had one set explicitly"），由调用方（<c>OrielApp</c>）负责判断，后端不重复记账。
    /// </para>
    /// </remarks>
    void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items);

    /// <summary>上下文菜单里的自定义项被点击，参数是该项的 <see cref="OrielMenuItem.Id"/>。</summary>
    /// <remarks>
    /// 菜单栏的自定义项点击也走这条（它们同样只有 id、同样要回到页面的
    /// <c>menu.itemclicked</c>）。名字里的 "ContextMenu" 是历史遗留——它比菜单栏早一批出现。
    /// </remarks>
    event Action<string>? ContextMenuItemClicked;

    /// <summary>
    /// 渲染引擎**内建**右键菜单的策略（与 <see cref="ShowContextMenu"/> 是两条独立通道）。
    /// </summary>
    /// <remarks>
    /// 做成可写属性而不是只走构造参数：过滤发生在**每次弹出时**，所以改完立刻生效，
    /// 不需要重建窗口。三平台实现都只在弹出回调里读它，不缓存。
    /// </remarks>
    OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>外部文件被拖进窗口（只有"松手那一刻"，对标 B8）。</summary>
    /// <remarks>
    /// 保留它是因为 B8 已经发布：应用可能直接订阅了它。<see cref="DragDrop"/> 是同一件事的
    /// 完整版本（四种阶段 + 坐标），新代码应当用后者。
    /// </remarks>
    event Action<OrielFileDropEventArgs>? FileDropped;

    /// <summary>
    /// 拖放的四个阶段（enter / over / drop / leave），带客户区物理像素坐标。
    /// </summary>
    /// <remarks>
    /// 平台能提供多少就报多少——**不允许"为了整齐"补假事件**：三平台都实现了四种才一起开
    /// 页面事件面（见 B10 的计划），所以走到页面时四种都是真的。
    /// </remarks>
    event Action<OrielDragEventArgs>? DragDrop;

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

    // 说明：消息框有两个入口，刻意不合并。
    //
    //   ShowMessageBox(text, title, icon) → void：既有的"通知型"消息框，只告诉用户一件事。
    //   ShowMessageDialog(…) → MessageResult：本批为 dialog.* 加的"询问型"消息框，
    //     需要知道用户点了哪个按钮。
    //
    // 不把前者改成返回结果：那会动到公开的 WebviewWindow.ShowMessageBox（void），
    // 而既有的调用方（demo 的上下文菜单等）并不关心结果。两套入口共享同一个原生调用
    // （下面各平台都是同一条 MessageBoxW / GtkMessageDialog / NSAlert 路径），
    // 所以不存在"两份实现漂移"的问题。

    /// <summary>
    /// 弹一个模态询问对话框，返回**用户点了哪个语义按钮**。
    /// </summary>
    /// <param name="message">正文。</param>
    /// <param name="title">标题；为 null 时用平台默认。</param>
    /// <param name="kind">语气：<c>info</c> / <c>warning</c> / <c>error</c> / <c>question</c>。</param>
    /// <param name="buttons">按钮集合。</param>
    /// <param name="okLabel">确认按钮的**显示**文案（不支持自定义按钮文案的平台会忽略它）。</param>
    /// <param name="cancelLabel">取消按钮的显示文案（同上）。</param>
    /// <returns>
    /// 语义结果，或 <c>null</c>（对话框被外部关掉、没有结果）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>为什么返回语义（<see cref="MessageResult.Ok"/> 这类）而不是按钮文案</b>：
    /// 三平台能显示的自定义文案不同——Windows 的 <c>MessageBoxW</c> **只能显示系统文案**
    /// （自定义要上 <c>TaskDialogIndirect</c>，那需要 comctl32 v6 激活清单）。
    /// 若命令返回"实际显示的文案"，页面拿它与自己期望的标签比对，在 Windows 上就会
    /// **静默得到错误答案**（用户点了"是"，但显示的是"确定"，比较不成立 → 当成否）。
    /// 返回语义之后，文案只是显示层的事，判断不再受它影响。
    /// </para>
    /// <para>
    /// <b>Windows 上自定义按钮文案不生效</b>（显示系统文案），这是平台限制，
    /// 已写进文档与 CHANGELOG 的"已知限制"。
    /// </para>
    /// </remarks>
    MessageResult? ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel);
}

/// <summary>请求用户注意的紧急程度（对标 Tauri 的 <c>UserAttentionType</c>）。</summary>
/// <remarks>
/// 取值与 Tauri 一致（<c>Critical = 1</c>、<c>Informational = 2</c>），
/// 这样页面侧的常量对象可以直接对得上。
/// <b>Linux 上两档没有区别</b>（GTK 只有开/关），见
/// <see cref="IWindowBackend.RequestUserAttention(OrielUserAttention?)"/>。
/// </remarks>
public enum OrielUserAttention
{
    /// <summary>持续提示直到用户来看（Windows 一直闪到激活、macOS 弹跳）。</summary>
    Critical = 1,

    /// <summary>短暂提示一下。</summary>
    Informational = 2,
}

/// <summary>消息对话框的按钮集合。</summary>
/// <remarks>
/// 用枚举而不是"传一组文案"：三平台各自的按钮**顺序与默认按钮**有差别
/// （Windows 的 <c>MB_YESNOCANCEL</c> 顺序固定、GTK 按添加顺序、macOS 的按钮顺序决定
/// 哪个是回车默认），所以"哪个按钮是什么语义"必须由调用方说清，平台实现各自映射。
/// </remarks>
public enum MessageButtons
{
    /// <summary>只有确认。</summary>
    Ok,

    /// <summary>确认 + 取消。</summary>
    OkCancel,

    /// <summary>是 + 否。</summary>
    YesNo,

    /// <summary>是 + 否 + 取消。</summary>
    YesNoCancel,
}

/// <summary>用户点了哪个语义按钮。</summary>
public enum MessageResult
{
    /// <summary>确认。</summary>
    Ok,

    /// <summary>取消。</summary>
    Cancel,

    /// <summary>是。</summary>
    Yes,

    /// <summary>否。</summary>
    No,
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
    /// <returns>
    /// 是否**已成功投递给 UI 线程**。<c>false</c> 表示调度通道已失效（消息窗口被销毁、
    /// GTK 的 idle source 建不出来等），动作**不会被执行**——此时实现会经
    /// <see cref="OrielDiagnostics.Reported"/> 留一条痕迹，不再静默丢弃。
    /// </returns>
    /// <remarks>
    /// 返回 <c>false</c> 不等于抛异常：调用方（尤其退出途中）通常只该记一笔就继续。
    /// 旧形态是 <c>void</c>，投递失败时既没有返回信号也没有日志，自动化验证里表现为
    /// "设置了但没生效"的假通过（下游反馈 2026-10-14 的 P2-5）。
    /// </remarks>
    bool PostToMainThread(Action action);

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

    /// <summary>
    /// 创建托盘图标（一个应用可以有多个，各按 <paramref name="id"/> 区分）。
    /// </summary>
    /// <param name="options">创建选项。</param>
    /// <param name="app">所属应用。</param>
    /// <param name="id">本托盘的唯一标识（由 <see cref="OrielApp"/> 分配/校验，后端只负责记住它）。</param>
    ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app, string id);

    /// <summary>
    /// 显示器与光标的查询入口。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CreateTray"/> 同一套分层：应用级能力各自一个子后端，而不是把
    /// 每个平台知识都堆进本接口。实现在应用运行期间保持不变，可以安全缓存引用。
    /// 见 <see cref="IMonitorBackend"/> 里关于"为什么单独一个接口"与线程要求的说明。
    /// </remarks>
    IMonitorBackend Monitors { get; }

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

    // ------------------------------------------------------------------ 系统剪贴板
    //
    // 只做**纯文本**。富格式（HTML）与图片刻意不做：
    //
    //   * HTML 在 Windows 上是 CF_HTML——一个带字节偏移头的私有格式，偏移写错会让
    //     粘贴目标是乱码或截断，而那个头必须按**目标**平台重算。三平台各一套规则，
    //     收益（一次粘贴的格式）远小于风险（静默写坏数据）。
    //   * 图片需要一套图像句柄/资源模型，本库没有（见 README 里"没有句柄表"的说明）。
    //
    // 这两个缺口记在 docs/TAURI2-PLAN.md 里，不是遗漏。

    /// <summary>
    /// 读系统剪贴板里的文本；没有文本内容时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>返回 null 而不是空串</b>：剪贴板里可能是图片、文件列表，或者就是空的——
    /// 而"空串"是一个合法的文本内容。调用方要能区分"没有文本"与"文本是空的"。
    /// </remarks>
    string? ReadClipboardText();

    /// <summary>把文本写进系统剪贴板；失败时返回 <c>false</c>。</summary>
    /// <remarks>
    /// 失败不抛异常（与通知一致）：剪贴板被别的进程占着是**正常状态**
    /// （Windows 上一次只有一个进程能打开剪贴板），不是调用方的错误。
    /// </remarks>
    bool WriteClipboardText(string text);

    /// <summary>清空系统剪贴板；失败时返回 <c>false</c>。</summary>
    bool ClearClipboard();

    /// <summary>
    /// 读剪贴板里的图片，返回 **PNG 编码**的字节；剪贴板里没有图片时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回**编码后的 PNG** 而不是裸像素：调用方（命令层）要把它交给页面侧的
    /// <c>Image.fromBytes</c>，而那里的 <c>Image</c> 持有的就是编码字节——
    /// 中间换成裸像素只是多一次转换、并让"图片尺寸"这件事在跨进程边界上失去依据。
    /// </para>
    /// <para>
    /// 各平台怎么拿到 PNG：Windows 读 <c>CF_DIB</c> 再自己编码（<see cref="OrielPngEncoder"/>）；
    /// macOS 优先取 <c>public.png</c>、否则把 <c>public.tiff</c> 交给 AppKit 转 PNG；
    /// Linux 从 <c>GdkPixbuf</c> 读裸像素再编码（<c>gdk_pixbuf_save_to_buffer</c> 是变参函数，
    /// 没法直接 P/Invoke）。
    /// </para>
    /// </remarks>
    byte[]? ReadClipboardImage();

    /// <summary>把 PNG 字节写进剪贴板；失败时返回 <c>false</c>（剪贴板被占用是正常状态）。</summary>
    /// <param name="png">PNG 编码的图片（命令层会把任意支持的格式统一转成 PNG 再进来）。</param>
    bool WriteClipboardImage(byte[] png);

    /// <summary>
    /// 写"富文本"：<paramref name="html"/> 作为 HTML，<paramref name="altText"/> 作为纯文本回退。
    /// </summary>
    /// <param name="html">HTML 片段（不需要 <c>&lt;html&gt;</c> 外壳）。</param>
    /// <param name="altText">不支持 HTML 的粘贴目标拿到的纯文本；为 <c>null</c> 时用 <paramref name="html"/> 原文。</param>
    /// <remarks>
    /// Windows 上这是 <c>CF_HTML</c>（带 UTF-8 字节偏移头的自描述格式，见
    /// <see cref="OrielClipboardHtml"/>）；macOS 上写 <c>public.html</c> + <c>public.utf8-plain-text</c>；
    /// Linux 上注册 <c>text/html</c> 与 <c>UTF8_STRING</c> 两个目标。
    /// </remarks>
    bool WriteClipboardHtml(string html, string? altText);

    /// <summary>
    /// 写一段"敏感文本"：除了正常文本，还告诉系统别把它记进历史/同步到别的设备。
    /// </summary>
    /// <remarks>
    /// 机制各平台不同：Windows 用 <c>CanIncludeInClipboardHistory=0</c> /
    /// <c>CanUploadToCloudClipboard=0</c> / <c>ExcludeClipboardContentFromMonitorProcessing</c>
    /// 三个标记格式；macOS 写 <c>org.nspasteboard.ConcealedType</c>；Linux 写
    /// <c>x-kde-passwordManagerHint=secret</c>。<b>都是"请求"，不是保证</b>——
    /// 剪贴板管理器可以不理（这与 Tauri 的说明一致）。
    /// </remarks>
    bool WriteClipboardSecret(string text);
}
