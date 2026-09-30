using System.Diagnostics;
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

    public event Action? Clicked;
    public event Action<string>? MenuItemClicked;

    internal GtkTrayBackend(OrielApp app, OrielTrayOptions options)
    {
        _app = app;
        _tooltip = options.Tooltip;

        _statusIcon = GtkNative.GtkStatusIconNew();
        if (_statusIcon == 0)
        {
            throw new InvalidOperationException("创建托盘图标失败（gtk_status_icon_new 返回 0）。");
        }

        s_icons[_statusIcon] = this;

        ApplyIcon(options.IconPath);
        GtkNative.GtkStatusIconSetTooltipText(_statusIcon, _tooltip);

        GtkNative.GSignalConnectData(
            _statusIcon, "activate",
            (delegate* unmanaged<nint, nint, void>)&OnActivate, 0, 0, 0);
        GtkNative.GSignalConnectData(
            _statusIcon, "popup-menu",
            (delegate* unmanaged<nint, uint, uint, nint, void>)&OnPopupMenu, 0, 0, 0);

        GtkNative.GtkStatusIconSetVisible(_statusIcon, 1);
    }

    /// <summary>
    /// 图标是否真的进了托盘区。false 在 Linux 上很常见且是**平台事实**：
    /// GNOME Shell 默认不显示托盘（需要 AppIndicator 扩展），Wayland 会话下多数合成器也不显示。
    /// </summary>
    public bool IsVisible => _statusIcon != 0 && GtkNative.GtkStatusIconIsEmbedded(_statusIcon) != 0;

    // ---- ITrayBackend ----

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (_statusIcon != 0)
        {
            GtkNative.GtkStatusIconSetTooltipText(_statusIcon, tooltip);
        }
    }

    public void SetIcon(string? iconPath)
    {
        if (_statusIcon != 0)
        {
            ApplyIcon(iconPath);
        }
    }

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
                Debug.WriteLine($"[OrielWeb] 托盘菜单的 role「{role}」在当前上下文无法执行，已忽略。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
        }
    }

    private void ApplyIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath))
        {
            return;
        }

        if (!File.Exists(iconPath))
        {
            Debug.WriteLine($"[OrielWeb] 托盘图标文件不存在：{iconPath}");
            return;
        }

        GtkNative.GtkStatusIconSetFromFile(_statusIcon, iconPath);
    }

    private void RaiseClicked() => Clicked?.Invoke();

    // ---- 原生回调（静态 trampoline）----

    [UnmanagedCallersOnly]
    private static void OnActivate(nint statusIcon, nint data)
    {
        try
        {
            if (s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                backend.RaiseClicked();
            }
        }
        catch (Exception ex)
        {
            // 异常不得穿越原生边界（外泄 = 进程终止）
            Debug.WriteLine($"[OrielWeb] 托盘点击回调抛出异常：{ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnPopupMenu(nint statusIcon, uint button, uint activateTime, nint data)
    {
        try
        {
            if (s_icons.TryGetValue(statusIcon, out GtkTrayBackend? backend))
            {
                backend.ShowMenu();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 托盘菜单回调抛出异常：{ex}");
        }
    }
}
