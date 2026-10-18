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
    /// 与 <see cref="IsResizable"/> 那几条一样取**创建时的选项**（<c>Frameless</c> 的取反）：
    /// 它在窗口生命期内不变，而三平台从原生读回它的代价与可靠性都不一样
    /// （Windows 上是我们的 <c>WM_NCCALCSIZE</c> 处理，读样式位读不出这个意图；
    /// GTK 的 getter 报的是**请求值**，Wayland 下合成器可能无视它）。
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
    /// 设置窗口的最大尺寸（逻辑像素）。传 0 表示不限制。
    /// </summary>
    /// <remarks>
    /// <b>与 <c>SetMinSize</c> 是同一套机制的两端</b>，但三平台落点不同：
    /// Windows 在 <c>WM_GETMINMAXINFO</c> 里同时处理 min 与 max（那边本来就有这个处理）、
    /// Cocoa 是 <c>setContentMaxSize:</c>、GTK 走 <c>gtk_window_set_geometry_hints</c>
    /// ——**只声明 max**，不动既有的 <c>set_size_request</c> min 机制。
    /// </remarks>
    void SetMaxSize(int width, int height);

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
}
