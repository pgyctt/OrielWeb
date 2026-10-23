using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>外观与窗口装饰：标题栏、边框、全屏、置顶、任务栏、主题、图标、进度。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    /// <summary>
    /// 窗口当前是否"显示中"（<see cref="Hide"/> 之后为 false；最小化仍算显示中）。
    /// </summary>
    /// <remarks>
    /// 问顶层部件自己的可见位：webview 是它的子部件，<c>gtk_widget_hide</c> 藏的是整个窗口，
    /// 不存在 Windows 组合宿主那种"渲染面是独立顶层窗口"的残余（见 <c>Win32WindowHost.Hide</c> 的不变量）。
    /// </remarks>
    public bool IsVisible => WindowAlive && GtkNative.GtkWidgetGetVisible(_gtkWindow);

    /// <summary>是否最小化（见 <see cref="IWindowBackend.IsMinimized"/>）。</summary>
    /// <remarks>
    /// 走 GDK 的状态位而不是 <c>gtk_window_is_active</c> 之类的间接判据：
    /// <c>GDK_WINDOW_STATE_ICONIFIED</c>（位 1）就是"最小化"这个事实本身。
    /// </remarks>
    public bool IsMinimized
    {
        get
        {
            const int GdkWindowStateIconified = 1 << 1;

            if (!WindowAlive)
            {
                return false;
            }

            nint gdkWindow = GtkNative.GtkWidgetGetWindow(_gtkWindow);
            return gdkWindow != 0 && (GtkNative.GdkWindowGetState(gdkWindow) & GdkWindowStateIconified) != 0;
        }
    }

    /// <summary>是否全屏（取库记住的状态，见 <see cref="IWindowBackend.IsFullscreen"/>）。</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>是否置顶（取库记住的状态，见 <see cref="IWindowBackend.IsAlwaysOnTop"/>）。</summary>
    public bool IsAlwaysOnTop => _isOnTop;

    /// <summary>是否有系统装饰（运行期可被 <see cref="SetDecorations"/> 改；见 <see cref="IWindowBackend.IsDecorated"/>）。</summary>
    public bool IsDecorated => _decorations;

    /// <summary>是否可缩放（原生读，见 <see cref="IWindowBackend.IsResizable"/>）。</summary>
    public bool IsResizable => WindowAlive && GtkNative.GtkWindowGetResizable(_gtkWindow);

    /// <summary>
    /// 是否可最大化（见 <see cref="IWindowBackend.IsMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>回落到 <see cref="IsResizable"/></b>：GTK 没有"可最大化"这个独立概念——
    /// 最大化跟着 resizable 走。<c>gtk_window_set_resizable(false)</c> 之后窗口确实最大化不了。
    /// </remarks>
    public bool IsMaximizable => IsResizable;

    /// <summary>
    /// 是否可最小化（见 <see cref="IWindowBackend.IsMinimizable"/>）。
    /// </summary>
    /// <remarks>
    /// 同样回落到 <see cref="IsResizable"/>，理由同 <see cref="IsMaximizable"/>。
    /// </remarks>
    public bool IsMinimizable => IsResizable;

    /// <summary>
    /// 是否可关闭（见 <see cref="IWindowBackend.IsClosable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>恒为 true</b>：GTK 没有"可关闭"这个概念——窗口总能收到 delete-event，
    /// 而本库的关闭接管是在那之上做的**拦截**（页面可以不同意关）。
    /// 所以"能不能关"在这里不是一个可以被关掉的属性。给恒真比给一个猜测值诚实。
    /// </remarks>
    public bool IsClosable => true;

    /// <summary>是否接收输入（见 <see cref="IWindowBackend.IsEnabled"/>）。</summary>
    public bool IsEnabled => WindowAlive && GtkNative.GtkWidgetGetSensitive(_gtkWindow);

    /// <summary>启用/禁用输入（见 <see cref="IWindowBackend.SetEnabled"/>）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (WindowAlive)
        {
            GtkNative.GtkWidgetSetSensitive(_gtkWindow, enabled ? 1 : 0);
        }
    }

    /// <summary>覆写这个窗口的主题（见 <see cref="IWindowBackend.SetTheme"/>）。</summary>
    /// <remarks>
    /// <para>
    /// <b>GTK 没有"按窗口选主题"的入口</b>：能设的只有应用级的
    /// <c>gtk-application-prefer-dark-theme</c>（改了之后**所有** GTK 窗口都跟着变）。
    /// 所以这里落点是 <see cref="LinuxPlatformBackend.SetApplicationTheme"/>——
    /// 作用域是应用，不是"这个窗口"。这不是本库的缺口，是 GTK 的形状。
    /// </para>
    /// <para>
    /// 与 <see cref="OrielApp.SetTheme"/> 那侧的分工：那边负责"记哪个窗口有自己覆写、
    /// 哪些跟着应用走"，这里只负责把结论写进 GTK。两边各管一半，免得作用域逻辑写两遍。
    /// </para>
    /// </remarks>
    public void SetTheme(OrielTheme? theme)
    {
        if (!WindowAlive)
        {
            return;
        }

        // GTK 只有应用级主题入口（见方法的 remarks）：落点是平台后端的共用设置
        _backend.SetApplicationTheme(theme);
    }

    /// <summary>请求用户注意（见 <see cref="IWindowBackend.RequestUserAttention"/>）。</summary>
    /// <remarks>
    /// <b>两档在这里没有区别</b>：GTK 只有 <c>gtk_window_set_urgency_hint</c> 这个开/关，
    /// 没有紧急程度的分级。传 null 表示取消（置为 false）。
    /// </remarks>
    public void RequestUserAttention(OrielUserAttention? type)
    {
        if (WindowAlive)
        {
            GtkNative.GtkWindowSetUrgencyHint(_gtkWindow, type is not null);
        }
    }

    /// <inheritdoc />
    public void SetSkipTaskbar(bool skip)
    {
        if (!WindowAlive)
        {
            return;
        }

        GtkNative.GtkWindowSetSkipTaskbarHint(_gtkWindow, skip);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>gtk_window_stick</c> 在 X11 上写 <c>_NET_WM_STATE_STICKY</c>；Wayland 没有
    /// "工作区"这个协议概念，合成器会直接忽略——不是本库能补的。
    /// </remarks>
    public void SetVisibleOnAllWorkspaces(bool visible)
    {
        if (!WindowAlive)
        {
            return;
        }

        if (visible)
        {
            GtkNative.GtkWindowStick(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowUnstick(_gtkWindow);
        }
    }

    /// <inheritdoc />
    public void SetContentProtected(bool contentProtected)
        => throw new NotSupportedException(
            "Linux 不支持 setContentProtected：X11 没有\"阻止窗口被截取\"的能力，"
            + "Wayland 的相关协议（wlr-screencopy 的排除列表）GTK3 不暴露。");

    /// <inheritdoc />
    public void SetSimpleFullscreen(bool fullscreen) => SetFullscreen(fullscreen);

    /// <inheritdoc />
    /// <remarks>
    /// 入参是**屏幕物理像素**（与 <c>Monitor.position</c> 同口径）；GTK 的
    /// <c>gtk_window_move</c> 收应用像素，所以先按该显示器的缩放比换算。
    /// 显示器不认识这个点时什么都不做（与 Tauri 一致）。
    /// </remarks>
    public void SetFullscreenOnMonitor(int x, int y)
    {
        if (!WindowAlive)
        {
            return;
        }

        OrielMonitor? monitor = _backend.Monitors.MonitorFromPoint(x, y);
        if (monitor is null)
        {
            return;
        }

        double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        int left = (int)Math.Round(monitor.Position.X / scale);
        int top = (int)Math.Round(monitor.Position.Y / scale);

        // 全屏窗口的 gtk_window_move 会被 WM 忽略，先退出全屏再搬
        bool wasFullscreen = _isFullscreen;
        if (wasFullscreen)
        {
            SetFullscreen(false);
        }

        GtkNative.GtkWindowMove(_gtkWindow, left, top);

        if (wasFullscreen)
        {
            SetFullscreen(true);
        }
    }

    /// <inheritdoc />
    public void SetTitleBarStyle(OrielTitleBarStyle style)
        => throw new NotSupportedException(
            "Linux 不支持 setTitleBarStyle：GTK 只有\"有装饰/无装饰\"两态（gtk_window_set_decorated），"
            + "没有\"标题栏透明/覆盖\"。等价能力是创建时用 Frameless + 页面自绘标题栏。");

    /// <inheritdoc />
    public void SetShadow(bool enable)
        => throw new NotSupportedException(
            "Linux 不支持 setShadow：GTK 没有阴影 API，窗口阴影由合成器按窗口类型给"
            + "（_GTK_FRAME_EXTENTS 那套），应用改不了。");

    /// <inheritdoc />
    public void SetAlwaysOnBottom(bool alwaysOnBottom)
    {
        if (!WindowAlive)
        {
            return;
        }

        // _NET_WM_STATE_BELOW：与置顶（_NET_WM_STATE_ABOVE）互斥，后设的赢
        if (alwaysOnBottom && _isOnTop)
        {
            SetOnTop(false);
        }

        GtkNative.GtkWindowSetKeepBelow(_gtkWindow, alwaysOnBottom);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 两层都设：<c>gtk_window_set_accept_focus</c> 是给 WM 的提示（"别把焦点给我"），
    /// <c>gtk_widget_set_can_focus</c> 决定 GTK 内部能不能把焦点交给它。只设一层的话，
    /// 多窗口应用里焦点仍可能在 GTK 内部被搬过来。
    /// </remarks>
    public void SetFocusable(bool focusable)
    {
        if (!WindowAlive)
        {
            return;
        }

        GtkNative.GtkWindowSetAcceptFocus(_gtkWindow, focusable);
        GtkNative.GtkWidgetSetCanFocus(_gtkWindow, focusable);
    }

    /// <inheritdoc />
    public void SetDecorations(bool decorations)
    {
        if (!WindowAlive || decorations == _decorations)
        {
            return;
        }

        _decorations = decorations;
        GtkNative.GtkWindowSetDecorated(_gtkWindow, decorations);
    }

    /// <inheritdoc />
    public void SetProgressBar(OrielProgressBarStatus status, double? progress)
        => throw new NotSupportedException(
            "Linux 不支持 setProgressBar：需要 libunity 的 LauncherEntry（D-Bus 协议），"
            + "本库不链接那个库——只对部分桌面环境有效的能力不值得引一个运行时依赖。");

    /// <inheritdoc />
    public void SetOverlayIcon(OrielImage? icon)
        => throw new NotSupportedException(
            "Linux 没有任务栏覆盖图标的概念（任务栏由各桌面环境自己实现，没有统一扩展点）。");

    /// <inheritdoc />
    public void SetBadgeCount(int? count)
        => throw new NotSupportedException(
            "Linux 不支持 setBadgeCount：需要 libunity 的 LauncherEntry（D-Bus 协议），本库不链接那个库。");

    /// <inheritdoc />
    public void SetBadgeLabel(string? label)
        => throw new NotSupportedException(
            "Linux 不支持 setBadgeLabel：需要 libunity 的 LauncherEntry（D-Bus 协议），本库不链接那个库。");

    public bool ToggleMaximize()
    {
        if (!WindowAlive) { return false; }
        bool willBeMaximized = !GtkNative.GtkWindowIsMaximized(_gtkWindow);
        if (willBeMaximized)
        {
            GtkNative.GtkWindowMaximize(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowUnmaximize(_gtkWindow);
        }

        // 返回的是"意图值"，不是读回的真实状态：maximize/unmaximize 只是向窗口管理器发请求，
        // 此处立刻读 gtk_window_is_maximized 拿到的仍是切换前的旧值（这正是"图标反了"的根因，
        // 页面用返回值驱动图标，于是显示成相反状态）。真实状态由 window-state-event 确认后经
        // SyncMaximizedState 上报；万一 WM 拒绝请求，那次上报会把页面纠正回真实值。
        return willBeMaximized;
    }

    public void SetFullscreen(bool enabled)
    {
        if (!WindowAlive || enabled == _isFullscreen)
        {
            return;
        }
        if (enabled)
        {
            GtkNative.GtkWindowFullscreen(_gtkWindow);
        }
        else
        {
            GtkNative.GtkWindowUnfullscreen(_gtkWindow);
        }
        _isFullscreen = enabled;
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public void SetOnTop(bool enabled)
    {
        if (!WindowAlive) { return; }
        _isOnTop = enabled;
        GtkNative.GtkWindowSetKeepAbove(_gtkWindow, enabled);
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    /// <summary>当前窗口标题（见 <see cref="IWindowBackend.Title"/>）。</summary>
    public string Title => _title;

    public void SetTitle(string title)
    {
        // 显式设置之后，页面标题不再覆盖它（见 _titleSetExplicitly）
        _titleSetExplicitly = true;
        _title = title;
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowSetTitle(_gtkWindow, title);
    }

    public void SetResizable(bool enabled)
    {
        if (!WindowAlive) { return; }
        GtkNative.GtkWindowSetResizable(_gtkWindow, enabled);
    }

    /// <summary>
    /// 设置是否可最大化——<b>空操作</b>（见 <see cref="IWindowBackend.SetMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// GTK3 没有控制最大化按钮的 API。窗口管理器允许哪些动作是由它自己依据
    /// <c>_NET_WM_ALLOWED_ACTIONS</c> 算出来的，客户端改不了（Tauri/tao 在 Linux 上
    /// 同样是空操作）。这里**刻意不记状态**：一旦记了，<see cref="IsMaximizable"/>
    /// 就会给出一个"用户看到的按钮其实还在"的假值。要真的禁止最大化，
    /// 现有手段是 <see cref="SetResizable"/>(false)——GTK 上两者本来就是同一件事。
    /// </remarks>
    public void SetMaximizable(bool enabled)
    {
        _ = enabled;
    }

    /// <summary>
    /// 设置是否可最小化——<b>空操作</b>（见 <see cref="IWindowBackend.SetMinimizable"/>）。
    /// </summary>
    /// <remarks>理由同 <see cref="SetMaximizable"/>：GTK 没有对应入口，且不记状态以免读取撒谎。</remarks>
    public void SetMinimizable(bool enabled)
    {
        _ = enabled;
    }

    /// <summary>
    /// 设置窗口图标（见 <see cref="IWindowBackend.SetIcon"/>）。
    /// </summary>
    /// <remarks>
    /// 复用创建期那条路（<c>gtk_window_set_icon_from_file</c>）——它在运行期同样有效，
    /// GTK 会重写 <c>_NET_WM_ICON</c>。
    /// <para>
    /// <b>Wayland 下通常无效</b>：那边窗口图标由合成器决定（多数合成器直接用 .desktop
    /// 文件的图标），应用改不了。这与创建期图标是同一个限制，不是这里新引入的。
    /// </para>
    /// </remarks>
    public void SetIcon(string path)
    {
        if (!WindowAlive || string.IsNullOrEmpty(path))
        {
            return;
        }

        _ = GtkNative.GtkWindowSetIconFromFile(_gtkWindow, path, 0);
    }

    /// <summary>从内存里的图像字节设置窗口图标（见 <see cref="IWindowBackend.SetIconFromBytes"/>）。</summary>
    /// <remarks>
    /// 与托盘图标共用 <see cref="GtkPixbufFactory"/>（RGBA → GdkPixbuf）。
    /// 注意 <c>gtk_window_set_icon</c> 会 ref 一次，所以这里仍要释放**自己**那份 pixbuf。
    /// </remarks>
    public void SetIconFromBytes(byte[] bytes)
    {
        if (!WindowAlive || bytes is null || bytes.Length == 0)
        {
            return;
        }

        OrielImage image;
        try
        {
            image = OrielImage.FromBytes(bytes);
        }
        catch (OrielImageFormatException ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] setIcon 的图像数据无法解码：{ex.Message}");
            return;
        }

        nint pixbuf = GtkPixbufFactory.CreatePixbuf(image);
        if (pixbuf == 0)
        {
            return;
        }

        try
        {
            GtkNative.GtkWindowSetIcon(_gtkWindow, pixbuf);
        }
        finally
        {
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>从裸 RGBA 设置窗口图标（见 <see cref="IWindowBackend.SetIconFromRgba"/>）。</summary>
    /// <remarks>与 <see cref="SetIconFromBytes"/> 共用后半段，只是跳过解码。</remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
    {
        if (!WindowAlive || rgba is null || rgba.Length == 0)
        {
            return;
        }

        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] setIcon 的 RGBA 长度不符：{width}x{height} 需要 {width * height * 4} 字节，" +
                $"收到 {rgba.Length} 字节。");
            return;
        }

        nint pixbuf = GtkPixbufFactory.CreatePixbuf(new OrielImage(rgba, width, height));
        if (pixbuf == 0)
        {
            return;
        }

        try
        {
            GtkNative.GtkWindowSetIcon(_gtkWindow, pixbuf);
        }
        finally
        {
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>
    /// 设置窗口底色（见 <see cref="IWindowBackend.SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 只记下运行期的值再走一遍 <see cref="ApplyBackgroundColor"/>——创建期与运行期
    /// 在 WebKitGTK 上是同一件事，没有理由写两份。
    /// </remarks>
    public void SetBackgroundColor(uint argb)
    {
        _runtimeBackgroundArgb = argb;
        ApplyBackgroundColor();
    }

    /// <summary>
    /// 设置是否可关闭——<b>空操作</b>（见 <see cref="IWindowBackend.SetClosable"/>）。
    /// </summary>
    /// <remarks>
    /// GTK 没有"可关闭"这个属性：窗口总能收到 delete-event，本库的关闭接管
    /// （页面可以不同意关）是在那之上做的**拦截**，而"拦截"和"禁掉关闭按钮"不是一回事
    /// （Tauri/tao 在 Linux 上同样没实现这一属性）。
    /// <see cref="IsClosable"/> 在这里恒为 <c>true</c>，不会因为调过本方法而变。
    /// <para>
    /// 真要"关不掉"，现有手段是拦 <c>close-requested</c> 事件——那是应用层策略，
    /// 而且能做到"按条件拒绝"，比一个静态属性更有用。
    /// </para>
    /// </remarks>
    public void SetClosable(bool enabled)
    {
        _ = enabled;
    }
    /// <inheritdoc />
    public void SetEffects(OrielEffects effects)
        => throw new NotSupportedException(
            "Linux 不支持 setEffects：窗口材质是 Windows DWM 与 macOS AppKit 的概念，"
            + "GTK3 没有对应 API（各桌面环境自己的模糊协议不在 GTK3 里）。"
            + "Tauri 在 Linux 上同样不实现。");

    /// <inheritdoc />
    public void ClearEffects()
        => throw new NotSupportedException(
            "Linux 不支持 clearEffects：这个能力整个不存在（见 setEffects），"
            + "而不是\"清的时候才不存在\"。");
}
