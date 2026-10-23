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

/// <summary>外观与窗口装饰：标题栏、边框、全屏、置顶、任务栏、主题、图标、进度。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    /// <inheritdoc />
    public void SetSkipTaskbar(bool skip)
    {
        if (!WindowAlive || skip == _skipTaskbar)
        {
            return;
        }

        // ITaskbarList 是**会话级**组件：没有 Explorer（Server Core、服务会话）时建不出来。
        // 那种情况下"隐藏任务栏按钮"没有意义也没有替代手段，如实报错而不是静默。
        bool ok = skip ? Win32TaskbarList.DeleteTab(_hwnd) : Win32TaskbarList.AddTab(_hwnd);
        if (!ok)
        {
            throw new InvalidOperationException(
                "window.setSkipTaskbar：任务栏列表不可用（当前会话没有 Explorer 的任务栏）。"
                + "这不是平台不支持，而是这个运行环境里没有任务栏可隐藏。");
        }

        _skipTaskbar = skip;
    }

    /// <inheritdoc />
    public void SetVisibleOnAllWorkspaces(bool visible)
        => throw new NotSupportedException(
            "Windows 不支持 setVisibleOnAllWorkspaces：Win32 没有公开的\"把窗口加入所有虚拟桌面\"接口"
            + "（虚拟桌面归 Explorer 管，没有对外的 COM/Win32 API）。Tauri 在 Windows 上同样不支持。");

    /// <inheritdoc />
    public void SetContentProtected(bool contentProtected)
    {
        if (!WindowAlive)
        {
            return;
        }

        if (!contentProtected)
        {
            _ = Win32.SetWindowDisplayAffinity(_hwnd, Win32Constants.WDA_NONE);
            return;
        }

        if (Win32.SetWindowDisplayAffinity(_hwnd, Win32Constants.WDA_EXCLUDEFROMCAPTURE))
        {
            return;
        }

        // Win10 2004 之前不认识 WDA_EXCLUDEFROMCAPTURE。退回 WDA_MONITOR：
        // 效果是"截屏里显示为黑块"，比"完全不保护"接近意图，但确实不是同一件事。
        if (Win32.SetWindowDisplayAffinity(_hwnd, Win32Constants.WDA_MONITOR))
        {
            OrielDiagnostics.Report(
                "[OrielWeb] setContentProtected：本系统不支持 WDA_EXCLUDEFROMCAPTURE，"
                + "已退回 WDA_MONITOR（截屏时窗口显示为黑块而不是消失）。");
            return;
        }

        throw new InvalidOperationException(
            "window.setContentProtected：SetWindowDisplayAffinity 失败（Win7 以下的系统没有这个能力）。");
    }

    /// <inheritdoc />
    public void SetSimpleFullscreen(bool fullscreen) => SetFullscreen(fullscreen);

    /// <inheritdoc />
    public void SetFullscreenOnMonitor(int x, int y)
    {
        if (!WindowAlive)
        {
            return;
        }

        // 点不在任何显示器上 → 什么都不做（与 Tauri 一致）
        nint monitor = Win32.MonitorFromPoint(
            new POINT(x, y), (uint)Win32Constants.MONITOR_DEFAULTTONULL);
        if (monitor == 0)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 先退出全屏再搬（全屏窗口的 SetWindowPos 会被系统忽略），搬完再进全屏
        bool wasFullscreen = _isFullscreen;
        if (wasFullscreen)
        {
            SetFullscreen(false);
        }

        _ = Win32.SetWindowPos(
            _hwnd, 0, info.rcMonitor.Left, info.rcMonitor.Top, 0, 0,
            Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);

        if (wasFullscreen)
        {
            SetFullscreen(true);
        }
    }

    /// <inheritdoc />
    public void SetTitleBarStyle(OrielTitleBarStyle style)
        => throw new NotSupportedException(
            "Windows 不支持 setTitleBarStyle：Win32 没有\"标题栏透明/覆盖\"这种样式位，"
            + "等价能力是创建时用 Frameless + 页面自绘标题栏（本库的 OrielWindowOptions.Frameless）。"
            + "Tauri 在 Windows 上同样不支持。");

    /// <inheritdoc />
    public void SetShadow(bool enable)
    {
        if (!WindowAlive)
        {
            return;
        }

        int policy = enable ? Win32Constants.DWMNCRP_ENABLED : Win32Constants.DWMNCRP_DISABLED;
        int hr = Win32.DwmSetWindowAttribute(
            _hwnd, Win32Constants.DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

        if (hr < 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] setShadow：DwmSetWindowAttribute(NCRENDERING_POLICY) 失败（HRESULT=0x{hr:X8}）。");
        }

        // 改完必须让 DWM 重算非客户区，否则"改了但看着没变"
        _ = Win32.SetWindowPos(
            _hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER
            | Win32Constants.SWP_NOACTIVATE | Win32Constants.SWP_FRAMECHANGED);
    }

    /// <inheritdoc />
    public void SetAlwaysOnBottom(bool alwaysOnBottom)
    {
        if (!WindowAlive || alwaysOnBottom == _alwaysOnBottom)
        {
            return;
        }

        if (alwaysOnBottom && _isOnTop)
        {
            // 置顶与置底互斥：后设的赢（Tauri 也是先取消置顶再压到底）
            SetOnTop(false);
        }

        _alwaysOnBottom = alwaysOnBottom;

        // 取消置底时用 HWND_TOP：把窗口放回普通 z 序的顶部（相当于"刚被激活"的位置）。
        // 没有"还原到原来的 z 序位置"的入口——那个位置本来就已经不存在了。
        _ = Win32.SetWindowPos(
            _hwnd,
            alwaysOnBottom ? Win32Constants.HWND_BOTTOM : 0,
            0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE
            | (alwaysOnBottom ? 0 : Win32Constants.SWP_NOZORDER));
    }

    /// <inheritdoc />
    public void SetFocusable(bool focusable)
    {
        if (!WindowAlive)
        {
            return;
        }

        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_EXSTYLE);
        nint updated = focusable
            ? style & ~(nint)Win32Constants.WS_EX_NOACTIVATE
            : style | Win32Constants.WS_EX_NOACTIVATE;

        _ = Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_EXSTYLE, updated);
    }

    /// <inheritdoc />
    public void SetDecorations(bool decorations)
    {
        if (!WindowAlive || decorations == _decorations)
        {
            return;
        }

        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
        if (decorations)
        {
            // 只补标题栏与可缩放边：最小化/最大化/系统菜单在创建期就已按选项带上了，
            // 这里不擅自把它们加回来（那是 SetMaximizable / SetMinimizable 的事）。
            style |= (nint)(Win32Constants.WS_CAPTION | Win32Constants.WS_THICKFRAME);
        }
        else
        {
            style &= ~(nint)(Win32Constants.WS_CAPTION | Win32Constants.WS_THICKFRAME);
        }

        _ = Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);
        _decorations = decorations;

        // 样式位改了必须 FRAMECHANGED，否则非客户区不会重算
        _ = Win32.SetWindowPos(
            _hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER
            | Win32Constants.SWP_NOACTIVATE | Win32Constants.SWP_FRAMECHANGED);
    }

    // ---- 任务栏 ----

    /// <inheritdoc />
    public void SetProgressBar(OrielProgressBarStatus status, double? progress)
    {
        if (!WindowAlive)
        {
            return;
        }

        int flag = status switch
        {
            OrielProgressBarStatus.None => Win32TaskbarList.TBPF_NOPROGRESS,
            OrielProgressBarStatus.Indeterminate => Win32TaskbarList.TBPF_INDETERMINATE,
            OrielProgressBarStatus.Paused => Win32TaskbarList.TBPF_PAUSED,
            OrielProgressBarStatus.Error => Win32TaskbarList.TBPF_ERROR,
            _ => Win32TaskbarList.TBPF_NORMAL,
        };

        if (!Win32TaskbarList.SetProgressState(_hwnd, flag))
        {
            throw new InvalidOperationException(
                "window.setProgressBar：任务栏列表不可用（本会话只有 ITaskbarList，没有 ITaskbarList3）。"
                + "没有 Explorer 的任务栏时进度条没有落点。");
        }

        // None 表示隐藏：状态位已经清了，值不必再写（写了也不会显示）
        if (status == OrielProgressBarStatus.None)
        {
            return;
        }

        if (progress is { } value)
        {
            double clamped = Math.Clamp(value, 0, 100);
            _ = Win32TaskbarList.SetProgressValue(_hwnd, (ulong)Math.Round(clamped), 100);
        }
    }

    /// <inheritdoc />
    public void SetOverlayIcon(OrielImage? icon)
    {
        if (!WindowAlive)
        {
            return;
        }

        nint handle = 0;
        if (icon is not null)
        {
            handle = Win32IconFactory.CreateFromRgba(icon);
            if (handle == 0)
            {
                throw new InvalidOperationException(
                    $"window.setOverlayIcon：图标创建失败（{icon.Width}x{icon.Height}）。");
            }
        }

        if (!Win32TaskbarList.SetOverlayIcon(_hwnd, handle, icon is null ? null : "覆盖图标"))
        {
            if (handle != 0)
            {
                _ = Win32.DestroyIcon(handle);
            }

            throw new InvalidOperationException(
                "window.setOverlayIcon：任务栏列表不可用（本会话只有 ITaskbarList，没有 ITaskbarList3）。"
                + "没有 Explorer 的任务栏时覆盖图标没有落点。");
        }

        // 换下来的旧图标由本对象销毁（任务栏已经拷走了它需要的那份）
        if (_overlayIcon != 0)
        {
            _ = Win32.DestroyIcon(_overlayIcon);
        }

        _overlayIcon = handle;
    }

    /// <inheritdoc />
    public void SetBadgeCount(int? count)
        => throw new NotSupportedException(
            "Windows 没有 Dock 徽标概念：用 setOverlayIcon 在任务栏图标上叠一个小图代替。");

    /// <inheritdoc />
    public void SetBadgeLabel(string? label)
        => throw new NotSupportedException(
            "Windows 没有 Dock 徽标概念：用 setOverlayIcon 在任务栏图标上叠一个小图代替。");

    /// <summary>是否最小化（见 <see cref="IWindowBackend.IsMinimized"/>）。</summary>
    public bool IsMinimized => Win32.IsIconic(_hwnd);

    /// <summary>是否全屏（取库记住的状态，见 <see cref="IWindowBackend.IsFullscreen"/>）。</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>是否置顶（取库记住的状态，见 <see cref="IWindowBackend.IsAlwaysOnTop"/>）。</summary>
    public bool IsAlwaysOnTop => _isOnTop;

    /// <summary>是否有系统装饰（取创建时的选项，见 <see cref="IWindowBackend.IsDecorated"/>）。</summary>
    public bool IsDecorated => _decorations;

    /// <summary>是否可缩放（原生读，见 <see cref="IWindowBackend.IsResizable"/>）。</summary>
    public bool IsResizable => (CurrentStyle() & Win32Constants.WS_THICKFRAME) != 0;

    /// <summary>是否可最大化（原生读，见 <see cref="IWindowBackend.IsMaximizable"/>）。</summary>
    public bool IsMaximizable => (CurrentStyle() & Win32Constants.WS_MAXIMIZEBOX) != 0;

    /// <summary>是否可最小化（原生读，见 <see cref="IWindowBackend.IsMinimizable"/>）。</summary>
    public bool IsMinimizable => (CurrentStyle() & Win32Constants.WS_MINIMIZEBOX) != 0;

    /// <summary>
    /// 是否可关闭（原生读，见 <see cref="IWindowBackend.IsClosable"/>）。
    /// </summary>
    /// <remarks>
    /// <b>"存在且可用"两件事都要满足</b>：窗口带 <c>WS_SYSMENU</c>（关闭按钮**存在**）
    /// 且系统菜单里的 <c>SC_CLOSE</c> 未被灰化（**可用**）。
    /// <para>
    /// 旧版只读 <c>WS_SYSMENU</c>——那答的是"有没有系统菜单"，而
    /// <see cref="SetClosable"/> 的落点是灰化菜单项，两者对不上会让
    /// "写进去读不回来"。补上后不变量成立：调过 <c>SetClosable(false)</c> 的窗口
    /// 这里必然读到 <c>false</c>（前提是它本来有系统菜单）。
    /// </para>
    /// <para>
    /// 窗口没有系统菜单时是 <c>false</c>；<c>GetMenuState</c> 对不存在的项返回
    /// <c>0xFFFFFFFF</c>，一并按"不可关"处理。
    /// </para>
    /// <para>
    /// <b>无边框窗口同样有系统菜单</b>（实测）：本库的 frameless 靠 <c>WM_NCCALCSIZE</c>
    /// 隐藏边框，<c>ComputeStyle</c> 里照样带 <c>WS_SYSMENU</c>（见
    /// <see cref="ComputeStyle"/>）——所以它的 <c>IsClosable</c> 是 <c>true</c>，
    /// 与"有没有可见标题栏"无关。要让它不可关得显式调 <see cref="SetClosable"/>。
    /// </para>
    /// </remarks>
    public bool IsClosable
    {
        get
        {
            if ((CurrentStyle() & Win32Constants.WS_SYSMENU) == 0)
            {
                return false;
            }

            nint menu = Win32.GetSystemMenu(_hwnd, false);
            if (menu == 0)
            {
                return false;
            }

            uint state = Win32.GetMenuState(menu, Win32Constants.SC_CLOSE, Win32Constants.MF_BYCOMMAND);
            if (state == uint.MaxValue)
            {
                return false;
            }

            return (state & (Win32Constants.MF_GRAYED | Win32Constants.MF_DISABLED)) == 0;
        }
    }

    /// <summary>是否接收输入（见 <see cref="IWindowBackend.IsEnabled"/>）。</summary>
    public bool IsEnabled => _hwnd != 0 && Win32.IsWindowEnabled(_hwnd);

    /// <summary>启用/禁用输入（见 <see cref="IWindowBackend.SetEnabled"/>）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_hwnd != 0)
        {
            // 返回值是"调用之前是否启用"，不是操作是否成功——显式丢弃免得被当成结果
            _ = Win32.EnableWindow(_hwnd, enabled);
        }
    }

    /// <summary>覆写这个窗口的主题（见 <see cref="IWindowBackend.SetTheme"/>）。</summary>
    /// <remarks>
    /// <para>
    /// **Windows 落点是 DWM 的深色标题栏属性 + UxTheme 的非客户区重绘**——
    /// 客户区（网页）的颜色由 CSS 决定，DWM 管的是标题栏 / 边框那圈。
    /// 所以"改主题"在这里改的是窗口的**装饰**，页面自己要跟着改配色
    /// （页面经 <c>window.theme()</c> / <c>theme.changed</c> 拿到当前值再自己决定）。
    /// </para>
    /// <para>
    /// <c>null</c> = 跟随系统：撤掉 DWM 覆写、恢复默认主题类。这两个都要做——
    /// 只撤 DWM 不撤主题类的话，标题栏可能停在"深色"上不动。
    /// </para>
    /// <para>
    /// 两个 DWM 属性名都试（<see cref="Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE"/> 与
    /// <see cref="Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1"/>）：
    /// Win10 2004 之前只认旧名，之后只认新名。非 0 的 HRESULT 是"这版本不认识这个属性"，
    /// 不是错误，跳过即可。
    /// </para>
    /// </remarks>
    public void SetTheme(OrielTheme? theme)
    {
        if (_hwnd == 0)
        {
            return;
        }

        int dark = theme == OrielTheme.Dark ? 1 : 0;

        // 先试 20（2004+），失败再试 19（1809/1903）——两个都不接受时保持原样。
        // DwmSetWindowAttribute 返回 HRESULT，非 0 只是"这个属性不认识"，不是调用错了。
        int size = sizeof(int);
        if (Win32.DwmSetWindowAttribute(
                _hwnd, Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, size) != 0)
        {
            _ = Win32.DwmSetWindowAttribute(
                _hwnd, Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, size);
        }

        if (theme is null)
        {
            // 跟随系统：撤掉主题类绑定，让标题栏按系统主题画
            _ = Win32.SetWindowTheme(_hwnd, null, null);
        }
        else
        {
            // 显式覆写：让标题栏按"视觉样式"画（而不是固定浅色）
            var data = new WTA_NONCLIENT_PARAMS
            {
                Flags = Win32Constants.WTNCA_USEVISUALSTYLE,
                Mask = Win32Constants.WTNCA_USEVISUALSTYLE,
            };
            _ = Win32.SetWindowThemeAttribute(
                _hwnd, Win32Constants.WTA_NONCLIENT, ref data, Marshal.SizeOf<WTA_NONCLIENT_PARAMS>());
        }

        // 让 DWM 重画非客户区：不发的话标题栏要等到下次被重绘才变
        _ = Win32.SetWindowPos(
            _hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
            Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 读当前窗口样式位。
    /// </summary>
    /// <remarks>
    /// 每次读一次而不是缓存：<c>SetResizable</c> 之类就是改这个样式位，
    /// 缓存会与"系统实际接受的状态"脱节——而"读回来反映实际"正是这一组查询的用途。
    /// </remarks>
    private nint CurrentStyle() => _hwnd == 0 ? 0 : Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);

    public bool ToggleMaximize()
    {
        if (Win32.IsZoomed(_hwnd))
        {
            Restore();
        }
        else
        {
            Maximize();
        }
        return Win32.IsZoomed(_hwnd);
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        Win32.SetWindowPos(
            _hwnd,
            enabled ? Win32Constants.HWND_TOPMOST : Win32Constants.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled == _isFullscreen)
        {
            return;
        }

        if (enabled)
        {
            var placement = default(WINDOWPLACEMENT);
            placement.length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();
            Win32.GetWindowPlacement(_hwnd, ref placement);
            _savedPlacement = placement;

            // 保存原始样式：退出全屏时必须原样还原。此前退出时无条件
            // style |= WS_OVERLAPPEDWINDOW，会把无边框（WS_POPUP）窗口全屏后变成带边框
            nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
            _savedStyle = style;
            style &= ~(nint)(Win32Constants.WS_CAPTION | Win32Constants.WS_THICKFRAME);
            // WS_POPUP = 0x80000000：在 AnyCPU 下编译期 nint 被视为 32 位，常量转换会报 CS8778。
            // 实际运行于 64 位进程，语义为置位高位，unchecked 是有意为之。
            style |= unchecked((nint)Win32Constants.WS_POPUP);
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);

            var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            Win32.GetMonitorInfo(monitor, ref info);
            Win32.SetWindowPos(_hwnd, 0,
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED | Win32Constants.SWP_SHOWWINDOW);
            _isFullscreen = true;
        }
        else
        {
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, _savedStyle);

            var placement = _savedPlacement;
            Win32.SetWindowPlacement(_hwnd, ref placement);
            Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
            _isFullscreen = false;
        }
    }

    /// <summary>当前窗口标题（见 <see cref="IWindowBackend.Title"/>）。</summary>
    public string Title => _title;

    public void SetTitle(string title)
    {
        // 显式设置之后，页面标题不再覆盖它（见 _titleSetExplicitly）
        _titleSetExplicitly = true;
        _title = title;
        Win32.SetWindowTextW(_hwnd, title);
    }

    public void SetResizable(bool enabled)
    {
        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
        const uint resizableMask = Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX;
        style = enabled
            ? style | (nint)resizableMask
            : style & ~(nint)resizableMask;
        Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
            Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
    }

    /// <summary>设置是否可最大化（改 <c>WS_MAXIMIZEBOX</c>，见 <see cref="IWindowBackend.SetMaximizable"/>）。</summary>
    public void SetMaximizable(bool enabled) => ApplyFrameStyleBit(Win32Constants.WS_MAXIMIZEBOX, enabled);

    /// <summary>设置是否可最小化（改 <c>WS_MINIMIZEBOX</c>，见 <see cref="IWindowBackend.SetMinimizable"/>）。</summary>
    public void SetMinimizable(bool enabled) => ApplyFrameStyleBit(Win32Constants.WS_MINIMIZEBOX, enabled);

    /// <summary>
    /// 设置是否可关闭（见 <see cref="IWindowBackend.SetClosable"/>）。
    /// </summary>
    /// <remarks>
    /// 落点是**灰化系统菜单里的"关闭"项**（<c>SC_CLOSE</c>），而不是清 <c>WS_SYSMENU</c>。
    /// 后者会把标题栏上三个按钮和整个系统菜单一起干掉，那是"删掉按钮"而不是"关闭按钮不可用"。
    /// <para>
    /// 窗口没有系统菜单时**什么都不做**——那种窗口本来就没有关闭按钮可灰。
    /// 注意**无边框窗口不属于这一类**：本库的 frameless 只是靠 <c>WM_NCCALCSIZE</c>
    /// 隐藏边框，<c>ComputeStyle</c> 里仍带 <c>WS_SYSMENU</c>，所以它有系统菜单、
    /// 这里会真的去灰化（实测确认）。
    /// </para>
    /// </remarks>
    public void SetClosable(bool enabled)
    {
        nint menu = Win32.GetSystemMenu(_hwnd, false);
        if (menu == 0)
        {
            return;
        }

        Win32.EnableMenuItem(
            menu,
            Win32Constants.SC_CLOSE,
            Win32Constants.MF_BYCOMMAND | (enabled ? Win32Constants.MF_ENABLED : Win32Constants.MF_GRAYED));

        // 系统菜单项的状态改了必须重画，否则菜单栏看着没变。
        Win32.DrawMenuBar(_hwnd);
    }

    /// <summary>设置窗口图标（见 <see cref="IWindowBackend.SetIcon"/>）。</summary>
    /// <remarks>
    /// 与创建期的 <see cref="ApplyExplicitIcon"/> 走完全同一条路——这正是把它抽成
    /// 静态方法的原因：运行期换图标与启动时设图标在 Win32 上是同一件事（都是
    /// <c>WM_SETICON</c>），没有理由写两份。
    /// </remarks>
    public void SetIcon(string path)
    {
        if (_hwnd == 0 || string.IsNullOrEmpty(path))
        {
            return;
        }

        ApplyExplicitIcon(_hwnd, path);
    }

    /// <summary>从内存里的图像字节设置窗口图标（见 <see cref="IWindowBackend.SetIconFromBytes"/>）。</summary>
    /// <remarks>
    /// 与 PNG 文件那条路（<see cref="ApplyPngIcon"/>）共用后半段：解码成 RGBA 再交给
    /// 托盘那套 <see cref="Win32IconFactory.CreateFromRgba"/>。差别只是数据来源。
    /// </remarks>
    public void SetIconFromBytes(byte[] bytes)
    {
        if (_hwnd == 0 || bytes is null || bytes.Length == 0)
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
            // 认不出的格式：保持现有图标，不抛——与 LoadImageW 返回 0 时的语义一致。
            // 但**记一笔**：这条路上"静默失败"是页面最难查的那种（它只知道命令成功了）。
            OrielDiagnostics.Report($"[OrielWeb] setIcon 的图像数据无法解码：{ex.Message}");
            return;
        }

        nint icon = Win32IconFactory.CreateFromRgba(image);
        if (icon != 0)
        {
            SetIconHandles(_hwnd, icon);
        }
    }

    /// <summary>从裸 RGBA 设置窗口图标（见 <see cref="IWindowBackend.SetIconFromRgba"/>）。</summary>
    /// <remarks>
    /// 与 <see cref="SetIconFromBytes"/> 共用后半段，只是跳过解码——
    /// <see cref="OrielImage"/> 的构造函数本来就是"收 RGBA + 尺寸"，不做格式解码。
    /// </remarks>
    public void SetIconFromRgba(byte[] rgba, int width, int height)
    {
        if (_hwnd == 0 || rgba is null || rgba.Length == 0)
        {
            return;
        }

        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
        {
            // 长度对不上就**不猜**：给工厂一个错位的缓冲，画出来是乱码图标，
            // 那比"没设上"更难查（看着像颜色错了）。
            OrielDiagnostics.Report(
                $"[OrielWeb] setIcon 的 RGBA 长度不符：{width}x{height} 需要 {width * height * 4} 字节，" +
                $"收到 {rgba.Length} 字节。");
            return;
        }

        nint icon = Win32IconFactory.CreateFromRgba(new OrielImage(rgba, width, height));
        if (icon != 0)
        {
            SetIconHandles(_hwnd, icon);
        }
    }

    /// <summary>
    /// 设置窗口底色（见 <see cref="IWindowBackend.SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 只是记下运行期的值再走一遍 <see cref="ApplyBackgroundColor"/>——创建期与运行期
    /// 在 WebView2 上是同一件事（都是 <c>DefaultBackgroundColor</c>），没有理由写两份。
    /// </remarks>
    public void SetBackgroundColor(uint argb)
    {
        _runtimeBackgroundArgb = argb;
        ApplyBackgroundColor();
    }

    /// <summary>
    /// 切一个"边框类"样式位并让窗口重算非客户区。
    /// </summary>
    /// <remarks>
    /// 抽出这个方法是因为 <c>setResizable</c> / <c>setMaximizable</c> / <c>setMinimizable</c>
    /// 改的是同一类位：只写 <c>GWL_STYLE</c> 不够，必须再发一次
    /// <c>SWP_FRAMECHANGED</c>，否则系统仍按旧样式画标题栏上的按钮——
    /// 表现为"属性已经改了，但按钮还在"。
    /// <para>
    /// 只清 <c>WS_MAXIMIZEBOX</c>/<c>WS_MINIMIZEBOX</c> 是**安全**的：Win32 会据此自动灰掉
    /// 系统菜单里的对应项，不需要我们再动菜单。这与"可关闭"不同——那边没有对应的样式位
    /// （见 <see cref="IsClosable"/> 的说明），所以本方法**不**用于 closable。
    /// </para>
    /// </remarks>
    /// <param name="bit">要切的样式位（单个位）。</param>
    /// <param name="enabled">置位还是清位。</param>
    private void ApplyFrameStyleBit(uint bit, bool enabled)
    {
        if (_hwnd == 0)
        {
            return;
        }

        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
        style = enabled ? style | (nint)bit : style & ~(nint)bit;
        Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
            Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
    }
}
