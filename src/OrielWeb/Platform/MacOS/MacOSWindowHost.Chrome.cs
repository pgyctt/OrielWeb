using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>外观与窗口装饰：标题栏、边框、全屏、置顶、任务栏、主题、图标、进度。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    /// <summary>
    /// 窗口当前是否"显示中"（<see cref="Hide"/> 之后为 false；最小化仍算显示中）。
    /// </summary>
    /// <remarks>
    /// <c>orderOut:</c> 藏的是整个 NSWindow，WKWebView 是它的 contentView 子视图、随之一并消失，
    /// 不存在 Windows 组合宿主那种"渲染面是独立顶层窗口"的残余（见 <c>Win32WindowHost.Hide</c> 的不变量）。
    /// </remarks>
    public bool IsVisible => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isVisible"));

    /// <summary>是否最小化（见 <see cref="IWindowBackend.IsMinimized"/>）。</summary>
    /// <remarks>
    /// Cocoa 的 <c>isMiniaturized</c> 就是"收进 Dock 了"。
    /// 注意它与 <c>isVisible</c> **不互斥**：最小化的窗口仍然 visible。
    /// </remarks>
    public bool IsMinimized => _nsWindow != 0 && ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isMiniaturized"));

    /// <summary>是否全屏（取库记住的状态，见 <see cref="IWindowBackend.IsFullscreen"/>）。</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>是否置顶（取库记住的状态，见 <see cref="IWindowBackend.IsAlwaysOnTop"/>）。</summary>
    public bool IsAlwaysOnTop => _isOnTop;

    /// <summary>当前是否显示系统装饰；<see cref="SetDecorations"/> 会改它。</summary>
    /// <remarks>
    /// 记库里的状态而不是读 <c>styleMask</c>：本库的无边框窗口**仍然带 <c>Titled</c>**
    /// （无边框靠"透明标题栏 + 隐藏按钮 + fullSizeContentView"实现），
    /// 读样式位会把无边框窗口读成"有装饰"。
    /// </remarks>
    public bool IsDecorated => _decorations;

    /// <summary>是否可缩放（原生读，见 <see cref="IWindowBackend.IsResizable"/>）。</summary>
    public bool IsResizable => (CurrentStyleMask() & StyleResizable) != 0;

    /// <summary>
    /// 是否可最大化（见 <see cref="IWindowBackend.IsMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// 用 resizable 位：AppKit 的"缩放"（zoom / 绿灯）以
    /// <c>NSWindowStyleMaskResizable</c> 为前提，没有独立的最大化位。
    /// </remarks>
    public bool IsMaximizable => (CurrentStyleMask() & StyleResizable) != 0;

    /// <summary>是否可最小化（原生读，见 <see cref="IWindowBackend.IsMinimizable"/>）。</summary>
    public bool IsMinimizable => (CurrentStyleMask() & StyleMiniaturizable) != 0;

    /// <summary>是否可关闭（原生读，见 <see cref="IWindowBackend.IsClosable"/>）。</summary>
    public bool IsClosable => (CurrentStyleMask() & StyleClosable) != 0;

    /// <summary>
    /// 是否接收输入（见 <see cref="IWindowBackend.IsEnabled"/>）。
    /// </summary>
    /// <remarks>
    /// **只挡鼠标、不挡键盘**：Cocoa 没有"禁用窗口"这个概念，这里用
    /// <c>ignoresMouseEvents</c> 近似。如实写出来，而不是假装与另两个平台一致。
    /// </remarks>
    public bool IsEnabled => _nsWindow != 0
        && !ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("ignoresMouseEvents"));

    /// <summary>启用/禁用输入（见 <see cref="IWindowBackend.SetEnabled"/>）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_nsWindow != 0)
        {
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), !enabled);
        }
    }

    /// <summary>覆写这个窗口的主题（见 <see cref="IWindowBackend.SetTheme"/>）。</summary>
    /// <remarks>
    /// <para>
    /// macOS 落点是 <c>NSWindow.appearance</c>（**按窗口**）：设了它的窗口按这个
    /// appearance 画，没设的跟应用走（<c>NSApp.appearance</c>）。所以 macOS 上
    /// "这个窗口"与"整个应用"是两层，语义比 Linux（只有一层）清楚。
    /// </para>
    /// <para>
    /// <c>null</c> = 跟随系统：把 <c>appearance</c> 还原成 <c>nil</c>，
    /// 那表示"让 NSWindow 自己按系统/应用决定"。
    /// </para>
    /// </remarks>
    public void SetTheme(OrielTheme? theme)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        if (theme is null)
        {
            // 还原：让 NSWindow 按系统/应用主题画
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setAppearance:"), 0);
            return;
        }

        nint appearanceName = ObjCRuntime.MakeNSString(
            theme == OrielTheme.Dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
        nint appearance = ObjCRuntime.SendIdObj(
            ObjCRuntime.GetClassOrThrow("NSAppearance"),
            ObjCRuntime.Sel("appearanceNamed:"),
            appearanceName);
        if (appearance != 0)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setAppearance:"), appearance);
        }
    }

    /// <inheritdoc />
    public void SetSkipTaskbar(bool skip)
        => throw new NotSupportedException(
            "macOS 不支持 setSkipTaskbar：Dock 里显示的是整个应用，没有\"按窗口隐藏\"这回事"
            + "（要从 Dock 消失得改 NSApplication 的 activationPolicy，那是应用级决定）。"
            + "Tauri 在 macOS 上同样不支持。");

    /// <inheritdoc />
    /// <remarks>
    /// 只切 <c>NSWindowCollectionBehaviorCanJoinAllSpaces</c> 这一位，**保留**窗口已有的其它
    /// collectionBehavior（全屏窗口的 <c>FullScreenPrimary</c> 就在同一字段里）：整段覆盖会把
    /// 它们一起清掉，"刚设过全工作区"的全屏窗口于是行为变了。
    /// </remarks>
    public void SetVisibleOnAllWorkspaces(bool visible)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        const nuint CanJoinAllSpaces = 1 << 0;
        nuint behavior = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("collectionBehavior"));
        behavior = visible ? behavior | CanJoinAllSpaces : behavior & ~CanJoinAllSpaces;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setCollectionBehavior:"), (nint)behavior);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>NSWindowSharingNone = 0</c>（不可被截取）、<c>NSWindowSharingReadOnly = 1</c>（默认）。
    /// </remarks>
    public void SetContentProtected(bool contentProtected)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setSharingType:"), contentProtected ? 0 : 1);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 与原生全屏的区别：**不创建新的 Space**，靠"去掉标题栏 + 窗口铺满所在屏幕"实现
    /// （Lion 之前的全屏形态）。所以用户不会被带到另一张桌面、别的窗口也不会被推走。
    /// </para>
    /// <para>
    /// 进出都保存/还原 <c>styleMask</c> 与窗口矩形——退出后窗口回到原来的位置与大小。
    /// 与原生全屏互斥：已经处于原生全屏时先退出它，否则两个模式打架、窗口卡在半屏。
    /// </para>
    /// </remarks>
    public void SetSimpleFullscreen(bool fullscreen)
    {
        if (_nsWindow == 0 || fullscreen == _isSimpleFullscreen)
        {
            return;
        }

        if (fullscreen)
        {
            if (IsFullscreen)
            {
                SetFullscreen(false);
            }

            _styleMaskBeforeSimpleFullscreen =
                (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
            _frameBeforeSimpleFullscreen = ReadFrame();

            nint screen = ScreenOfWindow();
            (double sx, double sy, double sw, double sh) = ScreenFrame(screen);

            // 去掉标题栏：borderless 才能铺满到菜单栏下方（原生保留圆角与阴影）
            ObjCRuntime.SendVoidNint(
                _nsWindow,
                ObjCRuntime.Sel("setStyleMask:"),
                (nint)(_styleMaskBeforeSimpleFullscreen & ~StyleTitled));

            ObjCRuntime.SendVoidRectBool(
                _nsWindow, ObjCRuntime.Sel("setFrame:display:"), new ObjCRuntime.NSRect(sx, sy, sw, sh), true);

            _isSimpleFullscreen = true;
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)_styleMaskBeforeSimpleFullscreen);

        if (_frameBeforeSimpleFullscreen is { } f)
        {
            ObjCRuntime.SendVoidRectBool(
                _nsWindow, ObjCRuntime.Sel("setFrame:display:"), new ObjCRuntime.NSRect(f.X, f.Y, f.Width, f.Height), true);
        }

        _frameBeforeSimpleFullscreen = null;
        _isSimpleFullscreen = false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 参数是**屏幕物理像素**（与 <c>Monitor.position</c> 同口径）。Cocoa 的屏幕坐标是
    /// "点 + 左下原点"，所以先按主屏缩放比与主屏高度把它翻过来再比对——不翻的话副屏上
    /// 会挑错屏幕（上下颠倒的那一块）。点不在任何屏幕上时什么都不做（与 Tauri 一致）。
    /// </remarks>
    public void SetFullscreenOnMonitor(int x, int y)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        nint target = ScreenForPhysicalPoint(x, y);
        if (target == 0)
        {
            return;
        }

        (double sx, double sy, double sw, double sh) = ScreenFrame(target);

        bool wasFullscreen = IsFullscreen;
        bool wasSimple = _isSimpleFullscreen;
        if (wasFullscreen)
        {
            SetFullscreen(false);
        }

        if (wasSimple)
        {
            SetSimpleFullscreen(false);
        }

        // 全屏切换本身会把窗口铺满所在屏幕，这里只需要"把它搬到那台屏幕上"
        ObjCRuntime.SendVoidRectBool(
            _nsWindow, ObjCRuntime.Sel("setFrame:display:"), new ObjCRuntime.NSRect(sx, sy, sw, sh), true);

        if (wasSimple)
        {
            SetSimpleFullscreen(true);
        }
        else if (wasFullscreen)
        {
            SetFullscreen(true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 三档的落点：
    /// <list type="bullet">
    ///   <item><c>visible</c>：<c>titlebarAppearsTransparent = NO</c>、标题可见、
    ///     去掉 <c>fullSizeContentView</c>（内容从标题栏下方开始）。</item>
    ///   <item><c>transparent</c>：标题栏透明，其余同上。</item>
    ///   <item><c>overlay</c>：透明 + 标题隐藏 + <c>fullSizeContentView</c>——
    ///     页面延伸到标题栏下方，只剩红绿灯浮在上面（与 Tauri 的说明一致）。</item>
    /// </list>
    /// </remarks>
    public void SetTitleBarStyle(OrielTitleBarStyle style)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        bool transparent = style != OrielTitleBarStyle.Visible;
        bool overlay = style == OrielTitleBarStyle.Overlay;

        ObjCRuntime.SendVoidBool(
            _nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), transparent);
        ObjCRuntime.SendVoidNint(
            _nsWindow,
            ObjCRuntime.Sel("setTitleVisibility:"),
            overlay ? NSWindowTitleHidden : 0);

        // 改 styleMask 会让 AppKit 重画标题栏；先记再改，避免把别的位一起抹掉
        nuint mask = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
        mask = overlay ? mask | StyleFullSizeContentView : mask & ~StyleFullSizeContentView;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)mask);
    }

    /// <inheritdoc />
    public void SetShadow(bool enable)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setHasShadow:"), enable);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 落点是 <c>window.level</c>：普通窗口是 0，这里降到 <b>-1</b>——低于所有普通窗口、
    /// 但远高于桌面（<c>kCGDesktopWindowLevel</c> 在 -20 亿那一档，降到那里窗口会跑到壁纸
    /// 后面、而且通常再也拿不到焦点）。所以本库的"置底"是"比普通窗口都低、仍可交互"。
    /// </remarks>
    public void SetAlwaysOnBottom(bool alwaysOnBottom)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setLevel:"), alwaysOnBottom ? -1 : 0);
    }

    /// <inheritdoc />
    public void SetFocusable(bool focusable)
        => throw new NotSupportedException(
            "macOS 不支持 setFocusable：AppKit 没有公开的\"这个窗口不能成为 key window\"入口"
            + "（只有私有的 _setPreventsActivation，用它等于把自己绑到未公开 API 上）。"
            + "需要\"点了不抢焦点\"的窗口得用 nonactivating 的 NSPanel，本库不提供那种窗口类型。");

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 两件事一起做：切 <c>NSWindowStyleMaskTitled</c>，并把标题栏的透明/可见性设回去。
    /// 只切样式位不够——AppKit 里"无装饰"是**borderless**（没有标题栏、没有红绿灯），
    /// 而 Titled 位还在但标题栏透明的窗口看起来仍然是"有装饰"的（那是本库的 Frameless 形态）。
    /// </para>
    /// <para>
    /// 它**不会**动 <c>fullSizeContentView</c> 或标题可见性——那两件是
    /// <see cref="SetTitleBarStyle"/> 的事，两个方法各管一段。
    /// </para>
    /// </remarks>
    public void SetDecorations(bool decorations)
    {
        if (_nsWindow == 0 || decorations == _decorations)
        {
            return;
        }

        nuint mask = (nuint)ObjCRuntime.SendNint(_nsWindow, ObjCRuntime.Sel("styleMask"));
        mask = decorations ? mask | StyleTitled : mask & ~StyleTitled;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), (nint)mask);

        ObjCRuntime.SendVoidBool(
            _nsWindow, ObjCRuntime.Sel("setTitlebarAppearsTransparent:"), !decorations);
        ObjCRuntime.SendVoidNint(
            _nsWindow, ObjCRuntime.Sel("setTitleVisibility:"), decorations ? 0 : NSWindowTitleHidden);

        _decorations = decorations;
    }

    /// <inheritdoc />
    public void SetProgressBar(OrielProgressBarStatus status, double? progress)
        => throw new NotSupportedException(
            "macOS 不支持 setProgressBar：NSDockTile 没有进度条 API（要塞进度得把整个磁贴内容"
            + "接管成自绘视图）。Tauri 在 macOS 上同样不实现。");

    /// <inheritdoc />
    public void SetOverlayIcon(OrielImage? icon)
        => throw new NotSupportedException(
            "macOS 没有任务栏覆盖图标：用 setBadgeCount / setBadgeLabel 在 Dock 图标上显示数字或短文本。");

    /// <inheritdoc />
    /// <remarks>
    /// <c>NSApp.dockTile.badgeLabel</c> 收字符串：<c>null</c> 或 0 清空，正数写十进制文本。
    /// **应用级**——所有窗口调它都是改同一个 Dock 图标（AppKit 的事实）。
    /// </remarks>
    public void SetBadgeCount(int? count)
    {
        if (count is null or <= 0)
        {
            SetBadgeLabel(null);
            return;
        }

        SetBadgeLabel(count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public void SetBadgeLabel(string? label)
    {
        nint app = SharedApplication();
        if (app == 0)
        {
            return;
        }

        nint dockTile = ObjCRuntime.SendId(app, ObjCRuntime.Sel("dockTile"));
        if (dockTile == 0)
        {
            return;
        }

        nint text = label is null ? 0 : ObjCRuntime.MakeNSString(label);
        ObjCRuntime.SendVoidObj(dockTile, ObjCRuntime.Sel("setBadgeLabel:"), text);
    }

    /// <summary>请求用户注意（见 <see cref="IWindowBackend.RequestUserAttention"/>）。</summary>
    /// <remarks>
    /// <para>
    /// <c>requestUserAttention:</c> 的取值是 <c>NSCriticalRequest = 0</c> /
    /// <c>NSInformationalRequest = 10</c>（**不是** 0/1，容易记错），
    /// 返回值是一个请求 id，取消时要交给 <c>cancelUserAttentionRequest:</c>。
    /// </para>
    /// <para>
    /// <b>两条消息都必须发给 <c>NSApplication</c> 的实例，不是 <c>NSWindow</c>，也不是类对象。</b>
    /// 它们都是 <c>NSApplication</c> 的**实例方法**：发给窗口会因 unrecognized selector 抛
    /// <c>NSException</c>（进程直接 SIGABRT），发给类对象同理。这也是"应用级注意力"而非
    /// "窗口级"的语义来源——Dock 图标弹跳本来就是整个应用的行为。
    /// </para>
    /// </remarks>
    public void RequestUserAttention(OrielUserAttention? type)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        if (type is null)
        {
            if (_attentionRequestId != 0)
            {
                ObjCRuntime.SendVoidNint(
                    SharedApplication(),
                    ObjCRuntime.Sel("cancelUserAttentionRequest:"),
                    _attentionRequestId);
                _attentionRequestId = 0;
            }

            return;
        }

        const int NsCriticalRequest = 0;
        const int NsInformationalRequest = 10;

        int request = type == OrielUserAttention.Critical ? NsCriticalRequest : NsInformationalRequest;

        _attentionRequestId = ObjCRuntime.SendNintNint(
            SharedApplication(), ObjCRuntime.Sel("requestUserAttention:"), request);
    }

    /// <summary>
    /// 读当前的 <c>styleMask</c>。
    /// </summary>
    /// <remarks>
    /// 每次读一次而不是缓存：<c>SetResizable</c> 就是改这个位，
    /// 缓存会与"AppKit 实际接受的状态"脱节。
    /// </remarks>
    private nuint CurrentStyleMask()
        => _nsWindow == 0 ? 0 : (nuint)ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));

    /// <summary>
    /// 设置窗口底色（见 <see cref="IWindowBackend.SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 只记下运行期的值再走一遍 <see cref="ApplyBackgroundColor"/>——创建期与运行期
    /// 在 AppKit 上是同一组 setter，没有理由写两份。
    /// </remarks>
    public void SetBackgroundColor(uint argb)
    {
        _runtimeBackgroundArgb = argb;
        ApplyBackgroundColor();
    }

    private void HideStandardButton(nint buttonKind)
    {
        var button = ObjCRuntime.SendIdNint(_nsWindow, ObjCRuntime.Sel("standardWindowButton:"), buttonKind);
        if (button != 0)
        {
            ObjCRuntime.SendVoidBool(button, ObjCRuntime.Sel("setHidden:"), true);
        }
    }

    public bool ToggleMaximize()
    {
        Maximize(); // NSWindow zoom: 本身就是切换语义
        // zoom: 是同步生效的，这里读到的就是切换后的真实值（与 Linux 的 GTK 异步语义不同）。
        // 统一走 SyncMaximizedState：既更新托管事件，也把事件推给页面。
        return SyncMaximizedState(force: true);
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled != _isFullscreen)
        {
            ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("toggleFullScreen:"));
            _isFullscreen = enabled;
        }
    }

    /// <summary>
    /// 原生全屏路径回灌（绿钮、ESC、系统菜单的进出全屏不经过 <see cref="SetFullscreen"/>）。
    /// </summary>
    /// <remarks>
    /// 不回灌的话 <c>_isFullscreen</c> 会与真实状态相反，下一次 Toggle/SetFullscreen 行为颠倒——
    /// Linux 由 window-state-event 回灌（SyncFullscreenState）、Windows 全屏只经 API 发起
    /// 无此问题，macOS 是最后一个缺口（评审 P2-9）。
    /// </remarks>
    internal void OnNativeFullscreenChanged(bool isFullscreen)
    {
        if (_isFullscreen != isFullscreen)
        {
            _isFullscreen = isFullscreen;
        }
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setLevel:"), enabled ? NSFloatingWindowLevel : 0);
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
        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString(title));
    }

    public void SetResizable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled ? (nint)((nuint)style | StyleResizable) : (nint)((nuint)style & ~StyleResizable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }

    /// <summary>
    /// 设置是否可最大化（见 <see cref="IWindowBackend.SetMaximizable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>在 macOS 上这就是改 <c>NSWindowStyleMaskResizable</c></b>，与
    /// <see cref="SetResizable"/> 是同一个操作——AppKit 根本没有独立的最大化位，
    /// 缩放按钮可用与否完全由 resizable 位决定（<see cref="IsMaximizable"/>
    /// 读的也是这一位，所以"写进去能读回来"成立）。
    /// <para>
    /// 副作用要说清楚：调 <c>setMaximizable(false)</c> 会**同时**让窗口不可拖拽缩放。
    /// 这是 AppKit 的模型，不是本库的取舍——Tauri 在 macOS 上也是同一落点。
    /// 迁移代码若同时调过 <c>setResizable</c> 和 <c>setMaximizable</c>，
    /// 在 macOS 上后调的那个赢。
    /// </para>
    /// </remarks>
    public void SetMaximizable(bool enabled) => SetResizable(enabled);

    /// <summary>设置是否可最小化（切 <c>NSWindowStyleMaskMiniaturizable</c>，见 <see cref="IWindowBackend.SetMinimizable"/>）。</summary>
    public void SetMinimizable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled
            ? (nint)((nuint)style | StyleMiniaturizable)
            : (nint)((nuint)style & ~StyleMiniaturizable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }

    /// <summary>
    /// 设置窗口图标（见 <see cref="IWindowBackend.SetIcon"/>）。
    /// </summary>
    /// <remarks>
    /// <b>macOS 上没有窗口级图标</b>，能设的只有应用（Dock）图标——所以这个调用会影响
    /// 整个应用而不只是本窗口。这是平台事实（AppKit 的模型），本库照实实现并写明，
    /// 而不是假装它只作用于本窗口。
    /// <para>
    /// 与创建期图标共用 <see cref="ApplyIconToApplication"/>：AppKit 那边也是同一个 API。
    /// </para>
    /// </remarks>
    public void SetIcon(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ApplyIconToApplication(path);
    }

    /// <summary>从内存里的图像字节设置窗口图标（见 <see cref="IWindowBackend.SetIconFromBytes"/>）。</summary>
    /// <remarks>
    /// 与托盘图标共用 <see cref="MacOSImageFactory.CreateNSImage"/>（RGBA → CGImage → NSImage）。
    /// 落点仍是**应用（Dock）图标**——macOS 没有窗口级图标（见 <see cref="SetIcon"/>）。
    /// </remarks>
    public void SetIconFromBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
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

        nint nsImage = MacOSImageFactory.CreateNSImage(image);
        if (nsImage != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), nsImage);
        }
    }

    /// <summary>从裸 RGBA 设置窗口图标（见 <see cref="IWindowBackend.SetIconFromRgba"/>）。</summary>
    /// <remarks>
    /// 与 <see cref="SetIconFromBytes"/> 共用后半段，只是跳过解码。
    /// 落点仍是**应用（Dock）图标**——macOS 没有窗口级图标。
    /// </remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
    {
        if (rgba is null || rgba.Length == 0)
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

        nint nsImage = MacOSImageFactory.CreateNSImage(new OrielImage(rgba, width, height));
        if (nsImage != 0)
        {
            ObjCRuntime.SendVoidObj(SharedApplication(), ObjCRuntime.Sel("setApplicationIconImage:"), nsImage);
        }
    }

    /// <summary>
    /// 设置是否可关闭（切 <c>NSWindowStyleMaskClosable</c>，见 <see cref="IWindowBackend.SetClosable"/>）。
    /// </summary>
    /// <remarks>
    /// 与 Windows 的落点（灰化系统菜单项）不同，这里就是<b>直接切样式位</b>——
    /// 红色关闭按钮会随位消失/出现。之所以两边落点不同，是因为 AppKit 确实有这个位
    /// 而 Win32 没有（Win32 上"按钮"属于系统菜单，见 <see cref="IsClosable"/> 的说明）。
    /// <para>
    /// 不变量"写进去能读回来"成立：<see cref="IsClosable"/> 读的就是这一位。
    /// </para>
    /// </remarks>
    public void SetClosable(bool enabled)
    {
        var style = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("styleMask"));
        var newStyle = enabled
            ? (nint)((nuint)style | StyleClosable)
            : (nint)((nuint)style & ~StyleClosable);
        ObjCRuntime.SendVoidNint(_nsWindow, ObjCRuntime.Sel("setStyleMask:"), newStyle);
    }
}
