using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 的通知载体：一个隐藏的托盘项 + <c>NIF_INFO</c> 气球。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 WinRT 的 Toast</b>：NativeAOT 下没有 WinRT 投影（Ryn 的注释记着同一件事，
/// 它为此改走 PowerShell 子进程），而"未打包应用要有可点击的 Toast"还要求开始菜单快捷方式
/// 携带 AUMID 并注册 COM 激活器——那是打包器的职责，不是库该做的。托盘气球只需要一个图标句柄，
/// 且点击能可靠回调。
/// </para>
/// <para>
/// <b>为什么单独占一个隐藏托盘项</b>：通知应当独立于用户是否启用托盘（<c>NIS_HIDDEN</c> 让它
/// 不出现在托盘区）。复用用户托盘会让"没配托盘就发不了通知"成为隐藏前提。
/// </para>
/// </remarks>
internal sealed unsafe class Win32BalloonIcon : IDisposable
{
    /// <summary>通知用图标 ID（与用户托盘的 ID 1 区分开）。</summary>
    private const uint Id = 2;

    private readonly nint _hwnd;
    private bool _added;
    private nint _icon;
    private bool _ownsIcon;
    private bool _disposed;
    private string? _lastNotificationId;

    internal event Action<string>? Clicked;

    internal Win32BalloonIcon(nint hwnd) => _hwnd = hwnd;

    /// <summary>
    /// 展示一条通知。同一条隐藏托盘项上连续调用会替换上一条（系统一次只显示一个气球）。
    /// </summary>
    /// <returns>是否已成功提交给系统（<c>NIM_MODIFY</c> 的返回值）——系统没显示横幅时，
    /// 这个返回值能区分"没提交成功"（实现问题）与"提交了但被系统的通知设置拦住"。</returns>
    internal bool Show(OrielNotificationOptions notification)
    {
        if (!EnsureAdded())
        {
            return false;
        }

        // 气球点击时系统只回传"哪个图标的气球被点了"，没有通知 id 这个概念，
        // 所以记忆最近一次发出的 id：这是平台限制下的最佳对应（注释在 OrielNotificationOptions.Id 上）。
        _lastNotificationId = notification.Id;

        var data = CreateData(Win32Constants.NIF_INFO);
        // data 是局部变量：它内部的 fixed buffer 已在栈上固定，直接取地址即可（再套 fixed 会报 CS0213）
        Win32TrayBackend.WriteFixed(data.szInfoTitle, 64, notification.Title);
        Win32TrayBackend.WriteFixed(data.szInfo, 256, notification.Body);

        data.dwInfoFlags = Win32Constants.NIIF_INFO;

        // 这个成员是 uTimeout / uVersion 的联合体，解释权归 shell：由于 EnsureAdded 已经调用过
        // NIM_SETVERSION(NOTIFYICON_VERSION_4)，它在这里被读作**版本**。
        // 早先填的是 10000（当作"停留 10 秒"）——那等于告诉 shell"版本 10000"，
        // 是自相矛盾的无意义值，可能让 shell 直接丢掉整条通知。填版本号才是正确语义。
        data.uVersionOrTimeout = Win32Constants.NOTIFYICON_VERSION_4;

        return Win32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 气球回调（由调度窗口按 <see cref="Win32Constants.WM_APP_NOTIFY"/> 转发）。
    /// </summary>
    /// <remarks>
    /// 与托盘一样兼容两种送法：V4 下 <paramref name="wParam"/> 是事件类型，
    /// 旧式下它是图标 ID 而事件类型在 <paramref name="lParam"/> 里。
    /// </remarks>
    internal void HandleCallback(nuint wParam, nint lParam)
    {
        uint eventType = wParam == Id ? (uint)lParam : (uint)wParam;

        if (eventType == Win32Constants.NIN_BALLOONUSERCLICK && _lastNotificationId is { Length: > 0 } id)
        {
            Clicked?.Invoke(id);
        }
    }

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

        if (_ownsIcon && _icon != 0)
        {
            _ = Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private bool EnsureAdded()
    {
        if (_added)
        {
            return true;
        }

        _icon = Win32TrayBackend.LoadIcon(null, out _ownsIcon);
        var data = CreateData(
            Win32Constants.NIF_MESSAGE | Win32Constants.NIF_ICON | Win32Constants.NIF_STATE);
        data.uCallbackMessage = Win32Constants.WM_APP_NOTIFY;
        data.dwState = Win32Constants.NIS_HIDDEN;
        data.dwStateMask = Win32Constants.NIS_HIDDEN;
        data.hIcon = _icon;

        if (!Win32.Shell_NotifyIconW(Win32Constants.NIM_ADD, ref data))
        {
            System.Diagnostics.Debug.WriteLine("[OrielWeb] 通知载体创建失败：系统通知不可用。");
            return false;
        }

        _added = true;

        // V4 后回调的 wParam 才是事件类型（NIN_BALLOONUSERCLICK）；不设版本拿不到统一的回调形态。
        // flags 沿用 NIM_ADD 那一套（官方示例同样复用结构体），失败只是回退到旧式送法。
        var version = CreateData(Win32Constants.NIF_MESSAGE | Win32Constants.NIF_ICON | Win32Constants.NIF_STATE);
        version.uCallbackMessage = Win32Constants.WM_APP_NOTIFY;
        version.dwState = Win32Constants.NIS_HIDDEN;
        version.dwStateMask = Win32Constants.NIS_HIDDEN;
        version.hIcon = _icon;
        version.uVersionOrTimeout = Win32Constants.NOTIFYICON_VERSION_4;
        if (!Win32.Shell_NotifyIconW(Win32Constants.NIM_SETVERSION, ref version))
        {
            System.Diagnostics.Debug.WriteLine("[OrielWeb] 通知载体 NIM_SETVERSION 失败：回退到旧式回调形态。");
        }

        return true;
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = Id,
        uFlags = flags,
    };
}
