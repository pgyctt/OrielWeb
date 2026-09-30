using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 托盘后端：<c>GtkStatusIcon</c> + <c>GtkMenu</c>。
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
/// 菜单项点击用 <c>g_signal_connect_data</c> 的 data 参数携带 <see cref="GCHandle"/>：
/// GTK 信号只提供"用户数据"这一个传参口，而我们得把"点了哪一项"带回托管侧。
/// </para>
/// </remarks>
internal sealed unsafe class GtkTrayBackend : ITrayBackend
{
    /// <summary>statusIcon 指针 → 后端实例。静态 trampoline 没有 this，只能按指针回查。</summary>
    private static readonly Dictionary<nint, GtkTrayBackend> s_icons = [];

    private readonly OrielApp _app;
    private nint _statusIcon;
    private nint _menu;
    private string _tooltip;
    private readonly List<GCHandle> _bindingHandles = [];
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
        DestroyMenu();
        _menu = BuildMenu(items);
        if (_menu != 0)
        {
            // 新建的菜单项默认不显示，必须显式 show_all（含子菜单，它们在弹出时才会被真正映射）
            GtkNative.GtkWidgetShowAll(_menu);
        }
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

        // 顺序要紧：先销毁原生菜单（widget 没了就不会再有信号），再释放回调用的 GCHandle，
        // 否则中间那一刻是"回调可能拿到已释放句柄"的窗口。
        DestroyMenu();

        if (_statusIcon != 0)
        {
            s_icons.Remove(_statusIcon);
            GtkNative.GtkStatusIconSetVisible(_statusIcon, 0);
            GtkNative.GObjectUnref(_statusIcon);
            _statusIcon = 0;
        }
    }

    // ---- 菜单构建 ----

    private nint BuildMenu(IReadOnlyList<OrielMenuItem> items)
    {
        nint menu = GtkNative.GtkMenuNew();
        if (menu == 0)
        {
            return 0;
        }

        foreach (OrielMenuItem item in items)
        {
            if (item.IsSeparator)
            {
                GtkNative.GtkMenuShellAppend(menu, GtkNative.GtkSeparatorMenuItemNew());
                continue;
            }

            string label = BuildLabel(item);
            nint widget = item.Checked
                ? GtkNative.GtkCheckMenuItemNewWithLabel(label)
                : GtkNative.GtkMenuItemNewWithLabel(label);

            if (item.Checked)
            {
                GtkNative.GtkCheckMenuItemSetActive(widget, 1);
            }
            if (!item.Enabled)
            {
                GtkNative.GtkWidgetSetSensitive(widget, 0);
            }

            if (item.Items is { Count: > 0 } children)
            {
                nint submenu = BuildMenu(children);
                if (submenu != 0)
                {
                    // 子菜单的引用由父项持有（set_submenu 会 sink）
                    GtkNative.GtkMenuItemSetSubmenu(widget, submenu);
                }
            }
            else
            {
                var binding = new MenuItemBinding(this, item);
                GCHandle handle = GCHandle.Alloc(binding);
                _bindingHandles.Add(handle);
                GtkNative.GSignalConnectData(
                    widget, "activate",
                    (delegate* unmanaged<nint, nint, void>)&OnMenuItemActivate,
                    GCHandle.ToIntPtr(handle), 0, 0);
            }

            GtkNative.GtkMenuShellAppend(menu, widget);
        }

        return menu;
    }

    private static string BuildLabel(OrielMenuItem item)
    {
        string text = item.Label ?? DefaultLabel(item.Role) ?? string.Empty;

        // GTK 的菜单项没有"右对齐显示快捷键"的轻量做法（要 accel group + accel path 才能真按键触发），
        // 这里只把加速键跟在标签后面做提示；按键本身到达页面。
        if (item.Accelerator is { Length: > 0 } acceleratorText
            && OrielAccelerator.TryParse(acceleratorText, out OrielAccelerator? accelerator))
        {
            text += "  (" + accelerator!.DisplayString + ")";
        }

        return text;
    }

    private static string? DefaultLabel(string? role) => role switch
    {
        OrielMenuRole.Quit => "Quit",
        OrielMenuRole.Close => "Close",
        OrielMenuRole.Minimize => "Minimize",
        OrielMenuRole.Zoom => "Maximize",
        OrielMenuRole.ToggleFullScreen => "Full Screen",
        OrielMenuRole.About => "About",
        _ => null,
    };

    private static void ApplyIcon(nint statusIcon, string? iconPath)
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

        GtkNative.GtkStatusIconSetFromFile(statusIcon, iconPath);
    }

    private void ApplyIcon(string? iconPath) => ApplyIcon(_statusIcon, iconPath);

    private void DestroyMenu()
    {
        if (_menu == 0)
        {
            return;
        }

        // 顶层菜单没有父容器持有，引用计数由我们掌握（子菜单由父项持有，不重复释放）
        GtkNative.GObjectUnref(_menu);
        _menu = 0;

        foreach (GCHandle handle in _bindingHandles)
        {
            handle.Free();
        }
        _bindingHandles.Clear();
    }

    internal void ShowMenu()
    {
        if (_menu == 0 || _disposed)
        {
            return;
        }

        // triggerEvent 传 0：用当前指针位置。托盘菜单的弹出信号在部分合成器下拿不到事件对象，
        // 传 0 是 GTK 明确支持的形态。
        GtkNative.GtkMenuPopupAtPointer(_menu, 0);
    }

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            ActivateRole(role);
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
        }
    }

    /// <summary>托盘里可用的 role 只有应用级动作（理由与 Windows 侧一致：没有"当前窗口"这回事）。</summary>
    private void ActivateRole(string role)
    {
        if (role == OrielMenuRole.Quit)
        {
            _app.Quit();
        }
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

    [UnmanagedCallersOnly]
    private static void OnMenuItemActivate(nint menuItem, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is MenuItemBinding binding)
            {
                binding.Backend.Activate(binding.Item);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 菜单项回调抛出异常：{ex}");
        }
    }

    private sealed class MenuItemBinding(GtkTrayBackend backend, OrielMenuItem item)
    {
        internal GtkTrayBackend Backend { get; } = backend;

        internal OrielMenuItem Item { get; } = item;
    }
}
