using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 判断当前进程有没有**图形会话**（Aqua session），以及"没有就别调那些会崩的 API"的守卫。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独有一层</b>：macOS 上有一批 API 在没有窗口服务器的进程里不是"不生效"，
/// 而是**把进程杀掉**——ObjC 异常（<c>NSInternalInconsistencyException</c>）托管侧接不住，
/// 进程以 SIGABRT 结束，调用方连错误码都拿不到。已经实测到两处：
/// </para>
/// <list type="bullet">
///   <item><c>NSMenu</c> 的弹出（<c>NSPopupMenuWindow _availablePositioningRectForMenuType:</c>
///     抛 <c>'Nil screen'</c>）——见 <c>MacOSMenu</c>；</item>
///   <item><c>NSStatusBar</c> 的状态项（托盘）——见 <c>MacOSTrayBackend</c>。</item>
/// </list>
/// <para>
/// 典型场景是通过 SSH 跑无人自检，而机器停在登录窗口（<c>stat -f%Su /dev/console</c> 是
/// <c>root</c>）。Hyper-V 的 macOS VM 默认就是这个状态，自检在那里必须能跑完。
/// </para>
/// <para>
/// 判据是 <c>NSScreen.screens.count</c>：没有会话时它为空。注意**不要**用
/// "进程能不能连上 WindowServer"来判断——那需要真的去连，失败方式正是我们要避开的那种。
/// </para>
/// </remarks>
internal static class MacOSSession
{
    private static readonly Lazy<bool> s_hasScreen = new(
        () =>
        {
            nint screenClass = ObjCRuntime.GetClass("NSScreen");
            if (screenClass == 0)
            {
                // 类都拿不到（不是 AppKit 进程？）——不拦，让调用方去撞真实的错误。
                return true;
            }

            nint screens = ObjCRuntime.SendId(screenClass, ObjCRuntime.Sel("screens"));
            return screens != 0 && ObjCRuntime.SendNint(screens, ObjCRuntime.Sel("count")) > 0;
        },
        isThreadSafe: true);

    /// <summary>当前进程是否有图形会话（没有屏幕时为 <c>false</c>）。</summary>
    internal static bool HasScreen => s_hasScreen.Value;

    /// <summary>
    /// 没有图形会话时抛 <see cref="NotSupportedException"/>（分发器统一翻成 <c>not_supported</c>）。
    /// </summary>
    /// <param name="what">人话描述，例如"菜单无法弹出"。</param>
    internal static void RequireScreen(string what)
    {
        if (HasScreen)
        {
            return;
        }

        throw new NotSupportedException(
            $"当前进程没有图形会话（没有屏幕），{what}：macOS 在这里会直接终止进程，"
            + "所以本库提前拒绝。请在已登录的桌面会话里运行。");
    }
}
