using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// GTK 菜单的构建、弹出与释放（托盘菜单、窗口上下文菜单共用）。
/// </summary>
/// <remarks>
/// <para>
/// 抽出理由与 Windows 侧一致：分隔线、禁用、勾选、子菜单、加速键提示这些规则在三处完全相同。
/// role 的语义由 <see cref="OrielMenuRoles"/> 统一解释，这里只负责"画"与"报回选择"。
/// </para>
/// <para>
/// 菜单项点击用 <c>g_signal_connect_data</c> 的 data 参数携带 <see cref="GCHandle"/>：
/// GTK 信号只有"用户数据"这一个传参口，而我们要把"点了哪一项"带回托管侧。
/// </para>
/// <para>
/// <b>弹出是异步的</b>（<c>gtk_menu_popup_at_pointer</c> 立即返回，用户之后才会选），
/// 因此调用方必须让菜单对象活到选择发生——不能像 Windows 那样弹出后立刻释放。
/// </para>
/// </remarks>
internal sealed unsafe class GtkMenu : IDisposable
{
    private nint _handle;
    private readonly List<GCHandle> _bindings = [];
    private bool _disposed;

    private GtkMenu(nint handle) => _handle = handle;

    /// <summary>原生菜单句柄。<c>gtk_menu_item_set_submenu</c> 之类的原生调用会用到。</summary>
    internal nint Handle => _handle;

    internal bool IsEmpty => _handle == 0;

    /// <summary>构建菜单；<paramref name="items"/> 为空时返回 null。</summary>
    internal static GtkMenu? Build(IReadOnlyList<OrielMenuItem> items, Action<OrielMenuItem> onActivate)
    {
        if (items.Count == 0)
        {
            return null;
        }

        nint handle = GtkNative.GtkMenuNew();
        if (handle == 0)
        {
            return null;
        }

        // gtk_menu_new() 给的是 floating 引用（GtkWidget 都继承 GInitiallyUnowned）。
        // 顶层菜单没有父容器替我们 sink，所以在这里取走它，Dispose 里的 unref 才是配对的。
        // 不 sink 就直接 unref 会打乱引用计数：GTK 报 "A floating object was finalized"，
        // 连续弹几次菜单就可能把同一个对象销毁两次。子菜单不在此列——set_submenu 会 sink。
        GtkNative.GObjectRefSink(handle);

        var menu = new GtkMenu(handle);
        menu.AppendAll(handle, items, onActivate);

        // 新建的菜单项默认不显示，必须显式 show_all（含子菜单，它们在弹出时才被真正映射）
        GtkNative.GtkWidgetShowAll(handle);
        return menu;
    }

    /// <summary>
    /// 在指针位置弹出。
    /// </summary>
    /// <param name="triggerEvent">
    /// 触发本次弹出的 <c>GdkEvent</c>。从信号处理器里弹时**必须**把它传进来：GTK 靠这个事件
    /// 定位并决定抓取，传 0 时它会报 "no trigger event for menu popup" 并弹不出来。
    /// 从异步 IPC 回调里弹（没有当前事件）时只能传 0，此时 GTK 退化为"用当前指针位置"。
    /// </param>
    internal void Popup(nint triggerEvent = 0)
    {
        if (_handle != 0 && !_disposed)
        {
            GtkNative.GtkMenuPopupAtPointer(_handle, triggerEvent);
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
        if (_handle != 0)
        {
            // 顶层菜单没有父容器持有，引用计数由我们掌握（子菜单由父项持有，不重复释放）
            GtkNative.GObjectUnref(_handle);
            _handle = 0;
        }

        foreach (GCHandle handle in _bindings)
        {
            handle.Free();
        }
        _bindings.Clear();
    }

    private void AppendAll(nint menu, IReadOnlyList<OrielMenuItem> items, Action<OrielMenuItem> onActivate)
    {
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
                nint submenu = GtkNative.GtkMenuNew();
                if (submenu != 0)
                {
                    AppendAll(submenu, children, onActivate);
                    GtkNative.GtkWidgetShowAll(submenu);
                    // 子菜单的引用由父项持有（set_submenu 会 sink）
                    GtkNative.GtkMenuItemSetSubmenu(widget, submenu);
                }
            }
            else
            {
                var binding = new MenuBinding(item, onActivate);
                GCHandle handle = GCHandle.Alloc(binding);
                _bindings.Add(handle);
                GtkNative.GSignalConnectData(
                    widget, "activate",
                    (delegate* unmanaged<nint, nint, void>)&OnActivate,
                    GCHandle.ToIntPtr(handle), 0, 0);
            }

            GtkNative.GtkMenuShellAppend(menu, widget);
        }
    }

    private static string BuildLabel(OrielMenuItem item)
    {
        string text = item.Label ?? OrielMenuRoles.DefaultLabel(item.Role) ?? string.Empty;

        // GTK 菜单项没有"右对齐显示快捷键"的轻量做法（要 accel group + accel path 才能真按键触发），
        // 这里只把加速键跟在标签后面做提示；按键本身到达页面。
        if (item.Accelerator is { Length: > 0 } acceleratorText
            && OrielAccelerator.TryParse(acceleratorText, out OrielAccelerator? accelerator))
        {
            text += "  (" + accelerator!.DisplayString + ")";
        }

        return text;
    }

    [UnmanagedCallersOnly]
    private static void OnActivate(nint menuItem, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is MenuBinding binding)
            {
                binding.OnActivate(binding.Item);
            }
        }
        catch (Exception ex)
        {
            // 异常不得穿越原生边界（外泄 = 进程终止）
            Debug.WriteLine($"[OrielWeb] 菜单项回调抛出异常：{ex}");
        }
    }

    private sealed class MenuBinding(OrielMenuItem item, Action<OrielMenuItem> onActivate)
    {
        internal OrielMenuItem Item { get; } = item;

        internal Action<OrielMenuItem> OnActivate { get; } = onActivate;
    }
}
