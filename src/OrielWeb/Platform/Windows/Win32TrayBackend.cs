using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 托盘后端：<c>Shell_NotifyIconW</c> + 弹出菜单。
/// </summary>
/// <remarks>
/// <para>
/// 回调窗口是**本类型专属**的不可见窗口（<see cref="Win32MessageWindow"/>）：
/// 托盘图标不随任何应用窗口存活，而平台的调度窗口与 WebView2 共享消息空间——
/// 自选的 "WM_APP + n" 会与 WebView2 的私有消息撞车（现象见 <see cref="Win32MessageWindow"/> 的说明），
/// 所以这里另开一个窗口，不与任何第三方组件共用消息号。
/// 菜单用 <c>TPM_RETURNCMD</c> 直接取回选中项 id（<see cref="Win32Menu.Popup"/>），不需要命令路由。
/// </para>
/// <para>
/// <b>多托盘</b>（对标 Tauri 2）：每个后端实例占一个进程内唯一的 <c>NOTIFYICONDATA.uID</c>，
/// 回调消息仍走同一个 <c>WM_APP_TRAY</c>，靠 <c>lParam</c> 低字携带的鼠标消息 + <c>uID</c> 分派。
/// 因为刻意不调 <c>NIM_SETVERSION</c>（理由见 <see cref="Add"/>），回调形态恒为旧式：
/// <c>wParam</c> = 图标 ID、<c>lParam</c> 低字 = 鼠标消息——正好能分辨是哪一个托盘。
/// </para>
/// </remarks>
internal sealed unsafe class Win32TrayBackend : ITrayBackend
{
    /// <summary>
    /// 进程内唯一的图标 ID 分配器。
    /// </summary>
    /// <remarks>
    /// 从 1 开始（0 在 <c>NOTIFYICONDATA.uID</c> 里是合法的，但保留给"未设置"这类哨兵用途）。
    /// 每个后端实例一个专属 ID，于是同一进程里多个托盘的回调能分辨归属。
    /// </remarks>
    private static int s_nextTrayId;

    /// <summary>
    /// 图标 ID → 后端实例。托盘回调只拿到 <c>uID</c>，靠这张表找回实例。
    /// </summary>
    private static readonly Dictionary<uint, Win32TrayBackend> s_instances = [];

    private readonly OrielApp _app;
    private readonly uint _trayId;

    /// <summary>
    /// 专属消息窗口：托盘回调必须有一个**不与 WebView2 共享**的消息空间
    /// （理由见 <see cref="Win32MessageWindow"/> 的说明）。
    /// </summary>
    private readonly Win32MessageWindow _window;

    private nint _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _hidden;
    private string _tooltip = "OrielWeb";
    private Win32Menu? _menu;
    private bool _disposed;

    public string Id { get; }

    /// <inheritdoc/>
    public bool MenuOnClick { get; set; }

    public event Action? Clicked;
    public event Action<OrielTrayEvent>? EventReceived;
    public event Action<string>? MenuItemClicked;
    public event Action<string>? RawEvent;

    internal Win32TrayBackend(OrielApp app, OrielTrayOptions options, string id)
    {
        _app = app;
        Id = id;
        MenuOnClick = options.MenuOnClick;
        _tooltip = options.Tooltip;

        _trayId = (uint)Interlocked.Increment(ref s_nextTrayId);
        s_instances[_trayId] = this;

        _icon = ResolveIcon(options, out _ownsIcon);

        _window = new Win32MessageWindow();
        _window.MessageReceived += OnWindowMessage;

        Add();
    }

    /// <summary>
    /// 把选项里的图标解成 HICON。
    /// </summary>
    /// <remarks>
    /// 统一走 <see cref="OrielTrayOptions.ResolveIcon"/>：解码在托管侧完成（PNG/ICO 都吃），
    /// 因此**Windows 不再受 <c>LoadImageW</c> 只认 ICO/BMP 的限制**——PNG 图标现在也能用。
    /// 没有可用图标时回落到 exe 图标。
    /// </remarks>
    private static nint ResolveIcon(OrielTrayOptions options, out bool owns)
    {
        if (options.ResolveIcon() is { } image)
        {
            nint fromImage = Win32IconFactory.CreateFromRgba(image);
            if (fromImage != 0)
            {
                owns = true;
                return fromImage;
            }

            OrielDiagnostics.Report("[OrielWeb] 托盘图标：由 RGBA 造 HICON 失败，回落到应用图标。");
        }

        return LoadExecutableIcon(out owns);
    }

    /// <summary>专属窗口收到的消息：托盘回调（左键与右键）都走这里。</summary>
    private void OnWindowMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == Win32Constants.WM_APP_TRAY)
        {
            HandleCallback(wParam, lParam);
        }
    }

    // ---- 原生回调入口（由专属窗口的 WndProc 转发）----

    /// <summary>
    /// 托盘回调。**V4 形态**：<c>LOWORD(lParam)</c> 是事件类型、<c>HIWORD(lParam)</c> 是图标 ID、
    /// <c>GET_X/Y_LPARAM(wParam)</c> 是锚点坐标（MSDN：Shell_NotifyIcon 的 Remarks）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么现在用 V4</b>：它是拿到 Enter/Move/Leave 的唯一途径（<c>NIN_POPUPOPEN</c>/
    /// <c>NIN_POPUPCLOSE</c> 只在 V4 下送达），而这三者正是对标 Tauri 2 的事件面所必需的。
    /// 早年不切 V4 的三个理由现在都不成立：
    /// ① 旧式"简单可靠"不再优先——事件面完整性更重要；
    /// ② <c>uTimeoutOrVersion</c> 联合体的坑——现在只写 uVersion、不再碰气球超时（见 <see cref="Add"/>）；
    /// ③ 多托盘靠 <c>HIWORD(lParam)</c> 的图标 ID 分辨，V4 同样支持（MSDN 明确：图标 ID 限制 16 位）。
    /// </para>
    /// <para>
    /// 事件送达的完整清单（MSDN）：鼠标在图标矩形内移动/悬停、键盘选中或激活、气球上的动作，
    /// 一律经 <c>uCallbackMessage</c> 送达，事件类型在 <c>LOWORD(lParam)</c>。
    /// </para>
    /// </remarks>
    internal void HandleCallback(nuint wParam, nint lParam)
    {
        uint eventType = (uint)(lParam & 0xFFFF);
        uint iconId = (uint)((ulong)lParam >> 16) & 0xFFFF;

        // V4 下坐标在 wParam 的两半里（GET_X/Y_LPARAM 的等价物）
        double x = (short)(wParam & 0xFFFF);
        double y = (short)((wParam >> 16) & 0xFFFF);
        var position = new OrielTrayPoint(x, y);

        RawEvent?.Invoke(
            $"V4 回调：事件 0x{eventType:X}（图标 ID {iconId}）坐标 ({x},{y})");

        OrielTrayMouseButton? button = eventType switch
        {
            Win32Constants.WM_LBUTTONDOWN or Win32Constants.WM_LBUTTONUP or Win32Constants.WM_LBUTTONDBLCLK
                or Win32Constants.NIN_SELECT or Win32Constants.NIN_KEYSELECT => OrielTrayMouseButton.Left,
            Win32Constants.WM_RBUTTONDOWN or Win32Constants.WM_RBUTTONUP or Win32Constants.WM_RBUTTONDBLCLK
                or Win32Constants.WM_CONTEXTMENU => OrielTrayMouseButton.Right,
            Win32Constants.WM_MBUTTONDOWN or Win32Constants.WM_MBUTTONUP or Win32Constants.WM_MBUTTONDBLCLK
                => OrielTrayMouseButton.Middle,
            _ => null,
        };

        bool isDown = eventType is Win32Constants.WM_LBUTTONDOWN
            or Win32Constants.WM_RBUTTONDOWN or Win32Constants.WM_MBUTTONDOWN;
        bool isDouble = eventType is Win32Constants.WM_LBUTTONDBLCLK
            or Win32Constants.WM_RBUTTONDBLCLK or Win32Constants.WM_MBUTTONDBLCLK;

        switch (eventType)
        {
            // Enter / Leave：V4 才有的悬停事件（对标 Tauri 2 的 Enter/Leave）
            case Win32Constants.NIN_POPUPOPEN:
                EventReceived?.Invoke(new OrielTrayEvent(
                    Id, OrielTrayEventType.Enter, position: position));
                return;

            case Win32Constants.NIN_POPUPCLOSE:
                EventReceived?.Invoke(new OrielTrayEvent(
                    Id, OrielTrayEventType.Leave, position: position));
                return;

            // Move：指针在图标矩形内移动（MSDN 明确 lParam 会是 WM_MOUSEMOVE）
            case Win32Constants.WM_MOUSEMOVE:
                EventReceived?.Invoke(new OrielTrayEvent(
                    Id, OrielTrayEventType.Move, position: position));
                return;
        }

        if (button is { } b)
        {
            OrielTrayEventType type = isDouble ? OrielTrayEventType.DoubleClick : OrielTrayEventType.Click;
            EventReceived?.Invoke(new OrielTrayEvent(
                Id, type, b, isDown ? OrielTrayButtonState.Down : OrielTrayButtonState.Up, position));
        }

        // 抬起（含 NIN_SELECT/NIN_KEYSELECT 这两个"激活"事件）才算一次点击
        bool isClick = eventType is Win32Constants.WM_LBUTTONUP
            or Win32Constants.WM_RBUTTONUP or Win32Constants.WM_MBUTTONUP
            or Win32Constants.NIN_SELECT or Win32Constants.NIN_KEYSELECT;

        if (isClick && button == OrielTrayMouseButton.Left)
        {
            Clicked?.Invoke();
            if (MenuOnClick)
            {
                ShowMenu();
            }

            return;
        }

        if (eventType is Win32Constants.WM_CONTEXTMENU or Win32Constants.WM_RBUTTONUP)
        {
            ShowMenu();
        }
    }

    /// <summary>弹出托盘菜单（右键事件与左键 MenuOnClick 两个入口都调它）。</summary>
    internal void ShowMenu()
    {
        if (_disposed)
        {
            return;
        }

        // owner 用专属窗口：Popup 会先把它置前台（托盘菜单的硬性要求）
        OrielMenuItem? item = _menu?.Popup(_window.Handle);
        if (item is not null)
        {
            Activate(item);
        }
    }

    // ---- ITrayBackend ----

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (!_added)
        {
            return;
        }

        var data = CreateData(Win32Constants.NIF_TIP);
        WriteTip(ref data);
        _ = Win32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 设置图标旁的文字。
    /// </summary>
    /// <remarks>
    /// <b>Windows 不支持</b>（对标 Tauri 2 的 <c>title</c> 同样标注 Windows: Unsupported）——
    /// 通知区没有"图标旁文字"这个概念，这里是显式空操作，不报错。
    /// </remarks>
    public void SetTitle(string? title)
    {
        _ = title;
    }

    /// <summary>
    /// 模板图标记。
    /// </summary>
    /// <remarks>
    /// <b>仅 macOS 有意义</b>：Windows 的托盘图标由我们提供的 HICON 决定外观，
    /// 系统不做反色重绘。显式空操作。
    /// </remarks>
    public void SetIconAsTemplate(bool asTemplate)
    {
        _ = asTemplate;
    }

    public void SetIcon(OrielImage? icon)
    {
        nint newIcon;
        bool owns;

        if (icon is null)
        {
            // null = 回到平台默认（exe 图标）
            newIcon = LoadExecutableIcon(out owns);
        }
        else
        {
            newIcon = Win32IconFactory.CreateFromRgba(icon);
            owns = newIcon != 0;
        }

        if (newIcon == 0)
        {
            // 新图标加载失败：保留原图标而不是把托盘变成空白（用户看不出发生了什么）
            OrielDiagnostics.Report("[OrielWeb] 托盘图标加载失败，保留原图标。");
            return;
        }

        nint previous = _icon;
        bool previousOwned = _ownsIcon;
        _icon = newIcon;
        _ownsIcon = owns;

        if (_added)
        {
            var data = CreateData(Win32Constants.NIF_ICON);
            data.hIcon = _icon;
            _ = Win32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref data);
        }

        if (previousOwned && previous != 0)
        {
            _ = Win32.DestroyIcon(previous);
        }
    }

    public void SetMenu(IReadOnlyList<OrielMenuItem> items)
    {
        _menu?.Dispose();
        _menu = Win32Menu.Build(items);
    }

    public void Show() => SetHidden(hidden: false);

    public void Hide() => SetHidden(hidden: true);

    /// <summary><c>Shell_NotifyIcon(NIM_ADD)</c> 是否成功（失败时托盘静默缺失，见 <see cref="Add"/>）。</summary>
    public bool IsVisible => _added && !_hidden;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = CreateData(0);
            _ = Win32.Shell_NotifyIconW(Win32Constants.NIM_DELETE, ref data);
            _added = false;
        }

        s_instances.Remove(_trayId);

        _menu?.Dispose();
        _menu = null;

        if (_ownsIcon && _icon != 0)
        {
            _ = Win32.DestroyIcon(_icon);
            _icon = 0;
        }

        // 窗口最后释放：NIM_DELETE 已经做完，句柄不再需要
        _window.Dispose();
    }

    // ---- 菜单项激活 ----

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            // 托盘没有"当前窗口"：窗口级/编辑类 role 在这里没有明确目标，
            // OrielMenuRoles 会返回 false 表示"没人处理"，我们据此静默忽略（而不是猜一个窗口去操作）。
            if (!OrielMenuRoles.TryActivate(role, _app, window: null))
            {
                OrielDiagnostics.Report($"[OrielWeb] 托盘菜单的 role「{role}」在当前上下文无法执行，已忽略。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
        }
    }

    // ---- 原生细节 ----

    private void Add()
    {
        var data = CreateData(
            Win32Constants.NIF_MESSAGE | Win32Constants.NIF_ICON | Win32Constants.NIF_TIP | Win32Constants.NIF_SHOWTIP);
        data.uCallbackMessage = Win32Constants.WM_APP_TRAY;
        data.hIcon = _icon;
        WriteTip(ref data);

        // 加不上（例如 explorer 未运行）不是致命错误：记一笔，托盘静默缺失，
        // 应用其余部分照常工作——为"托盘图标没出现"让进程起不来是过度反应。
        if (!Win32.Shell_NotifyIconW(Win32Constants.NIM_ADD, ref data))
        {
            OrielDiagnostics.Report("[OrielWeb] Shell_NotifyIcon(NIM_ADD) 失败：托盘不可用。");
            return;
        }

        _added = true;

        // 切到 **V4 回调**（对标 Tauri 2 的事件面所必需）：
        // MSDN 明确 NIM_SETVERSION 必须在**每次 NIM_ADD 之后**调用，且不随注销持久化。
        // V4 带来 Enter/Leave（NIN_POPUPOPEN/NIN_POPUPCLOSE）与事件坐标；
        // 图标 ID 移到 HIWORD(lParam)，多托盘照样分得清（MSDN：图标 ID 限制 16 位）。
        //
        // 联合体那个坑（uTimeoutOrVersion）在这里是安全的：本库的通知**早已不走托盘气球**
        // （改走 WinRT toast，见 Win32ToastNotification），因此没有任何调用点会去写 uTimeout，
        // 这个字段的语义恒定是"版本号"。
        var versionData = CreateData(0);
        versionData.uVersionOrTimeout = Win32Constants.NOTIFYICON_VERSION_4;
        if (!Win32.Shell_NotifyIconW(Win32Constants.NIM_SETVERSION, ref versionData))
        {
            // 失败不致命：回调退化成旧式形态（无 Enter/Leave/坐标，但点击与菜单照常）。
            // 记一笔，让"悬停事件收不到"有据可查。
            OrielDiagnostics.Report(
                "[OrielWeb] NIM_SETVERSION(NOTIFYICON_VERSION_4) 失败：托盘回调退回旧式形态，" +
                "悬停事件（Enter/Leave）与事件坐标将不可用。");
        }
    }

    private void SetHidden(bool hidden)
    {
        if (!_added)
        {
            return;
        }

        _hidden = hidden;

        var data = CreateData(Win32Constants.NIF_STATE);
        data.dwState = hidden ? Win32Constants.NIS_HIDDEN : 0;
        data.dwStateMask = Win32Constants.NIS_HIDDEN;
        _ = Win32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = _trayId,
        uFlags = flags,
    };

    private void WriteTip(ref NOTIFYICONDATAW data)
    {
        fixed (char* tip = data.szTip)
        {
            WriteFixed(tip, 128, _tooltip);
        }
    }

    /// <summary>写定长宽字符缓冲（截断到 capacity-1 并保证结尾 NUL）。</summary>
    internal static void WriteFixed(char* destination, int capacity, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            destination[0] = '\0';
            return;
        }

        int length = Math.Min(value.Length, capacity - 1);
        for (int i = 0; i < length; i++)
        {
            destination[i] = value[i];
        }

        destination[length] = '\0';
    }

    /// <summary>退回可执行文件自带图标（与窗口图标同源）。</summary>
    private static nint LoadExecutableIcon(out bool owns)
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe)
            && Win32.ExtractIconExW(exe, 0, out _, out nint small, 1) > 0
            && small != 0)
        {
            owns = true;
            return small;
        }

        owns = false;
        return 0;
    }
}
