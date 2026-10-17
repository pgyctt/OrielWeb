using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 托盘后端：<c>GtkStatusIcon</c> + <see cref="GtkMenu"/>。
/// </summary>
/// <remarks>
/// <para>
/// 用 GTK3 自带的 <c>GtkStatusIcon</c>（在 GTK3 里已标 deprecated）而不是 AppIndicator：
/// 后者是另一个原生库，会与 GTK 的进程级类型注册表冲突（Ryn 正是为此改走纯 D-Bus），
/// 而本库的定位是"零额外原生依赖"。代价是显示与否完全取决于宿主桌面环境——
/// GNOME Shell 需要 AppIndicator 扩展、Wayland 会话多数不显示，这一点写在
/// <see cref="OrielTray"/> 的文档里，不假装能用。
/// </para>
/// <para>
/// 菜单的构建与弹出交给 <see cref="GtkMenu"/>（与窗口上下文菜单共用同一套规则）。
/// </para>
/// <para>
/// <b>多托盘</b>：每个实例各持一个 <c>GtkStatusIcon</c>，静态表按指针分辨归属——
/// 与 Windows 的 <c>uID</c> 表是同一套思路。
/// </para>
/// <para>
/// <b>事件能力</b>：<c>GtkStatusIcon</c> 只发 <c>activate</c>（左键）与 <c>popup-menu</c>（右键）
/// 两个信号，<b>拿不到</b> Enter/Move/Leave 与按键状态——这是平台事实（对标 Tauri 2 同样标注
/// Linux: Unsupported）。因此这里只报 Click（按键未知）。
/// </para>
/// </remarks>
internal sealed unsafe class GtkTrayBackend : ITrayBackend
{
    /// <summary>statusIcon 指针 → 后端实例。静态 trampoline 没有 this，只能按指针回查。</summary>
    private static readonly Dictionary<nint, GtkTrayBackend> s_icons = [];

    private readonly OrielApp _app;
    private nint _statusIcon;
    private GtkMenu? _menu;
    private string _tooltip;
    private bool _disposed;

    public string Id { get; }

    public event Action? Clicked;
    public event Action<OrielTrayEvent>? EventReceived;
    public event Action<string>? MenuItemClicked;
    public event Action<string>? RawEvent;

    internal GtkTrayBackend(OrielApp app, OrielTrayOptions options, string id)
    {
        _app = app;
        Id = id;
        _tooltip = options.Tooltip;

        _statusIcon = GtkNative.GtkStatusIconNew();
        if (_statusIcon == 0)
        {
            throw new InvalidOperationException("创建托盘图标失败（gtk_status_icon_new 返回 0）。");
        }

        s_icons[_statusIcon] = this;

        ApplyIcon(options);
        GtkNative.GtkStatusIconSetTooltipText(_statusIcon, _tooltip);

        GtkNative.GSignalConnectData(
            _statusIcon, "activate",
            (delegate* unmanaged<nint, nint, void>)&OnActivate, 0, 0, 0);
        GtkNative.GSignalConnectData(
            _statusIcon, "popup-menu",
            (delegate* unmanaged<nint, uint, uint, nint, void>)&OnPopupMenu, 0, 0, 0);

        // button-press/release-event：GTK 的状态图标**没有** Enter/Move/Leave 信号
        //（权威信号表：activate / button-press-event / button-release-event / popup-menu /
        // query-tooltip / scroll-event / size-changed），但这两个能给出**按键与坐标**——
        // 有了它们，Click 事件的 button/buttonState/position 才与另外两个平台对等。
        GtkNative.GSignalConnectData(
            _statusIcon, "button-press-event",
            (delegate* unmanaged<nint, nint, nint, int>)&OnButtonPress, 0, 0, 0);
        GtkNative.GSignalConnectData(
            _statusIcon, "button-release-event",
            (delegate* unmanaged<nint, nint, nint, int>)&OnButtonRelease, 0, 0, 0);

        GtkNative.GtkStatusIconSetVisible(_statusIcon, 1);
    }

    /// <summary>
    /// 按创建选项套用图标。
    /// </summary>
    /// <remarks>
    /// 统一走 <see cref="OrielTrayOptions.ResolveIcon"/>：读文件与解码在托管侧完成
    /// （PNG/ICO 都吃），三平台共用同一个解码器与同一条失败路径。
    /// 解码成功的走 pixbuf（不必落盘），失败/未设置则不加图标。
    /// </remarks>
    private void ApplyIcon(OrielTrayOptions options)
    {
        if (options.ResolveIcon() is { } image)
        {
            ApplyPixbuf(image);
        }
    }

    private void ApplyPixbuf(OrielImage image)
    {
        nint pixbuf = GtkPixbufFactory.CreatePixbuf(image);
        if (pixbuf == 0)
        {
            // 造不出来：记一笔并保留原图标，不让托盘变空白
            OrielDiagnostics.Report("[OrielWeb] 托盘图标：由 RGBA 造 pixbuf 失败，保留原图标。");
            return;
        }

        try
        {
            GtkNative.GtkStatusIconSetFromPixbuf(_statusIcon, pixbuf);
        }
        finally
        {
            // GtkStatusIcon 会自己 ref 住 pixbuf，这里放掉我们那一份
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    // ---- ITrayBackend ----

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (_statusIcon != 0)
        {
            GtkNative.GtkStatusIconSetTooltipText(_statusIcon, tooltip);
        }
    }

    /// <summary>
    /// 设置图标旁文字。
    /// </summary>
    /// <remarks>
    /// 落到 <c>gtk_status_icon_set_title</c>，但**语义与 Tauri 的 Linux title 不同**：
    /// GTK 文档写明该 title "may be used by tools like screen readers to render the tray icon"
    /// ——它是无障碍标签，**不会**画在托盘上（那需要 AppIndicator/KSNI 协议的能力）。
    /// 如实标注这条差异，不假装等价。
    /// </remarks>
    public void SetTitle(string? title)
    {
        if (_statusIcon != 0)
        {
            GtkNative.GtkStatusIconSetTitle(_statusIcon, title ?? string.Empty);
        }
    }

    /// <summary>模板图标记（仅 macOS 有意义）；Linux 显式空操作。</summary>
    public void SetIconAsTemplate(bool asTemplate)
    {
        _ = asTemplate;
    }

    public void SetIcon(OrielImage? icon)
    {
        if (_statusIcon == 0)
        {
            return;
        }

        if (icon is null)
        {
            // GtkStatusIcon 没有"清空图标"的入口：给一张全透明 pixbuf，视觉上等价于"没有图标"。
            // 直接返回会让"换回默认"变成空操作，与另外两个平台的语义不一致。
            ApplyPixbuf(TransparentPixbuf());
            return;
        }

        ApplyPixbuf(icon);
    }

    /// <summary>一张 1x1 全透明的 pixbuf（用于"清空图标"）。</summary>
    private static OrielImage TransparentPixbuf() => new(new byte[4], 1, 1);

    public void SetMenu(IReadOnlyList<OrielMenuItem> items)
    {
        _menu?.Dispose();
        _menu = GtkMenu.Build(items, Activate);
    }

    public void Show()
    {
        if (_statusIcon != 0)
        {
            GtkNative.GtkStatusIconSetVisible(_statusIcon, 1);
        }
    }

    public void Hide()
    {
        if (_statusIcon != 0)
        {
            GtkNative.GtkStatusIconSetVisible(_statusIcon, 0);
        }
    }

    public bool IsVisible => _statusIcon != 0 && GtkNative.GtkStatusIconIsEmbedded(_statusIcon) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _menu?.Dispose();
        _menu = null;

        if (_statusIcon != 0)
        {
            s_icons.Remove(_statusIcon);
            GtkNative.GtkStatusIconSetVisible(_statusIcon, 0);
            GtkNative.GObjectUnref(_statusIcon);
            _statusIcon = 0;
        }
    }

    internal void ShowMenu() => _menu?.Popup();

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            // 托盘没有"当前窗口"：窗口级与编辑类 role 在这里没有明确目标，忽略而不是猜一个窗口
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

    /// <summary>
    /// 上报一次点击。
    /// </summary>
    /// <remarks>
    /// 由 <c>button-press/release-event</c> 驱动（那两个信号带按键与坐标），
    /// 因此能给出 button/buttonState/position。若只收到 <c>activate</c>（某些桌面环境只发它），
    /// 则按键与坐标为 <c>null</c>——如实标注"平台未提供"，而不是猜一个 Left 出来。
    /// </remarks>
    private void RaiseClickEvent(
        OrielTrayMouseButton? button = null,
        OrielTrayButtonState? state = null,
        OrielTrayPoint? position = null,
        bool isDoubleClick = false)
    {
        EventReceived?.Invoke(new OrielTrayEvent(
            Id,
            isDoubleClick ? OrielTrayEventType.DoubleClick : OrielTrayEventType.Click,
            button,
            state,
            position));
    }

    /// <summary>把 GDK 的按键号映射成我们的枚举（GDK：1=左、2=中、3=右）。</summary>
    private static OrielTrayMouseButton? MapButton(uint gdkButton) => gdkButton switch
    {
        1 => OrielTrayMouseButton.Left,
        2 => OrielTrayMouseButton.Middle,
        3 => OrielTrayMouseButton.Right,
        _ => null, // 4~5 是滚轮，不是"按键"
    };

    /// <summary>读一次事件的坐标（根窗口坐标，与另外两平台的屏幕坐标同义）。</summary>
    private static OrielTrayPoint? ReadPosition(nint eventPtr)
        => GtkNative.GdkEventGetRootCoords(eventPtr, out double x, out double y) != 0
            ? new OrielTrayPoint(x, y)
            : null;

    // ---- 原生回调（静态 trampoline）----

    [UnmanagedCallersOnly]
    private static void OnActivate(nint statusIcon, nint data)
    {
        try
        {
            if (s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                backend.RawEvent?.Invoke("信号 activate（激活）");
                backend.RaiseClickEvent();
                backend.Clicked?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // 异常不得穿越原生边界（外泄 = 进程终止）
            OrielDiagnostics.Report($"[OrielWeb] 托盘点击回调抛出异常：{ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static int OnButtonPress(nint statusIcon, nint eventPtr, nint data)
    {
        try
        {
            if (!s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                return 0; // FALSE：不阻止默认处理
            }

            // GDK 的事件类型在结构体首字段：GDK_BUTTON_PRESS=4、GDK_2BUTTON_PRESS=5、GDK_3BUTTON_PRESS=6
            int eventType = *(int*)eventPtr;
            bool isDouble = eventType == 5;

            _ = GtkNative.GdkEventGetButton(eventPtr, out uint gdkButton);
            OrielTrayMouseButton? button = MapButton(gdkButton);

            backend.RawEvent?.Invoke(
                $"信号 button-press-event（GDK 类型 {eventType}，按键 {gdkButton}）");

            backend.RaiseClickEvent(button, OrielTrayButtonState.Down, ReadPosition(eventPtr), isDouble);
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘按键回调抛出异常：{ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnButtonRelease(nint statusIcon, nint eventPtr, nint data)
    {
        try
        {
            if (!s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                return 0;
            }

            _ = GtkNative.GdkEventGetButton(eventPtr, out uint gdkButton);
            OrielTrayMouseButton? button = MapButton(gdkButton);

            backend.RawEvent?.Invoke($"信号 button-release-event（按键 {gdkButton}）");

            backend.RaiseClickEvent(button, OrielTrayButtonState.Up, ReadPosition(eventPtr));
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘按键回调抛出异常：{ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static void OnPopupMenu(nint statusIcon, uint button, uint activateTime, nint data)
    {
        try
        {
            if (s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                backend.RawEvent?.Invoke($"信号 popup-menu（button={button}）");
                backend.ShowMenu();
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘菜单回调抛出异常：{ex}");
        }
    }
}
