using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 窗口宿主：NSWindow + WKWebView，全部经 objc_msgSend。
/// 窗口原点/尺寸由 JS 侧提供 + 宿主跟踪增量（不做 struct 返回的 msgSend）。
/// </summary>
internal sealed partial class MacOSWindowHost : IWindowBackend
{
    // NSWindowStyleMask
    private const nuint StyleTitled = 1 << 0;
    private const nuint StyleClosable = 1 << 1;
    private const nuint StyleMiniaturizable = 1 << 2;
    private const nuint StyleResizable = 1 << 3;
    private const nuint StyleFullSizeContentView = 1 << 15;

    private const nint NSWindowCloseButton = 0;
    private const nint NSWindowMiniaturizeButton = 1;
    private const nint NSWindowZoomButton = 2;

    private const nint NSWindowTitleHidden = 1;
    private const nint NSFloatingWindowLevel = 3; // kCGFloatingWindowLevel（置顶）
    private const nint NSViewWidthSizable = 2;
    private const nint NSViewHeightSizable = 16;

    private readonly WebviewWindow _window;
    private readonly OrielWindowOptions _options;
    private readonly OrielApp _app;

    /// <summary>
    /// 本窗口的门面对象。内建 <c>win.*</c> 命令要靠它作用到**发起调用的那个窗口**上
    /// （多窗口下不能用一个全局的"当前窗口"代替）。
    /// </summary>
    internal WebviewWindow Window => _window;
    private readonly MacOSPlatformBackend _backend;
    private readonly EmbeddedAssetStore? _assets;

    /// <summary>oriel:// 的 scheme 处理器（挂在 config 上，必须活到 webview 销毁）。</summary>
    private nint _assetSchemeHandler;
    private readonly MacOSWebMessageHandler _messageHandler;

    private nint _nsWindow;
    private nint _nsWindowDelegate;
    private nint _webview;

    /// <summary>运行期设过的窗口底色（<c>setBackgroundColor</c>）；<c>null</c> 表示没设过，用于回落创建期选项。</summary>
    private uint? _runtimeBackgroundArgb;
    private nint _navigationDelegate;
    private nint _scriptHandler;
    /// <summary>承载拖放的容器视图（webview 是它的子视图）。</summary>
    private nint _dropView;
    /// <summary>UI 委托：目前只为 <c>willOpenMenu:</c>（过滤内建右键菜单）而存在。</summary>
    private nint _uiDelegate;
    private volatile bool _loadedRaised;

    // 无边框/窗口几何跟踪（点；cocoa 坐标原点在左下）
    private double _cocoaX;
    private double _cocoaY;
    private (double X, double Y)? _dragPointerStart;
    private (double X, double Y)? _dragWindowOrigin;

    private int _minWidth;
    private int _minHeight;

    /// <summary>最大尺寸（逻辑像素，0 = 不限制）。与 <c>_minWidth</c> / <c>_minHeight</c> 配对。</summary>
    private int _maxWidth;
    private int _maxHeight;

    /// <summary>已发出的"请求注意"请求 id（<c>cancelUserAttentionRequest:</c> 要用）。</summary>
    /// <remarks>
    /// macOS 的 <c>requestUserAttention:</c> 返回一个 id，取消时要把它交回去——
    /// 这与另两个平台（"取消"是个无参动作）不同，所以要记下来。
    /// </remarks>
    private nint _attentionRequestId;

    private bool _isFullscreen;
    private bool _isOnTop;
    private string _title;

    /// <summary>
    /// 是否由 API（<see cref="SetTitle"/>）显式设过标题。
    /// </summary>
    /// <remarks>
    /// 置位后不再让页面 <c>&lt;title&gt;</c> 覆盖窗口标题——显式设置优先于自动跟随。
    /// 三平台同一语义（否则"页面标题何时能覆盖"会变成平台相关的隐式规则）。
    /// </remarks>
    private bool _titleSetExplicitly;
    private int _evalSeq;

    // 上一次已上报（托管事件 + 页面）的最大化状态。NSWindow 的 zoom 是同步生效的，
    // 这个记忆值用于去重：windowDidResize / ToggleMaximize / 导航完成都可能触发同步。
    private bool _wasMaximized;
    // 页面是否已加载完成（桥接脚本就绪）。就绪前不推事件；加载完成时会强制补推一次当前状态。
    private bool _pageReady;

    // 上一次已上报的客户区尺寸 / 窗口位置（逻辑像素）。windowDidResize / windowDidMove
    // 在拖动时每个像素来一次，不去重就是把每秒几百次 ObjC 回调变成同样数量的托管事件 + 页面 IPC。
    private (double Width, double Height)? _lastReportedSize;
    private (double X, double Y)? _lastReportedPosition;
    // 上一次已上报的焦点状态；null 表示"还没报过"（首个 didBecomeKey 必须报，哪怕值相同）
    private bool? _lastReportedFocus;

    /// <summary>上一次已上报的缩放比；null 表示"还没报过"。</summary>
    private double? _lastReportedScaleFactor;

    // 强制销毁中：Destroy() 置它，用来让关闭路径上的判断看出"这次不受拦"。
    private bool _forceDestroy;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;
    private event Action<bool>? MaximizedChanged;
    private event Action<OrielSizeChangedEventArgs>? Resized;
    private event Action<OrielPositionChangedEventArgs>? Moved;
    private event Action<bool>? FocusChanged;
    private event Action<double>? ScaleFactorChanged;
    private event Action<string>? NavigationStarting;
    private event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted;
    private event Action<OrielConsoleMessageEventArgs>? ConsoleMessage;
    private event Action<OrielMessageReceivedEventArgs>? MessageReceived;
    private event Action<string>? ContextMenuItemClicked;
    private event Action<OrielFileDropEventArgs>? FileDropped;

    internal MacOSWindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, MacOSPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app;
        _backend = backend;
        _assets = assets;
        _title = options.Title;
        // 策略是可写属性（运行时能改），初值取自 options
        ContextMenuPolicy = options.ContextMenuPolicy;
        CloseInterceptEnabled = options.InterceptClose;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
        // 运行期可改（SetDecorations）；**注意 macOS 无边框窗口的 styleMask 里仍然带 Titled**
        // （无边框是"Titled + 透明标题栏 + 隐藏按钮 + fullSizeContentView"），
        // 所以不能用 styleMask 推断"有没有装饰"——那会把无边框窗口读成有装饰。
        _decorations = !options.Frameless;
        _messageHandler = new MacOSWebMessageHandler(this);
    }

    public nint NativeWindowHandle => _nsWindow;

    public OrielApp App => _app;

    /// <summary>本窗口的 label（见 <see cref="OrielWindowOptions.Label"/>）。</summary>
    public string Label => _options.Label;
    /// <remarks>本类为 internal，public 成员不增加对外 API 面；它实现 <see cref="IWindowBackend.IsOnUiThread"/>。</remarks>
    public bool IsOnUiThread() => _backend.IsOnUiThread();

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }
    event Action<bool>? IWindowBackend.MaximizedChanged { add => MaximizedChanged += value; remove => MaximizedChanged -= value; }
    event Action<OrielSizeChangedEventArgs>? IWindowBackend.Resized { add => Resized += value; remove => Resized -= value; }
    event Action<OrielPositionChangedEventArgs>? IWindowBackend.Moved { add => Moved += value; remove => Moved -= value; }
    event Action<bool>? IWindowBackend.FocusChanged { add => FocusChanged += value; remove => FocusChanged -= value; }

    event Action<double>? IWindowBackend.ScaleFactorChanged
    {
        add => ScaleFactorChanged += value;
        remove => ScaleFactorChanged -= value;
    }
    event Action<string>? IWindowBackend.NavigationStarting { add => NavigationStarting += value; remove => NavigationStarting -= value; }
    event Action<OrielNavigationCompletedEventArgs>? IWindowBackend.NavigationCompleted { add => NavigationCompleted += value; remove => NavigationCompleted -= value; }
    event Action<OrielConsoleMessageEventArgs>? IWindowBackend.ConsoleMessage { add => ConsoleMessage += value; remove => ConsoleMessage -= value; }
    event Action<OrielMessageReceivedEventArgs>? IWindowBackend.MessageReceived { add => MessageReceived += value; remove => MessageReceived -= value; }
    event Action<string>? IWindowBackend.ContextMenuItemClicked { add => ContextMenuItemClicked += value; remove => ContextMenuItemClicked -= value; }
    event Action<OrielFileDropEventArgs>? IWindowBackend.FileDropped { add => FileDropped += value; remove => FileDropped -= value; }
    // B10 第二批：本平台目前只上报 drop（见 OnFilesDropped）。enter/over/leave 要各自的坐标系换算
    // （macOS 左下原点 + backingScaleFactor、GTK 逻辑像素 + scaleFactor），与页面事件面同批上线——
    // 只在 Windows 收到 enter 的平台分裂比三平台都没有更难查。
    event Action<OrielDragEventArgs>? IWindowBackend.DragDrop { add => DragDrop += value; remove => DragDrop -= value; }

    private event Action<OrielDragEventArgs>? DragDrop;

    /// <summary>是否有键盘焦点（见 <see cref="IWindowBackend.IsFocused"/>）。</summary>
    /// <remarks>
    /// <c>isKeyWindow</c> 是"接收键盘事件的那个窗口"，语义与另两个平台一致。
    /// 它与 <c>isMainWindow</c> 不同：后者是"主窗口"，一个应用可以有多个主窗口，
    /// 但同时只有一个 key window。
    /// </remarks>
    public bool IsFocused => _nsWindow != 0 && ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isKeyWindow"));

    // ---- 光标（B21）----

    /// <summary>应用显式设置的光标形状（<c>null</c> = 没设过）。</summary>
    /// <remarks>
    /// 记它只为诊断与"设过之后不再假装能跟着 CSS 走"的文档一致性；
    /// macOS 没有"恢复默认"的入口，只能再设一个形状。
    /// </remarks>
    private OrielCursorIcon? _cursorIcon;

    /// <summary>光标可见性意图（与 <c>NSCursor hide/unhide</c> 的全局计数配对）。</summary>
    private bool _cursorVisible = true;

    /// <summary>是否已断开"鼠标 → 光标"的关联（<see cref="SetCursorGrab"/>）。</summary>
    private bool _cursorGrabbed;

    /// <summary>是否设置了忽略鼠标事件。</summary>
    private bool _ignoreCursorEvents;

    // ------------------------------------------------------------------
    // 平台开关 / 特效 / 任务栏（B22）
    // ------------------------------------------------------------------

    /// <summary>进简化全屏前的 <c>styleMask</c>（退出时还原）。</summary>
    private nuint _styleMaskBeforeSimpleFullscreen;

    /// <summary>进简化全屏前的窗口矩形（Cocoa 点），退出时还原。</summary>
    private (double X, double Y, double Width, double Height)? _frameBeforeSimpleFullscreen;

    /// <summary>当前是否处于简化全屏（见 <see cref="SetSimpleFullscreen"/>）。</summary>
    private bool _isSimpleFullscreen;

    /// <summary>B22 的材质视图（<c>NSVisualEffectView</c>）；0 = 没有。</summary>
    private nint _effectView;

    /// <summary>当前是否显示系统装饰（见 <see cref="IsDecorated"/>）。</summary>
    private bool _decorations;

    // ------------------------------------------------------------------
    // 上下文菜单
    // ------------------------------------------------------------------

    private MacOSMenu? _contextMenu;

    // ------------------------------------------------------------------
    // IWindowBackend
    // ------------------------------------------------------------------

    public void Show() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
    public void Hide() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("orderOut:"), 0);

    public void Close() => ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("performClose:"), 0);

    /// <summary>强制销毁窗口：绕过 <c>Closing</c> 与页面接管（对标 Tauri 2 的 <c>destroy</c>）。</summary>
    /// <remarks>
    /// <b>必须置 <c>_forceDestroy</c> 再 <c>close</c></b>：Cocoa 没有"直接销毁"的入口，
    /// <c>close</c> 一定会先问委托的 <c>windowShouldClose:</c>。那条路是我们自己接的，
    /// 所以让 <see cref="DecideClose"/> 在强制销毁时直接放行即可——
    /// 这与 Windows/Linux 用"不发 WM_CLOSE / 不走 delete-event"达到的效果相同。
    /// </remarks>
    public void Destroy()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        _forceDestroy = true;
        ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("close"));
    }

    /// <summary>是否让页面接管关闭；初值取自 <see cref="OrielWindowOptions.InterceptClose"/>。</summary>
    public bool CloseInterceptEnabled { get; set; }

    /// <summary>哪几类宿主事件转发给页面；默认全关（见 <see cref="OrielWindowEventKind"/>）。</summary>
    public OrielWindowEventKind EventForwarding { get; set; }

    public void Focus()
    {
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("makeKeyAndOrderFront:"), 0);
        ObjCRuntime.SendVoidBool(SharedApplication(), ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
    }

    public void Maximize() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("zoom:"));
    public void Minimize() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("miniaturize:"));

    public void Restore()
    {
        if (ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed")))
        {
            Maximize(); // zoom: 为切换语义，zoomed 状态下再次调用即还原
        }
        else
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("deminiaturize:"));
        }
    }

    private string? _currentUrl;

    /// <summary>当前文档 URL（NSURL 的 absoluteString）；销毁后或取不到时返回空串。</summary>
    private string CurrentUri()
    {
        if (_webview == 0)
        {
            return string.Empty;
        }

        nint url = ObjCRuntime.SendId(_webview, ObjCRuntime.Sel("URL"));
        if (url == 0)
        {
            return string.Empty;
        }

        return ObjCRuntime.ToManagedString(ObjCRuntime.SendId(url, ObjCRuntime.Sel("absoluteString"))) ?? string.Empty;
    }

    private static void AddButton(nint alert, string title)
        => _ = ObjCRuntime.SendIdObj(alert, ObjCRuntime.Sel("addButtonWithTitle:"), ObjCRuntime.MakeNSString(title));

    /// <summary>建面板并做三平台共通的配置（标题、过滤器、初始目录）。</summary>
    private static nint CreatePanel(bool isSave, string? title, IReadOnlyList<OrielFileFilter>? filters, string? initialDirectory)
    {
        var clsName = isSave ? "NSSavePanel" : "NSOpenPanel";
        nint panel = ObjCRuntime.SendId(ObjCRuntime.GetClass(clsName), ObjCRuntime.Sel(isSave ? "savePanel" : "openPanel"));
        if (panel == 0)
        {
            return 0;
        }

        if (title is not null)
        {
            ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setMessage:"), ObjCRuntime.MakeNSString(title));
        }

        // 空列表意味着"任意文件"，此时**不设置** allowedFileTypes（塞一个 "*" 进去会把面板
        // 锁成只能选无扩展名文件——见 OrielFileFilter.CocoaExtensions 的说明）
        IReadOnlyList<string> extensions = OrielFileFilter.CocoaExtensions(filters ?? []);
        if (extensions.Count > 0)
        {
            // allowedFileTypes 已在 macOS 12 起标废弃，但仍是最省事的路径：
            // 现代替代是 allowedContentTypes + UTType，需要额外框架与 AOT 友好的绑定，记在 ROADMAP
            ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setAllowedFileTypes:"), MakeStringArray(extensions));
        }

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            nint url = ObjCRuntime.SendIdObj(
                ObjCRuntime.GetClass("NSURL"),
                ObjCRuntime.Sel("fileURLWithPath:"),
                ObjCRuntime.MakeNSString(initialDirectory));
            if (url != 0)
            {
                ObjCRuntime.SendVoidObj(panel, ObjCRuntime.Sel("setDirectoryURL:"), url);
            }
        }

        return panel;
    }

    /// <summary>托管字符串数组 → NSArray（经 NSMutableArray 逐项 <c>addObject:</c>）。</summary>
    private static nint MakeStringArray(IReadOnlyList<string> items)
    {
        nint array = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSMutableArray"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));

        foreach (string item in items)
        {
            ObjCRuntime.SendVoidObj(array, ObjCRuntime.Sel("addObject:"), ObjCRuntime.MakeNSString(item));
        }

        return array;
    }

    /// <summary>
    /// 运行面板并收集**全部**选中路径，取消返回空数组。
    /// </summary>
    /// <remarks>
    /// 走 <c>URLs</c> 数组而不是 <c>URL</c>：<c>URL</c> 只反映"最后/唯一选中项"，
    /// 多选时拿不全。逐项 <c>objectAtIndex:</c> 与单选共用同一条路径，因此单选也走这里。
    /// </remarks>
    private static string[] RunPanel(nint panel)
    {
        if (panel == 0 || ObjCRuntime.SendId(panel, ObjCRuntime.Sel("runModal")) != 1) // NSModalResponseOK
        {
            return [];
        }

        nint urls = ObjCRuntime.SendId(panel, ObjCRuntime.Sel("URLs"));
        if (urls == 0)
        {
            return [];
        }

        int count = (int)ObjCRuntime.SendId(urls, ObjCRuntime.Sel("count"));
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            nint url = ObjCRuntime.SendIdNint(urls, ObjCRuntime.Sel("objectAtIndex:"), i);
            if (url == 0)
            {
                continue;
            }

            nint nsPath = ObjCRuntime.SendId(url, ObjCRuntime.Sel("path"));
            if (nsPath != 0 && ObjCRuntime.ToManagedString(nsPath) is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return [.. paths];
    }
}
