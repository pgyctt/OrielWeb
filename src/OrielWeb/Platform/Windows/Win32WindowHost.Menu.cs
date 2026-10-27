using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionDevice;

using DCompTarget = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionTarget;
using DCompVisual = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>窗口菜单栏与菜单命令分发。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    // ------------------------------------------------------------------
    // 菜单栏
    // ------------------------------------------------------------------

    /// <summary>当前挂在窗口上的菜单栏；null 表示没有。</summary>
    private Win32Menu? _menuBar;

    /// <summary>
    /// 设置这个窗口的菜单栏。
    /// </summary>
    /// <param name="items">顶层项；空集合表示移除菜单栏。</param>
    /// <remarks>
    /// <para>
    /// <b>换成新菜单栏时的顺序很重要</b>：必须先 <c>SetMenu(hwnd, 0)</c> 把旧的摘下来，
    /// 再 <c>Dispose</c> 它。反过来的话窗口会持有一个已销毁的 HMENU，用户下一次点菜单栏
    /// 就是 use-after-free（崩溃点在 user32 里，栈上看不出与本库有关）。
    /// </para>
    /// <para>
    /// 装上/摘掉之后都要 <see cref="Win32.DrawMenuBar"/>：否则菜单栏区域不重画，
    /// 表现是"设置了但看不见"或"摘掉了还留一条空白"。
    /// </para>
    /// </remarks>
    public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        DetachMenuBar();

        if (items.Count == 0)
        {
            // 空集合 = 显式移除。不是"没设置"——页面的意图是"这个窗口不要菜单栏"。
            _ = Win32.DrawMenuBar(_hwnd);
            return;
        }

        Win32Menu? bar = Win32Menu.Build(items, asMenuBar: true);
        if (bar is null)
        {
            _ = Win32.DrawMenuBar(_hwnd);
            return;
        }

        _menuBar = bar;
        _ = Win32.SetMenu(_hwnd, bar.Handle);
        _ = Win32.DrawMenuBar(_hwnd);

        // 无边框窗口要自己让出菜单栏高度（装饰窗口由 DefWindowProc 的 WM_NCCALCSIZE 让）。
        // 不做这一步的话菜单栏会画在客户区顶上、盖住页面顶部。
        if (_options.Frameless)
        {
            _ = Win32.SetWindowPos(
                _hwnd, 0, 0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE |
                Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE |
                Win32Constants.SWP_FRAMECHANGED);
        }
    }

    /// <summary>
    /// 让这个窗口反映应用级菜单栏。
    /// </summary>
    /// <remarks>
    /// Windows 的菜单栏属于窗口，所以这里就是"把这份内容装到我这个窗口上"。
    /// "不覆盖显式设过菜单栏的窗口"那条判断在 <see cref="WebviewWindow.SetAppMenuBar"/>，
    /// 不在后端——后端只管怎么装。
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items) => SetWindowMenuBar(items);

    /// <summary>把菜单栏从窗口上摘下来并释放；没有菜单栏时是空操作。</summary>
    /// <remarks>
    /// 顺序是"先摘、后放"，理由见 <see cref="SetWindowMenuBar"/> 的说明。
    /// </remarks>
    private void DetachMenuBar()
    {
        Win32Menu? bar = _menuBar;
        if (bar is null)
        {
            return;
        }

        _menuBar = null;
        if (_hwnd != 0)
        {
            _ = Win32.SetMenu(_hwnd, 0);
        }

        bar.Dispose();
    }

    /// <summary>菜单栏里的项被点中（由 WndProc 的 WM_COMMAND 调）。</summary>
    private void OnMenuCommand(uint commandId)
    {
        OrielMenuItem? item = _menuBar?.Find(commandId);
        if (item is null)
        {
            // 不是本窗口菜单栏的 id（可能是系统菜单或别的来源），照默认走。
            return;
        }

        ActivateMenuItem(item);
    }

    /// <summary>菜单项 → 行为：role 走共享解释器，自定义项上报到 <see cref="ContextMenuItemClicked"/>。</summary>
    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 Windows 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }
}
