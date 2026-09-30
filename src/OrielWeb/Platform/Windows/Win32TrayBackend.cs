using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 托盘后端：<c>Shell_NotifyIconW</c> + 弹出菜单。
/// </summary>
/// <remarks>
/// 回调窗口是**本类型专属**的不可见窗口（<see cref="Win32MessageWindow"/>）：
/// 托盘图标不随任何应用窗口存活，而平台的调度窗口与 WebView2 共享消息空间——
/// 自选的 "WM_APP + n" 会与 WebView2 的私有消息撞车（现象见 <see cref="Win32MessageWindow"/> 的说明），
/// 所以这里另开一个窗口，不与任何第三方组件共用消息号。
/// 菜单用 <c>TPM_RETURNCMD</c> 直接取回选中项 id（<see cref="Win32Menu.Popup"/>），不需要命令路由。
/// </remarks>
internal sealed unsafe class Win32TrayBackend : ITrayBackend
{
    /// <summary>本托盘的图标 ID。</summary>
    private const uint TrayId = 1;

    private readonly OrielApp _app;
    private readonly bool _menuOnClick;

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

    public event Action? Clicked;
    public event Action<string>? MenuItemClicked;
    public event Action<string>? RawEvent;

    internal Win32TrayBackend(OrielApp app, OrielTrayOptions options)
    {
        _app = app;
        _menuOnClick = options.MenuOnClick;
        _tooltip = options.Tooltip;
        _icon = LoadIcon(options.IconPath, out _ownsIcon);

        _window = new Win32MessageWindow();
        _window.MessageReceived += OnWindowMessage;

        Add();
    }

    /// <summary>专属窗口收到的消息：托盘回调（左键与右键）都走这里。</summary>
    private void OnWindowMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == Win32Constants.WM_APP_TRAY)
        {
            HandleCallback(wParam, lParam);
        }
    }

    // ---- 原生回调入口（由调度窗口的 WndProc 转发）----

    /// <summary>
    /// 托盘回调。两种送法都接：
    /// <list type="bullet">
    /// <item><b>V4</b>（<c>NIM_SETVERSION(NOTIFYICON_VERSION_4)</c>）：<paramref name="wParam"/> 是事件类型，
    /// 左键 <c>NIN_SELECT</c>、右键直接就是 <c>WM_CONTEXTMENU</c>，坐标在 <paramref name="lParam"/>。</item>
    /// <item><b>旧式</b>：<paramref name="wParam"/> 是图标 ID，<paramref name="lParam"/> 是鼠标消息
    /// （<c>WM_LBUTTONUP</c> / <c>WM_RBUTTONUP</c>）。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <b>右键也在这里，不在窗口消息里</b>：托盘事件一律经 <c>uCallbackMessage</c> 送达。
    /// 早先只在窗口过程里等一条发给窗口的 <c>WM_CONTEXTMENU</c>，所以右键永远没人处理。
    /// <para>
    /// 送法有两种，两条都认，但**不依赖版本设置是否生效**：
    /// 旧式（默认，参考实现 Ryn 也走这条）<c>wParam</c> 是图标 ID、<c>lParam</c> 的**低字**是鼠标消息；
    /// V4（调用过 <c>NIM_SETVERSION</c>）则把事件类型放进 <c>wParam</c>。
    /// 分辨只需看 <c>wParam</c> 是不是本托盘的 ID。
    /// </para>
    /// </remarks>
    internal void HandleCallback(nuint wParam, nint lParam)
    {
        uint legacyEvent = (uint)(lParam & 0xFFFF);
        uint eventType = wParam == TrayId ? legacyEvent : (uint)wParam;

        // 诊断输出：把原始参数与解析结果一起报出去。"点了没反应"的三种可能
        //（事件没送达 / 送达了没识别 / 识别了菜单没弹出）靠这一行就能分开。
        RawEvent?.Invoke(wParam == TrayId
            ? $"旧式回调：wParam=0x{wParam:X}（图标 ID）lParam=0x{(ulong)lParam:X} → 事件 0x{legacyEvent:X}"
            : $"V4 回调：wParam=0x{wParam:X}（事件类型）lParam=0x{(ulong)lParam:X}");

        if (eventType is Win32Constants.NIN_SELECT or Win32Constants.WM_LBUTTONUP)
        {
            Clicked?.Invoke();
            if (_menuOnClick)
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

    /// <summary>弹出托盘菜单（右键事件与旧式窗口消息两个入口都调它）。</summary>
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

    public void SetIcon(string? iconPath)
    {
        nint icon = LoadIcon(iconPath, out bool owns);
        if (icon == 0)
        {
            // 新图标加载失败：保留原图标而不是把托盘变成空白（用户看不出发生了什么）
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] 托盘图标加载失败：{iconPath}");
            return;
        }

        nint previous = _icon;
        bool previousOwned = _ownsIcon;
        _icon = icon;
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
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 托盘菜单的 role「{role}」在当前上下文无法执行，已忽略。");
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
            System.Diagnostics.Debug.WriteLine("[OrielWeb] Shell_NotifyIcon(NIM_ADD) 失败：托盘不可用。");
            return;
        }

        _added = true;

        // 刻意**不调** NIM_SETVERSION，就停在旧式回调上（参考实现 Ryn 也是这样）。
        // 原因有二：一是旧式形态简单可靠（wParam = 图标 ID、lParam 低字 = 鼠标消息）；
        // 二是 NOTIFYICONDATA.uTimeoutOrVersion 是联合体，调了版本它就被读作"版本号"、
        // 不调则被读作"气球停留毫秒数"——两边都不小心就会写出自相矛盾的值（见 Win32BalloonIcon 的历史坑）。
        // 不碰版本设置，这个字段的语义就始终是它字面上的意思。
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
        uID = TrayId,
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

    /// <summary>指定文件优先；未指定或加载失败时退回 exe 自带图标（与窗口图标同源）。通知载体也用它。</summary>
    internal static nint LoadIcon(string? path, out bool owns)
    {
        if (!string.IsNullOrEmpty(path))
        {
            nint fromFile = Win32.LoadImageW(
                0,
                path,
                Win32Constants.IMAGE_ICON,
                Win32.GetSystemMetrics(Win32Constants.SM_CXSMICON),
                Win32.GetSystemMetrics(Win32Constants.SM_CYSMICON),
                Win32Constants.LR_LOADFROMFILE);
            if (fromFile != 0)
            {
                owns = true;
                return fromFile;
            }
        }

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
