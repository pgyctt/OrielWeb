using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = DirectN.IDCompositionDevice;
using DCompFunctions = DirectN.Functions;
using DCompHWND = DirectN.HWND;
using DCompPoint = DirectN.POINT;
using DCompTarget = DirectN.IDCompositionTarget;
using DCompVisual = DirectN.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>webview 内容面：装配、导航、脚本、消息往返与页面事件。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    // ---- 导航操作（WebView2 内建历史；无历史时调用是空操作）----

    public bool CanGoBack => _webView?.CanGoBack ?? false;
    public bool CanGoForward => _webView?.CanGoForward ?? false;
    public void GoBack() => _webView?.GoBack();
    public void GoForward() => _webView?.GoForward();
    public void Reload() => _webView?.Reload();

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    // ------------------------------------------------------------------
    // WebView2 装配（异步回调均在 UI 线程）
    // ------------------------------------------------------------------

    internal void InitializeWebView2()
    {
        // 装配是异步的：立即返回，完成后继续（失败经 OnWebViewFailed 提示并销毁窗口）
        _ = InitializeWebView2Async();
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            EnsureLoaderInitialized();

            string? browserFolder = Environment.GetEnvironmentVariable("ORIEL_WEBVIEW2_FOLDER");

            // 先问 loader 能否找到运行时。这一步不能省：运行时缺失时
            // CreateCoreWebView2EnvironmentWithOptions 只会回一个"找不到文件"的 HRESULT，
            // 用户看到的是系统级文案，无从判断该装什么、去哪装。
            if (string.IsNullOrWhiteSpace(
                    WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString(browserFolder)))
            {
                // 必须延到消息循环启动后再处理：装配发生在 CreateWindow 内部，而
                // OrielApp.Run() 要等它返回才调用 window.Attach(backend)——此刻窗口门面
                // （ShowMessage/Close 等）还没有后端可用。延后处理保证回调拿到可用窗口。
                _backend.PostToMainThread(() => OnWebView2RuntimeMissing(browserFolder));
                return;
            }

            // 环境是**全进程共享**的（见 WindowsPlatformBackend.GetEnvironmentAsync）：
            // 每个窗口各建一个会撞在同一个 user data folder 上，而 WebView2 不允许同目录两个环境，
            // 第二个窗口会直接装配失败——"运行时再开一个窗口"因此在 Windows 上起不来。
            _environment = await _backend.GetEnvironmentAsync(browserFolder).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 环境失败（返回 null）。");

            _compositionController = await _environment.CreateCoreWebView2CompositionControllerAsync(_hwnd).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 组合控制器失败（返回 null）。");

            // 把 WebView 作为视觉挂进我们建立的 DirectComposition 树。此后它不再是子窗口，
            // 因此窗口能收到 WM_NCHITTEST —— 这是边缘调整大小得以走系统原生路径的关键。
            _compositionController.RootVisualTarget = _dcompVisual
                ?? throw new InvalidOperationException("DirectComposition 根视觉尚未建立。");

            // 挂上视觉后必须再提交一次，DComp 才会把它纳入当前的合成帧
            _dcompDevice?.Commit();

            // 组合控制器同时实现 ICoreWebView2Controller（bounds / visible / focus 等）
            _controller = _compositionController.Object as ICoreWebView2Controller
                ?? throw new InvalidOperationException("组合控制器未实现 ICoreWebView2Controller。");
            ApplyBackgroundColor();

            // 关掉 WebView2 自带的拖放：它默认会把外部文件拖放接管过去（转成页面的 drag 事件），
            // 而页面拿不到文件路径。关掉之后拖放落到窗口的 WM_DROPFILES，路径由原生侧给出。
            // 成员在 ICoreWebView2Controller4 上：老运行时没有这个接口，因此是可选增强（转换失败即跳过），
            // 这也避免"运行时版本决定行为"变成静默失败——跳过时下面的 WM_DROPFILES 路径仍然可用。
            if (_controller is ICoreWebView2Controller4 controller4)
            {
                controller4.AllowExternalDrop = false;
            }

            // 注册 OLE 拖放目标，拿 enter / over / leave（WM_DROPFILES 只在松手那刻回调一次）。
            // 必须在 AllowExternalDrop = false **之后**：否则 WebView2 可能已经占了窗口上的
            // IDropTarget，注册会以 DRAGDROP_E_ALREADYREGISTERED 失败。
            RegisterDropTarget();

            _compositionEvents = new CoreWebView2CompositionControllerEvents(_compositionController);
            _compositionEvents.CursorChanged += OnCursorChanged;

            _webView = _controller.CoreWebView2
                ?? throw new InvalidOperationException("获取 CoreWebView2 失败（返回 null）。");

            // 内嵌资源由 oriel:// 处理器应答（见 Win32AssetScheme）：旧实现是
            // SetVirtualHostNameToFolderMapping(host, 解压目录, DENY_CORS)，那要求资源先落盘，
            // 而且页面来源是 https 虚拟主机（与 Linux/macOS 的 file:// 不一致）。
            if (_assets is not null)
            {
                // 返回的事件处理器必须存着：托管侧没人引用它时，拦截会静默失效（页面白屏但无报错）。
                //
                // 第三个参数是 convertFileSrc 的授权器：**只接一个"这个绝对路径能不能读"的判定**，
                // 而 scope 与标识符留在应用侧（这里拿不到也不该知道）。传 null 也能跑，
                // 那样所有文件请求都会被 403——"没启用"绝不能被解释成"不限制"。
                _assetSchemeHandler = Win32AssetScheme.Attach(
                    _environment.Object,
                    _webView.Object,
                    _assets,
                    _assetHost,
                    absolutePath => App.Guard.TryAuthorizePath(
                        OrielAssetFileServer.ScopeCommand, absolutePath, App.Info.Identifier, out _));
            }

            if (_webView.Settings is { } settings)
            {
                using (settings)
                {
                    settings.AreDevToolsEnabled = _options.Debug;

                    // 缩放是**窗口**语义（见 OrielWindowOptions.Zoom）：关掉 Ctrl+滚轮 / Ctrl+± 与捏合。
                    // 文档层还有一道捕获期拦截（桥接脚本的缩放闸门），那是给没有等价开关的引擎备的。
                    settings.IsZoomControlEnabled = _options.Zoom;
                    settings.IsPinchZoomEnabled = _options.Zoom;
                }
            }

            _webViewEvents = new CoreWebView2Events(_webView);
            _webViewEvents.WebMessageReceived += OnWebMessageReceived;
            _webViewEvents.NavigationStarting += OnNavigationStarting;
            _webViewEvents.NavigationCompleted += OnNavigationCompleted;
            _webViewEvents.DocumentTitleChanged += OnDocumentTitleChanged;
            // 内建右键菜单：订阅它才有机会过滤（不订阅 = 平台原样弹）
            _webViewEvents.ContextMenuRequested += OnContextMenuRequested;

            await _webView.AddScriptToExecuteOnDocumentCreatedAsync(
                OrielBridgeJs.Build(
                    _options.ConsoleForwarding,
                    _options.Zoom,
                    App.Guard.Token,
                    App.Guard.TrustedPrefixes,
                    ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label))
                .ConfigureAwait(true);

            // 可见性按**意图态**恢复，而不是无条件置真：WithHidden() 起手时宿主已在 PostCreate 里
            // SW_HIDE，若这里硬置 true，那个顶层的 WebView2 渲染窗口（Chrome_WidgetWin_1）就会在
            // 首帧闸门期间（可达 8 秒）浮在桌面上吃点击——"程序还没显示出来就已经在吃点击"。
            // 闸门由宿主在 ui.ready 后调 Show() 打开（见 Hide() 的跨平台不变量说明）。
            _controller.IsVisible = _isVisible;
            UpdateBounds();

            // 内嵌资源的 URL 归一成 oriel://（兼容别名 https://<host>/… 也在这里统一），
            // 与 Linux/macOS 一样由 scheme 处理器应答。
            string url = _options.Url is { Length: > 0 } requested
                && AssetUrl.TryResolve(requested, _assetHost, out string relative)
                    ? AssetUrl.ForHost(_assetHost, relative)
                    : _options.Url ?? AssetUrl.DefaultDocument(_assetHost);
            _webView.Navigate(url);
        }
        catch (Exception ex)
        {
            OnWebViewFailed(DescribeWebView2Failure(ex, _backend.UserDataFolder));
        }
    }

    /// <summary>
    /// 把装配失败翻成"看得懂 + 能照做"的提示。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只有一条值得单独翻译：<c>0x800700AA</c>（ERROR_BUSY）/ <c>0x80070020</c>
    /// （ERROR_SHARING_VIOLATION）。它们的系统文案是"请求的资源在使用中"，而**"资源"在这里
    /// 指的是用户数据目录**——用户看到这句话时既不知道该关谁，也不知道该找哪个目录。
    /// 剩下的失败仍原样透出：给一个涵盖所有 HRESULT 的通用解释，只会把真正的原因埋掉。
    /// </para>
    /// </remarks>
    /// <param name="ex">装配时抛出的异常。</param>
    /// <param name="userDataFolder">本次应用的用户数据目录，用于把路径写进提示。</param>
    internal static string DescribeWebView2Failure(Exception ex, string userDataFolder)
    {
        const int ErrorBusy = unchecked((int)0x800700AA);
        const int ErrorSharingViolation = unchecked((int)0x80070020);

        if (ex.HResult is not (ErrorBusy or ErrorSharingViolation))
        {
            return $"初始化 WebView2 失败：{ex.Message}";
        }

        return "初始化 WebView2 失败：用户数据目录已被另一个进程占用。\n\n"
            + $"{userDataFolder}\n\n"
            + "同一个目录同一时刻只能有一个进程打开。按以下顺序检查：\n"
            + "1. 是否已经开着本应用的另一个实例（含最小化到托盘的）——关掉它再启动。\n"
            + "   需要「再启动一次就把前台实例叫回来」的话，构建时用 SingleInstance()。\n"
            + "2. 是否是另一个基于 OrielWeb 的应用在运行——用 UseUserDataFolder() 让它们各用各的目录。\n"
            + "3. 都排除了就等几秒重试：上一次异常退出时，浏览器进程可能还没退干净。";
    }

    /// <summary>
    /// 一次性把 WebView2Loader.dll 从本库的嵌入资源解压并加载（单文件发布友好）。
    /// </summary>
    /// <remarks>
    /// 传的是本库（而非入口程序集）的程序集：loader 由本库内嵌（见 OrielWeb.csproj），
    /// 消费方因此不必在自己的项目里做任何发布配置。若传入口程序集，只有恰好也内嵌了
    /// loader 的应用才能加载，与本库的自包含目标相悖。
    /// </remarks>
    private static void EnsureLoaderInitialized()
    {
        if (Interlocked.Exchange(ref s_loaderInitialized, 1) == 1)
        {
            return;
        }

        WebView2Utilities.Initialize(typeof(Win32WindowHost).Assembly);
    }

    private void OnWebMessageReceived(object? sender, ICoreWebView2WebMessageReceivedEventArgs args)
    {
        // 逐消息来源（纵深加固）：WebView2 给的"发消息的文档 URI"比顶级导航 URL 快照准——
        // 服务器重定向不触发 NavigationStarting、provisional 期间有新旧文档窗口期、子帧消息
        // 也各有来源。快照（CurrentUrl）降级为兜底。
        string? messageOrigin = args.Source;
        string? originUrl = string.IsNullOrEmpty(messageOrigin) ? CurrentUrl : messageOrigin;

        string? json = args.WebMessageAsJson;
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        if (OrielIpcLimits.ExceedsLimit(json))
        {
            // 入站规模闸（见 OrielIpcLimits）：超限就不解析、直接丢弃。拿不到 id 也就回不了执，
            // 至少让它走门禁的上报点（Debug + IpcRejected），宿主能知道"页面发了条大消息被丢了"。
            App.Guard.Report(OrielIpcRejectionLayer.Size, OrielIpcLimits.OversizeReason(json.Length), originUrl, null);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("__oriel", out var kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                // 非 OrielWeb 协议的消息（页面自己的 postMessage），忽略
                return;
            }

            switch (kind.GetString())
            {
                case "invoke":
                    // 门禁在分发器里做：它有回执通道，能把"为什么被拒"送回页面，
                    // 而不是让那个 Promise 干等到 30 秒超时。
                    _ = DispatchInvokeAsync(root.Clone(), originUrl);
                    break;
                case "console":
                    // 只有开启 console 转发时页面才会发这类消息（hook 由桥接脚本注入决定）
                    if (Accept(root, originUrl))
                    {
                        RaiseConsoleMessage(ReadStringProperty(root, "level"), ReadStringProperty(root, "text"));
                    }
                    break;
                case "message":
                    // 单向消息没有命令名，因此只过来源与令牌那一层；
                    // 命令授权（allow/deny）只管 invoke，原因见 OrielCapabilityOptions。
                    if (Accept(root, originUrl))
                    {
                        RaiseMessageReceived(
                            ReadStringProperty(root, "name"),
                            root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : "null");
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // 非 OrielWeb 消息（页面自定义 postMessage），忽略
        }
    }

    /// <summary>读字符串属性；缺失或类型不符时返回空串（页面数据不可信，一律按可缺席处理）。</summary>
    private static string ReadStringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>入站消息的来源 + 令牌校验（命令授权不在这里，见分发器）。originUrl 为逐消息来源，null 时退回快照。</summary>
    private bool Accept(JsonElement root, string? originUrl)
    {
        // 拒绝的记账在门禁内部（Debug 输出 + IpcRejected 事件），这里只返回结论。
        return App.Guard.TryAccept(originUrl ?? CurrentUrl, root, out _);
    }

    private async Task DispatchInvokeAsync(JsonElement message, string? originUrl)
    {
        try
        {
            await App.Dispatcher
                .HandleInvokeAsync(message, new UiThreadReplySink(this), originUrl ?? CurrentUrl, _window)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 分发器内部已按命令捕获并净化；此处兜底分发器之外的错误。
            // 同样不透传 ex.Message（可能含内部路径），文案与分发器保持同一口径。
            OrielDiagnostics.Report($"[OrielWeb] IPC 分发意外失败：{ex}");
            PostMessageOnUi($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":\"命令执行失败。\"}}");
        }
    }

    private void OnNavigationCompleted(object? sender, ICoreWebView2NavigationCompletedEventArgs args)
    {
        // 新文档不知道当前最大化状态，重置记忆以便下面推一次初始值
        _wasMaximized = !Win32.IsZoomed(_hwnd);
        SyncMaximizedState();

        bool success = args.IsSuccess;
        // WebErrorStatus 是枚举（如 CONNECTION_ABORTED / HOST_NAME_NOT_RESOLVED），
        // 这里只做字符串化——各平台文案不同，不试图统一成人话。
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(
            success,
            _lastNavigationUri,
            success ? null : args.WebErrorStatus.ToString()));

        if (success)
        {
            RaiseLoadedIfFirst();
        }
    }

    private void OnDocumentTitleChanged(object? sender, EventArgs args)
    {
        string? title = _webView?.DocumentTitle;
        if (!string.IsNullOrEmpty(title))
        {
            RaiseTitleChanged(title);
        }
    }

    internal void OnWebViewFailed(string message)
    {
        // 先走诊断通道，再弹窗：弹窗在**无人值守**的运行里是个坑——它会一直等一个
        // 永远不会有人点的"确定"，于是启动故障表现为"卡住"而不是"失败"。
        // 走到这里的场景（CI、后台启动、脚本拉起）不少，至少要让日志里有据可查。
        OrielDiagnostics.Report($"[OrielWeb] {message}");

        MessageBoxResult(message, "OrielWeb", OrielMessageBoxIcon.Error);
        Win32.DestroyWindow(_hwnd);
    }

    /// <summary>
    /// WebView2 运行时不可用时的处置：优先交给应用注册的回调，未注册（或回调抛异常）则弹库的默认提示。
    /// </summary>
    /// <remarks>
    /// 库自身不引导安装：弹什么、要不要静默装、能不能自动装都是应用策略（企业环境可能禁止联网安装）。
    /// 库只负责把"缺运行时"这个事实准确暴露出来，并给出默认的、可照做的提示。
    /// </remarks>
    private void OnWebView2RuntimeMissing(string? browserFolder)
    {
        var handler = _app.WebView2RuntimeMissingHandler;
        bool handled = false;
        bool keepWindowOpen = false;

        if (handler is not null)
        {
            var args = new OrielWebView2RuntimeMissingEventArgs(_window, browserFolder);
            try
            {
                handler(args);
                handled = true;
                keepWindowOpen = args.KeepWindowOpen;
            }
            catch (Exception ex)
            {
                // 回调自身的异常不该夺走用户的知情权：回落到默认提示
                OrielDiagnostics.Report($"[OrielWeb] OnWebView2RuntimeMissing 回调抛出异常，改用默认提示：{ex}");
            }
        }

        if (!handled)
        {
            MessageBoxResult(RuntimeMissingMessage(browserFolder), "OrielWeb", OrielMessageBoxIcon.Error);
        }

        // 没有 WebView2 的窗口没有内容可按；销毁它即让应用退出，与"装完再启动"的流程一致。
        if (!keepWindowOpen)
        {
            Win32.DestroyWindow(_hwnd);
        }
    }

    /// <summary>默认提示：区分"系统未装 Evergreen 运行时"与"指定的固定版本目录无效"两种情形。</summary>
    private static string RuntimeMissingMessage(string? browserFolder) =>
        browserFolder is null
            ? "未检测到 Microsoft Edge WebView2 运行时，界面无法显示。\r\n\r\n" +
              "请安装 WebView2 运行时后重新启动本应用：\r\n" +
              OrielWebView2RuntimeMissingEventArgs.DownloadUrl
            : "未在指定目录找到 WebView2 运行时，界面无法显示。\r\n\r\n" +
              $"目录：{browserFolder}\r\n\r\n" +
              "请确认环境变量 ORIEL_WEBVIEW2_FOLDER 指向有效的固定版本运行时目录；" +
              "如需改用系统 Evergreen 运行时，请清除该变量。";

    internal void RaiseLoadedIfFirst()
    {
        if (!_loadedRaised)
        {
            _loadedRaised = true;
            Loaded?.Invoke();
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        // 显式设过标题就不再跟随页面标题（理由见 _titleSetExplicitly 的说明）
        if (_titleSetExplicitly)
        {
            return;
        }

        _title = title;
        Win32.SetWindowTextW(_hwnd, title); // 文档标题 → 窗口标题同步
        TitleChanged?.Invoke(title);
    }

    private void OnNavigationStarting(object? sender, ICoreWebView2NavigationStartingEventArgs args)
    {
        RaiseNavigationStarting(args.Uri ?? string.Empty);
    }

    internal void RaiseNavigationStarting(string url)
    {
        // _lastNavigationUri 同时充当"当前文档 URL"：入站 IPC 要做来源校验，
        // 而消息本身不带来源，只能由"这个窗口现在停在哪个 URL"来回答
        // （它原本是给 NavigationCompleted 补 URL 用的——WebView2 的完成事件里没有 URL）。
        _lastNavigationUri = url;
        NavigationStarting?.Invoke(url);
        PushEventOnUi("navigation.starting", $"{{\"url\":{JsonText.EncodeString(url)}}}");
    }

    /// <summary>当前文档的 URL（导航开始时更新）。入站 IPC 的来源校验用它。</summary>
    internal string? CurrentUrl => _lastNavigationUri;

    /// <summary>
    /// 应用"内核就绪前的窗口底色"（见 <see cref="OrielWindowOptions.BackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 用 WebView2 的 <c>DefaultBackgroundColor</c>（<c>ICoreWebView2Controller2</c>，运行时 1.0.774+）。
    /// **不用窗口类的背景刷子**：本窗口是 <c>WS_EX_NOREDIRECTIONBITMAP</c> + DirectComposition 宿主，
    /// 没有系统绘制的表面，没人会去画那个刷子——只有引擎自己知道首帧之前该铺什么色。
    /// 拿不到那个接口（老运行时）或设置失败时按默认处理并记一笔：底色是装饰性的，不该影响窗口本身。
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        // 运行期设过就以它为准（后设的赢）；没设过才回落到创建期选项。
        uint? effective = _runtimeBackgroundArgb;
        if (effective is null && _options.TryGetBackgroundArgb(out byte ca, out byte cr, out byte cg, out byte cb))
        {
            effective = ((uint)ca << 24) | ((uint)cr << 16) | ((uint)cg << 8) | cb;
        }

        if (effective is not { } argb || _controller is null)
        {
            return;
        }

        OrielWindowOptions.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);

        try
        {
            if (WebView2Utilities.GetInterface<ICoreWebView2Controller2>(_controller) is not { } controller2)
            {
                OrielDiagnostics.Report("[OrielWeb] 当前 WebView2 运行时没有 ICoreWebView2Controller2，BackgroundColor 未生效。");
                return;
            }

            controller2.put_DefaultBackgroundColor(new COREWEBVIEW2_COLOR { A = a, R = r, G = g, B = b }).ThrowOnError();
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 设置窗口底色失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 快照里**没有**缩放：拖动与双击用的都是页面 CSS 像素（Win32 侧同理，坐标到页面之前已经换算过），
    /// 再注入一份 scale 只会诱导页面自己折算一次。取不到时返回 0，由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 兜底。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
        => OrielSystemSnapshot.Normalize((int)Win32.GetDoubleClickTime());

    internal void RaiseNavigationCompleted(OrielNavigationCompletedEventArgs args)
    {
        NavigationCompleted?.Invoke(args);
        string error = args.Error is null ? "null" : JsonText.EncodeString(args.Error);
        PushEventOnUi(
            "navigation.completed",
            $"{{\"success\":{(args.Success ? "true" : "false")},\"url\":{JsonText.EncodeString(args.Url)},\"error\":{error}}}");
    }

    internal void RaiseConsoleMessage(string level, string text)
        => ConsoleMessage?.Invoke(new OrielConsoleMessageEventArgs(level, text));

    internal void RaiseMessageReceived(string name, string json)
        => MessageReceived?.Invoke(new OrielMessageReceivedEventArgs(name, json));

    /// <summary>
    /// 向页面推送一个 oriel 事件（页面侧 <c>window.oriel.on(name, handler)</c> 接收）。
    /// 与回执通道 <see cref="PostMessageOnUi"/> 分开：那条只投递 <c>{"__oriel":"result",…}</c> 形态。
    /// </summary>
    private void PushEventOnUi(string name, string jsonValue)
    {
        // **必须切回 UI 线程**：WebView2 的 COM 绑定在 STA，`PostWebMessageAsJson` 在别的线程上
        // 会抛，而下面的 catch 只报告不抛——事件就静默消失了。回执通道（UiThreadReplySink）
        // 一直有这条守卫，事件通道此前没有：直到 B9 的通道结束事件从
        // `ConfigureAwait(false)` 之后的线程池线程发出，才暴露出来（症状是"数据事件全到、
        // 结束事件永远不来"，且只有 Windows 复现——另两个平台的 evaluateJavaScript 接受跨线程调用）。
        if (!IsOnUiThread())
        {
            PostToUiThread(() => PushEventOnUi(name, jsonValue));
            return;
        }

        PostMessageOnUi(
            $"{{\"__oriel\":\"event\",\"name\":{JsonText.EncodeString(name)},\"value\":{jsonValue}}}");
    }

    internal void PostWebMessageOnUi(string json) => PostMessageOnUi(json);

    private void PostMessageOnUi(string json)
    {
        try
        {
            _webView?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // 窗口销毁后的迟到回执，忽略
            OrielDiagnostics.Report($"[OrielWeb] 投递回执失败（窗口可能已销毁）：{ex.Message}");
        }
    }

    /// <summary>清除全部浏览数据（见 <see cref="IWindowBackend.ClearAllBrowsingData"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 落点是 <c>ICoreWebView2Profile.ClearBrowsingDataAsync(All)</c>
    /// （WebView2Aot 的 <c>ClearBrowsingDataAllAsync</c> 扩展）——清的是
    /// <c>WithAppInfo</c> 决定的那份 profile 目录，不涉及其他应用。
    /// </para>
    /// <para>
    /// 该 API 要求 WebView2 运行时支持 <c>ICoreWebView2Profile2</c>（运行时 1.0.1108+，
    /// 2022 年的版本）：老运行时会抛 COM 异常，**进诊断日志**而不是静默消失——
    /// "调了没生效"必须可观测。
    /// </para>
    /// </remarks>
    public void ClearAllBrowsingData()
    {
        if (_webView is null)
        {
            return;
        }

        // Profile 是 WebView2Aot 的扩展属性（IComObject<ICoreWebView2> → IComObject<ICoreWebView2Profile>?）。
        IComObject<ICoreWebView2Profile>? profile = _webView.Profile;
        if (profile is null)
        {
            OrielDiagnostics.Report("[OrielWeb] clearAllBrowsingData：拿不到 ICoreWebView2Profile，未清除。");
            return;
        }

        _ = ClearBrowsingDataSafeAsync(profile);
    }

    private static async Task ClearBrowsingDataSafeAsync(IComObject<ICoreWebView2Profile> profile)
    {
        try
        {
            await profile.ClearBrowsingDataAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] clearAllBrowsingData 失败（运行时可能不支持 Profile2）：{ex.Message}");
        }
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        var webView = _webView
            ?? throw new InvalidOperationException("WebView2 尚未就绪。");

        return await webView.ExecuteScriptAsync(script).ConfigureAwait(true) ?? "null";
    }

    public void PostMessageAsJson(string json) => _backend.PostToMainThread(() => PostMessageOnUi(json));
}
