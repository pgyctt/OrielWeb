using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>菜单栏与菜单命令分发。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    // ------------------------------------------------------------------
    // 菜单栏
    // ------------------------------------------------------------------

    /// <summary>当前挂在窗口上的菜单栏；null 表示没有。</summary>
    private GtkMenu? _menuBar;

    /// <summary>
    /// 设置这个窗口的菜单栏（Linux 的菜单栏属于窗口，所以这条与 <see cref="SetAppMenuBar"/> 同义）。
    /// </summary>
    /// <param name="items">顶层项；空集合表示移除菜单栏。</param>
    /// <remarks>
    /// <para>
    /// <b>布局是 <c>GtkBox{menubar, webview}</c></b>：GTK 的菜单栏是窗口里的一个部件
    /// （与 Windows 的非客户区菜单栏、macOS 的屏幕顶部菜单都不同），所以它必须与 webview
    /// 并排放在一个纵向盒里。这是本批唯一动到窗口结构的地方，也是客户区契约要重验的原因。
    /// </para>
    /// <para>
    /// <b>换菜单栏时的顺序</b>：先从盒里摘掉旧的、放掉它，再装新的。
    /// <c>gtk_box_pack_start</c> 是按调用顺序从起始端排开的，而 webview 已经在 0 号位了，
    /// 所以新菜单栏要用 <c>gtk_box_reorder_child(box, menubar, 0)</c> 挪到最上面——
    /// 不挪的话菜单栏会排到 webview **下面**，表现是"设置了但看不见"。
    /// </para>
    /// </remarks>
    public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        DetachMenuBar();

        if (items.Count == 0 || _rootBox == 0)
        {
            return;
        }

        GtkMenu? bar = GtkMenu.BuildMenuBar(items, item => ActivateMenuItem(item));
        if (bar is null)
        {
            return;
        }

        _menuBar = bar;

        // 菜单栏不占弹性空间（expand=false）：它要的是"刚好一行"，剩下的全归 webview。
        GtkNative.GtkBoxPackStart(_rootBox, bar.Handle, expand: false, fill: false, padding: 0);
        GtkNative.GtkBoxReorderChild(_rootBox, bar.Handle, 0);
        GtkNative.GtkWidgetShowAll(bar.Handle);

        // **要显式排队重算**：pack/remove 只是改了盒的子部件表，webview 的分配尺寸
        // 要等下一次布局走完才更新。不排队的话，紧随其后读 InnerSize 会拿到**改之前**的值
        // ——表现是"加了菜单栏但 innerSize 没变"或"拆了没还回来"。
        GtkNative.GtkWidgetQueueResize(_rootBox);

        // 菜单栏高度变了，最小尺寸的换算也要跟着重算（见 ApplyMinSizeRequest）。
        ApplyMinSizeRequest();
    }

    /// <summary>
    /// 让这个窗口反映应用级菜单栏。
    /// </summary>
    /// <remarks>
    /// Linux 的菜单栏属于窗口，与 <see cref="SetWindowMenuBar"/> 完全同义。
    /// "不覆盖显式设过菜单栏的窗口"那条判断在 <see cref="WebviewWindow.SetAppMenuBar"/>。
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items) => SetWindowMenuBar(items);

    /// <summary>把菜单栏从布局盒里摘掉并释放；没有菜单栏时是空操作。</summary>
    private void DetachMenuBar()
    {
        GtkMenu? bar = _menuBar;
        if (bar is null)
        {
            return;
        }

        _menuBar = null;

        // 先摘再放：不摘的话盒还持着引用，部件不会真正销毁（泄漏）；
        // 反过来（先放再摘）则是盒拿着一个已销毁的 widget，下次布局分配就崩。
        if (_rootBox != 0)
        {
            GtkNative.GtkContainerRemove(_rootBox, bar.Handle);
            // 与装上时同样要紧：移除只是改了子部件表，webview 的分配要等下一次布局。
            GtkNative.GtkWidgetQueueResize(_rootBox);
        }

        bar.Dispose();

        // 菜单栏没了，最小尺寸的换算跟着重算。
        ApplyMinSizeRequest();
    }

    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window, TryExecuteNativeEditing))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 Linux 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }
}
