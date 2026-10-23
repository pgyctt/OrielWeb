using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>创建流程：原生窗口/视图装配、初始样式与图标。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    /// <summary>
    /// 按 <see cref="OrielWindowOptions.DataStoreIdentifier"/> 给 webview 绑定一份隔离的浏览数据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>+[WKWebsiteDataStore dataStoreForIdentifier:]</c> 是 macOS 12+ 的**类方法**
    /// （返回一个按 UUID 隔离的存储）。同一个 UUID 的多个窗口共享 cookie / localStorage，
    /// 不同 UUID 彼此隔离——这就是 Tauri 的 <c>data_store_identifier</c> 在 macOS 上的全部含义。
    /// </para>
    /// <para>
    /// <b>先探测再发消息</b>：旧系统上这个类方法不存在，向它发消息会抛
    /// <c>NSInvalidArgumentException</c>（ObjC 异常，托管侧接不住）。探测不到时记一条诊断并
    /// 沿用默认存储——窗口照常可用，只是没有隔离。
    /// </para>
    /// </remarks>
    private void ApplyDataStoreIdentifier(nint config)
    {
        if (_options.DataStoreIdentifier is not Guid identifier)
        {
            return;
        }

        nint storeClass = ObjCRuntime.GetClassOrThrow("WKWebsiteDataStore");
        if (!ObjCRuntime.ClassRespondsToSelector(storeClass, "dataStoreForIdentifier:"))
        {
            OrielDiagnostics.Report(
                "[OrielWeb] DataStoreIdentifier 需要 macOS 12 或更高版本："
                + "本窗口沿用默认数据存储（没有隔离）。");
            return;
        }

        nint store = ObjCRuntime.SendIdObj(
            storeClass,
            ObjCRuntime.Sel("dataStoreForIdentifier:"),
            ObjCRuntime.MakeNSUUID(identifier));

        if (store == 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] DataStoreIdentifier 绑定失败（{identifier:D}），本窗口沿用默认数据存储。");
            return;
        }

        ObjCRuntime.SendVoidObj(config, ObjCRuntime.Sel("setWebsiteDataStore:"), store);
    }

    internal void Create()
    {
        // 取类即校验：类不存在时宁可显式报错，也不要让后面一长串 objc_msgSend 静默 no-op
        var clsWindow = ObjCRuntime.GetClassOrThrow("NSWindow");
        var clsWKWebView = ObjCRuntime.GetClassOrThrow("WKWebView");
        var clsWKWebViewConfiguration = ObjCRuntime.GetClassOrThrow("WKWebViewConfiguration");
        var clsWKUserScript = ObjCRuntime.GetClassOrThrow("WKUserScript");

        double width = _options.Width;
        double height = _options.Height;

        nuint style = StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable;
        if (_options.Frameless)
        {
            style |= StyleFullSizeContentView;
        }

        _nsWindow = ObjCRuntime.SendIdRectNuintNuintBool(
            ObjCRuntime.SendId(clsWindow, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithContentRect:styleMask:backing:defer:"),
            new ObjCRuntime.NSRect(0, 0, width, height),
            style,
            2, // NSBackingStoreBuffered
            false);
        ObjCRuntime.objc_retain(_nsWindow);

        _nsWindowDelegate = MacOSObjCClasses.CreateWindowDelegate(this);
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setDelegate:"), _nsWindowDelegate);

        SetTitle(_options.Title);

        // 无边框外观：透明标题栏 + 隐藏系统按钮（页面提供自绘控制）
        if (_options.Frameless)
        {
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), true);
            ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setTitleVisibility:"), NSWindowTitleHidden);
            HideStandardButton(NSWindowCloseButton);
            HideStandardButton(NSWindowMiniaturizeButton);
            HideStandardButton(NSWindowZoomButton);
        }

        // ---- WKWebView ----
        var config = ObjCRuntime.SendId(
            ObjCRuntime.SendId(clsWKWebViewConfiguration, ObjCRuntime.Sel("alloc")), ObjCRuntime.Sel("init"));

        // 数据存储（B23）：必须在创建 WKWebView **之前**挂到 config 上——
        // webview 一旦建出来，它的 dataStore 就固定了，之后改 config 不生效。
        ApplyDataStoreIdentifier(config);

        var userContentController = ObjCRuntime.SendId(config, ObjCRuntime.Sel("userContentController"));

        _scriptHandler = MacOSObjCClasses.CreateScriptHandler(_messageHandler);
        ObjCRuntime.SendVoidObjObj(
            userContentController,
            ObjCRuntime.Sel("addScriptMessageHandler:name:"),
            _scriptHandler,
            ObjCRuntime.MakeNSString("oriel"));

        // 桥接脚本（document 创建时注入）
        // initWithSource: 要的是 NSString*（不是 const char*）：直接传 C# 字符串会被
        // marshal 成 char*，WebKit 内部校验对象类型时 __CF_IS_OBJC 直接 trap。
        var userScript = ObjCRuntime.SendIdObjNintBool(
            ObjCRuntime.SendId(clsWKUserScript, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            ObjCRuntime.MakeNSString(
                MacOSBridgeJs.Build(
                    _options.ConsoleForwarding,
                    _options.Zoom,
                    App.Guard.Token,
                    App.Guard.TrustedPrefixes,
                    ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label)),
            0, // WKUserScriptInjectionTimeAtDocumentStart
            true);
        ObjCRuntime.SendVoidObj(userContentController, ObjCRuntime.Sel("addUserScript:"), userScript);

        // oriel:// 的处理器必须挂在 config 上，且必须在创建 webview **之前**（见 MacOSObjCClasses）。
        if (_assets is not null)
        {
            _assetSchemeHandler = MacOSObjCClasses.CreateAssetSchemeHandler(_assets, _app.AssetHost);
            ObjCRuntime.SendVoidObjObj(
                config,
                ObjCRuntime.Sel("setURLSchemeHandler:forURLScheme:"),
                _assetSchemeHandler,
                ObjCRuntime.MakeNSString(AssetUrl.Scheme));
        }

        _webview = ObjCRuntime.SendIdRectObj(
            ObjCRuntime.SendId(clsWKWebView, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithFrame:configuration:"),
            new ObjCRuntime.NSRect(0, 0, width, height),
            config);
        ObjCRuntime.objc_retain(_webview);

        // DevTools：与 Windows 的 AreDevToolsEnabled 语义一致——只"允许"检查（Safari 的
        // 「开发」菜单里能看到这个 webview），不自动打开面板。isInspectable 是 macOS 13.3+
        // 的公开 API，更早的系统没有这个方法，故先 respondsToSelector: 探测再调用——
        // 向不认识的 selector 发消息会直接 crash。
        if (ObjCRuntime.SendBoolRetObj(_webview, ObjCRuntime.Sel("respondsToSelector:"), ObjCRuntime.Sel("setInspectable:")))
        {
            ObjCRuntime.SendVoidBool(_webview, ObjCRuntime.Sel("setInspectable:"), _options.Debug);
        }

        // 缩放是**窗口**语义（见 OrielWindowOptions.Zoom）：关掉触摸板捏合与智能缩放。
        // allowsMagnification 是 WKWebView 的公开属性（10.10+），无需能力探测；
        // Ctrl+滚轮那一路在 macOS 上不是引擎默认行为，由桥接脚本的缩放闸门兜住。
        ObjCRuntime.SendVoidBool(_webview, ObjCRuntime.Sel("setAllowsMagnification:"), _options.Zoom);

        // 内核就绪前的底色（见 OrielWindowOptions.BackgroundColor）。创建期与运行期共用
        // ApplyBackgroundColor——AppKit 那边两边都是同一组 setter。
        ApplyBackgroundColor();

        _navigationDelegate = MacOSObjCClasses.CreateNavigationDelegate(this);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setNavigationDelegate:"), _navigationDelegate);

        // UI 委托：唯一用途是 willOpenMenu:（内建右键菜单弹出前过滤）
        _uiDelegate = MacOSObjCClasses.CreateUIDelegate(this);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setUIDelegate:"), _uiDelegate);

        // 视图层级：contentView → OrielDropView（收拖放）→ WKWebView（渲染页面）。
        // 中间这层是拖放所必需的：协议方法得由"注册了 dragged types 的 view"实现，
        // 而 AppKit 会沿父视图链找到它（见 MacOSObjCClasses.BuildDropView 的说明）。
        var contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));

        _dropView = MacOSObjCClasses.CreateDropView(this);
        ObjCRuntime.SendVoidRect(
            _dropView, ObjCRuntime.Sel("setFrame:"), new ObjCRuntime.NSRect(0, 0, width, height));
        ObjCRuntime.SendVoidObj(contentView, ObjCRuntime.Sel("addSubview:"), _dropView);
        ObjCRuntime.SendVoidNint(_dropView, ObjCRuntime.Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewHeightSizable);

        // 只受理文件 URL："public.file-url" 是 NSPasteboardTypeFileURL 的底层值
        ObjCRuntime.SendVoidObj(
            _dropView,
            ObjCRuntime.Sel("registerForDraggedTypes:"),
            MakeStringArray(["public.file-url"]));

        // WebView 填满容器，并随容器尺寸自动调整
        ObjCRuntime.SendVoidRect(
            _webview, ObjCRuntime.Sel("setFrame:"), new ObjCRuntime.NSRect(0, 0, width, height));
        ObjCRuntime.SendVoidObj(_dropView, ObjCRuntime.Sel("addSubview:"), _webview);
        ObjCRuntime.SendVoidNint(_webview, ObjCRuntime.Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewHeightSizable);

        // 最小尺寸
        if (_options.MinWidth is int minWidth || _options.MinHeight is int minHeight)
        {
            ObjCRuntime.SendVoidDouble2(
                _nsWindow,
                ObjCRuntime.Sel("setContentMinSize:"),
                _options.MinWidth ?? 0,
                _options.MinHeight ?? 0);
        }

        if (_options.X is not null || _options.Y is not null)
        {
            // At(x,y)：顶左角落在屏幕逻辑坐标 (x,y)——与 Windows（CreateWindowExW 的 x/y）
            // 语义对齐。At 一次给两个值，这里按可空处理只是防御只给一维的写法。
            double maxY = ScreenMaxY();
            ObjCRuntime.SendVoidDouble2(
                _nsWindow,
                ObjCRuntime.Sel("setFrameTopLeftPoint:"),
                _options.X.GetValueOrDefault(),
                maxY - _options.Y.GetValueOrDefault());
        }
        else if (_options.Center)
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));
        }

        if (!_options.Hidden)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
            ObjCRuntime.SendVoidBool(SharedApplication(), ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
        }
        else
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("orderOut:"), 0);
        }

        if (_options.Maximized)
        {
            Maximize();
        }
        if (_options.Fullscreen)
        {
            SetFullscreen(true);
        }

        // 图标：macOS 没有窗口级图标，能设的只有应用（Dock）图标——平台事实，不是偷懒。
        // 运行期换图标走 SetIcon，两边共用 ApplyIconToApplication。
        if (!string.IsNullOrEmpty(_options.Icon))
        {
            ApplyIconToApplication(_options.Icon);
        }

        // 底色（WithBackgroundColor）在创建期由上面那处 ApplyBackgroundColor() 应用。

        Navigate();
    }

    /// <summary>把图标文件设成**应用（Dock）图标**（见 <see cref="IWindowBackend.SetIcon"/>）。</summary>
    /// <remarks>
    /// <c>initWithContentsOfFile:</c> 要的是 <c>NSString*</c>（不是 <c>char*</c>）：必须用
    /// <c>MakeNSString</c> 造对象，直接传 C# 字符串会被 marshal 成 <c>char*</c>，
    /// ObjC 侧一问类型就 trap（见 ObjCRuntime 的注释）。
    /// <para>
    /// 路径不存在时 <c>initWithContentsOfFile:</c> 返回 nil → 这里静默保持原图标，
    /// 与创建期行为一致（那边也是"设不上就不设"）。
    /// </para>
    /// </remarks>
    private static void ApplyIconToApplication(string path)
    {
        nint image = ObjCRuntime.SendIdObj(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSImage"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithContentsOfFile:"),
            ObjCRuntime.MakeNSString(path));
        if (image != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), image);
        }
    }

    private static nint SharedApplication()
        => ObjCRuntime.SendId(ObjCRuntime.GetClass("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
}
