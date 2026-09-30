using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 托盘后端：<c>Shell_NotifyIconW</c> + 弹出菜单。
/// </summary>
/// <remarks>
/// 回调窗口复用平台的调度窗口（<see cref="WindowsPlatformBackend.MessageWindowHandle"/>）：
/// 托盘图标不随任何窗口存活，而调度窗口的生命周期与消息循环一致，正是它需要的宿主。
/// 菜单用 <c>TPM_RETURNCMD</c> 直接取回选中项 id（<see cref="Win32Menu.Popup"/>），不需要命令路由。
/// </remarks>
internal sealed unsafe class Win32TrayBackend : ITrayBackend
{
    /// <summary>本托盘的图标 ID（同一窗口上"用户托盘"与"通知气球"用不同 ID 区分）。</summary>
    private const uint TrayId = 1;

    private readonly WindowsPlatformBackend _owner;
    private readonly OrielApp _app;
    private readonly bool _menuOnClick;

    private nint _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _hidden;
    private string _tooltip = "OrielWeb";
    private Win32Menu? _menu;
    private bool _disposed;

    public event Action? Clicked;
    public event Action<string>? MenuItemClicked;

    internal Win32TrayBackend(WindowsPlatformBackend owner, OrielApp app, OrielTrayOptions options)
    {
        _owner = owner;
        _app = app;
        _menuOnClick = options.MenuOnClick;
        _tooltip = options.Tooltip;
        _icon = LoadIcon(options.IconPath, out _ownsIcon);
        Add();
    }

    // ---- 原生回调入口（由调度窗口的 WndProc 转发）----

    /// <summary>
    /// V4 回调：<paramref name="eventType"/> 是回调消息 <c>wParam</c> 里的事件类型。
    /// </summary>
    /// <remarks>
    /// <b>右键在这里，不在窗口消息里</b>：V4 起所有托盘事件都经 <c>uCallbackMessage</c> 送达，
    /// 右键的 <c>wParam</c> 就是 <c>WM_CONTEXTMENU</c>（<c>0x007B</c>）——它**不是**一条发给窗口的
    /// <c>WM_CONTEXTMENU</c> 消息。早先只在窗口过程里等那条消息，于是右键永远没人处理、菜单弹不出来。
    /// 另外 V4 的 <c>lParam</c> 低字是图标中心的 x、高字是 y（这里仍用
    /// <see cref="Win32Menu.Popup"/> 里的 <c>GetCursorPos</c>：右键时指针就在图标上，两者一致，
    /// 而前者在读坐标的场合更简单）。
    /// </remarks>
    internal void HandleCallback(nuint eventType)
    {
        if (eventType == Win32Constants.NIN_SELECT)
        {
            Clicked?.Invoke();
            if (_menuOnClick)
            {
                ShowMenu();
            }
            return;
        }

        if (eventType == Win32Constants.WM_CONTEXTMENU)
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

        OrielMenuItem? item = _menu?.Popup(_owner.MessageWindowHandle);
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
        SetVersion4();
    }

    private void SetVersion4()
    {
        var data = CreateData(0);
        data.uVersionOrTimeout = Win32Constants.NOTIFYICON_VERSION_4;
        _ = Win32.Shell_NotifyIconW(Win32Constants.NIM_SETVERSION, ref data);
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
        hWnd = _owner.MessageWindowHandle,
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
