using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>创建流程：原生窗口/视图装配、初始样式与图标。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    internal void Create()
    {
        _gtkWindow = GtkNative.GtkWindowNew(0); // GTK_WINDOW_TOPLEVEL
        GtkNative.GtkWindowSetDefaultSize(_gtkWindow, _options.Width, _options.Height);
        GtkNative.GtkWindowSetTitle(_gtkWindow, _options.Title);
        if (_options.Frameless)
        {
            GtkNative.GtkWindowSetDecorated(_gtkWindow, false);
            // 去掉装饰后 WM 就不再提供 resize 边框，"拖边缘改大小"会整个失效（Ubuntu 22.04 实测）。
            // 这里记下要不要自己接管；**不可调整大小的窗口不接管**——接管了也只是白白吃掉边缘那几像素的点击。
            _handleResizeEdges = _options.Resizable;
        }

        if (_minWidth > 0 || _minHeight > 0)
        {
            ApplyMinSizeRequest();
        }

        // oriel:// 的注册必须是"任何 webview 创建之前"——这里就是那一刻（本文件唯一建 view 的地方）。
        // 放在导航时注册就晚了：首次导航已经在解析 URL，scheme 还没登记，引擎会当成未知协议。
        if (_assets is not null)
        {
            LinuxAssetScheme.Register(_assets, App.AssetHost);
        }

        _userContentManager = GtkNative.WebkitUserContentManagerNew();
        GtkNative.WebkitUserContentManagerRegisterScriptMessageHandler(_userContentManager, "oriel");
        _webview = GtkNative.WebkitWebViewNewWithUserContentManager(_userContentManager);

        // DevTools：与 Windows 的 AreDevToolsEnabled 语义一致——只"允许"检查，不自动打开面板
        // （打开 enable_developer_extras 后，WebKitGTK 的右键菜单会出现「检查元素」）。
        // settings 交给 webview 后由它持有引用，所以本地这一份引用即刻释放。
        var webkitSettings = GtkNative.WebkitSettingsNew();
        GtkNative.WebkitSettingsSetEnableDeveloperExtras(webkitSettings, _options.Debug);
        GtkNative.WebkitWebViewSetSettings(_webview, webkitSettings);
        GtkNative.GObjectUnref(webkitSettings);

        ApplyBackgroundColor();

        // 信号连接（先注册状态，再连接信号）
        LinuxSignalHandlers.RegisterWindow(_gtkWindow, this);
        LinuxSignalHandlers.RegisterWebview(_webview, this);
        LinuxSignalHandlers.RegisterManager(_userContentManager, _messageHandler);
        LinuxSignalHandlers.ConnectSignals(_gtkWindow, _webview, _userContentManager);

        // 拖放落点：注册在 webview 上（信号连接在上一条里，载荷解析在 trampoline 里）
        EnableFileDrop(_webview);

        // 边缘 resize：motion 事件默认不投递，要显式加进事件掩码，热区才收得到指针移动
        if (_handleResizeEdges)
        {
            GtkNative.GtkWidgetAddEvents(_webview, GtkNative.GdkPointerMotionMask);
        }

        // 桥接脚本（document 创建时注入）。宿主事实快照在这里取一次：
        // 它读的是显示器与 GtkSettings，都与窗口是否 realize 无关（见 ReadSystemSnapshot）。
        var userScript = GtkNative.WebkitUserScriptNew(
            LinuxBridgeJs.Build(
                _options.ConsoleForwarding,
                _options.Zoom,
                App.Guard.Token,
                App.Guard.TrustedPrefixes,
                ReadSystemSnapshot(), _options.DragRegionSelector, _options.Label),
            1, // WEBKIT_USER_CONTENT_INJECT_TOP_FRAME（主帧）
            0, // WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START（文档开始处注入，与 Windows/macOS 后端一致）
            0,
            0);
        GtkNative.WebkitUserContentManagerAddScript(_userContentManager, userScript);

        // 根布局：GtkBox{menubar?, webview}。**没有菜单栏时也走这个盒**——结构固定成一种，
        // 加/去菜单栏就只是往盒里插/摘一个部件，不必在两条布局路径之间来回搬家
        // （搬家要重挂 webview，会把页面状态一起弄丢）。
        //
        // gtk_box_new 给的是 floating 引用，gtk_container_add 会 sink——盒由窗口持有，
        // 窗口销毁时一起走，这里不需要单独 unref。
        _rootBox = GtkNative.GtkBoxNew(GtkNative.GtkOrientationVertical, spacing: 0);
        if (_rootBox != 0)
        {
            GtkNative.GtkBoxPackStart(_rootBox, _webview, expand: true, fill: true, padding: 0);
            GtkNative.GtkContainerAdd(_gtkWindow, _rootBox);
        }
        else
        {
            // 建盒失败是极罕见的分配失败；退化成旧布局（webview 直装窗口）。
            // 菜单栏会因此装不上（SetWindowMenuBar 判 _rootBox == 0 直接跳过），
            // 但窗口本身还能用——一个能用的窗口比一个空窗口好。
            OrielDiagnostics.Report("[OrielWeb] GtkBox 创建失败，退化为 webview 直装窗口（菜单栏将不可用）。");
            GtkNative.GtkContainerAdd(_gtkWindow, _webview);
        }

        // 窗口图标：X11 下写入 _NET_WM_ICON（可用 xprop 验证）；Wayland 下由合成器决定，通常忽略。
        if (!string.IsNullOrEmpty(_options.Icon))
        {
            _ = GtkNative.GtkWindowSetIconFromFile(_gtkWindow, _options.Icon, 0);
        }

        // 底色（WithBackgroundColor）在创建期就应用过了：见上面那处 ApplyBackgroundColor()。

        if (_options.X is not null || _options.Y is not null)
        {
            // At(x,y)（与 Windows/macOS 对齐）：map 之前发出的 move 请求会被窗口管理器
            // 当作初始位置。只给一维时另一维传 -1（GTK 的"保持现状"值）。
            GtkNative.GtkWindowMove(_gtkWindow, _options.X ?? -1, _options.Y ?? -1);
        }
        else if (_options.Center)
        {
            GtkNative.GtkWindowSetPosition(_gtkWindow, GtkWinPosCenter);
        }

        // 隐藏启动：先 show_all 把窗口与 webview 都 realize 出来（GTK 的 realize 是从顶层向下
        // 传播的，不 show 的话 webview 不会被 realize，页面可能推迟到窗口可见才开始加载），
        // 再立刻 hide 顶层窗口——与 Windows 的 SW_HIDE（窗口存在但不可见）、macOS 的不
        // orderFront 语义一致。代价是 X11 下理论上有 map→unmap 的一帧，换来的是"隐藏状态下
        // 页面照常加载、IPC 照常往返"。
        GtkNative.GtkWidgetShowAll(_gtkWindow);
        if (_options.Hidden)
        {
            GtkNative.GtkWidgetHide(_gtkWindow);
        }

        if (_options.OnTop)
        {
            SetOnTop(true);
        }
        if (_options.Maximized)
        {
            GtkNative.GtkWindowMaximize(_gtkWindow);
        }
        if (_options.Fullscreen)
        {
            GtkNative.GtkWindowFullscreen(_gtkWindow);
        }

        Navigate();
    }
}
