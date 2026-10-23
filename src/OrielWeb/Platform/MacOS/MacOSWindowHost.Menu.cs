using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>菜单栏与菜单命令分发。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    // ------------------------------------------------------------------
    // 菜单栏
    // ------------------------------------------------------------------

    /// <summary>
    /// 应用菜单（<c>[NSApp setMainMenu:]</c> 装的那一份）。
    /// </summary>
    /// <remarks>
    /// <b>静态</b>而不是挂在窗口宿主上：macOS 的菜单栏是**应用级**的，一个进程只有一份
    /// （一个 NSApp）。挂实例上的话，第二个窗口装应用菜单会把第一个窗口那份 Dispose 掉，
    /// 而它们本来装的是同一件事——表现是"第一个窗口的菜单还能看见，但点了没反应"。
    /// </remarks>
    private static MacOSMenu? s_appMenuBar;

    /// <summary>
    /// macOS **不支持**"某个窗口的菜单栏"。
    /// </summary>
    /// <exception cref="NotSupportedException">总是抛出。</exception>
    /// <remarks>
    /// 与 Tauri 的行为一致（它的文档明说 <c>setAsWindowMenu</c> 在 macOS 上 "Unsupported"）。
    /// 这不是本库的缺口——是 AppKit 的模型里没有这个概念，菜单只能是应用级的。
    /// </remarks>
    public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        throw new NotSupportedException(
            "macOS 没有「某个窗口的菜单栏」：菜单在 macOS 是应用级的，请改用 setAsAppMenu。");
    }

    /// <summary>
    /// 设为应用菜单（<c>[NSApp setMainMenu:]</c>）。
    /// </summary>
    /// <param name="items">应用菜单的顶层项；空集合表示移除。</param>
    /// <remarks>
    /// <para>
    /// 顶层项在 macOS 的主菜单里是**子菜单**（第一个是应用菜单，其余是"文件/编辑/窗口/帮助"）。
    /// <see cref="MacOSMenu.Build"/> 建出来的结构正好是这个形状：带子项的顶层项会成为
    /// <c>NSMenuItem</c> + <c>NSMenu</c> 子菜单。
    /// </para>
    /// <para>
    /// 反复设置是幂等的：同一份内容会重建一次原生菜单（本库没有资源表，每次都是整棵发），
    /// 旧的那棵 Dispose 掉。<b>注意这会让旧菜单的点击回调失效</b>——所以自定义项的 action
    /// 要挂在**当前**那份菜单上，别把回调存起来跨设置复用。
    /// </para>
    /// </remarks>
    public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        nint app = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        if (app == 0)
        {
            OrielDiagnostics.Report("[OrielWeb] 拿不到 NSApplication.sharedApplication，应用菜单未设置。");
            return;
        }

        MacOSMenu? previous = s_appMenuBar;
        s_appMenuBar = null;

        if (items.Count == 0)
        {
            ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), 0);
            previous?.Dispose();
            return;
        }

        MacOSMenu? bar = MacOSMenu.Build(items, ActivateMenuItem);
        if (bar is null)
        {
            ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), 0);
            previous?.Dispose();
            return;
        }

        s_appMenuBar = bar;
        ObjCRuntime.SendVoidObj(app, ObjCRuntime.Sel("setMainMenu:"), bar.Handle);

        // 新的装上去了才放旧的——反过来会有一瞬间没有主菜单。
        previous?.Dispose();
    }

    private void ActivateMenuItem(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            if (!OrielMenuRoles.TryActivate(role, _app, _window, TryExecuteNativeEditing))
            {
                OrielDiagnostics.Report($"[OrielWeb] 菜单 role「{role}」在 macOS 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            ContextMenuItemClicked?.Invoke(id);
        }
    }
}
