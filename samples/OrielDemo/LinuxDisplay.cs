using System.Runtime.InteropServices;

namespace OrielDemo;

/// <summary>
/// 自检用的环境探针：当前会话的 GDK 后端是不是 Wayland。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自检需要它</b>：Wayland 下有几件事**由合成器决定**，程序化做不到——
/// 改窗口尺寸、移动窗口、读剪贴板（要焦点）。那几条断言验的是环境而不是库，
/// 应当 SKIP 而不是 FAIL；但"该跳过"必须建立在**问对了后端**之上。
/// </para>
/// <para>
/// <b>为什么不看 <c>WAYLAND_DISPLAY</c> 环境变量</b>：GDK 在 Wayland 会话里
/// 即使只设了 <c>DISPLAY=:0</c> 也会连 <c>wayland-0</c>（那是 Wayland 客户端的默认
/// socket 名，变量可以完全不设）。2026-10-22 在 Hyper-V 的 Ubuntu 22.04 真机上，
/// 自检就是因为"看环境变量猜后端"把 Wayland 当成 X11 而红的。
/// </para>
/// <para>
/// 这里直接问 GDK 自己（<c>gdk_display_get_name</c>），与库内部判定用的是同一个事实来源。
/// 只在 <c>gtk_init</c> 之后调用有效——自检跑在应用启动之后，满足这个前提。
/// </para>
/// </remarks>
internal static class LinuxDisplay
{
    private static readonly Lazy<bool> s_isWayland = new(Detect, isThreadSafe: true);

    /// <summary>当前 GDK 后端是否 Wayland（非 Linux 恒 <c>false</c>）。</summary>
    internal static bool IsWayland => s_isWayland.Value;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetDefaultDisplay();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetDisplayName(nint display);

    private static bool Detect()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            nint gdk = NativeLibrary.Load("libgdk-3.so.0");
            var getDefault = Marshal.GetDelegateForFunctionPointer<GetDefaultDisplay>(
                NativeLibrary.GetExport(gdk, "gdk_display_get_default"));
            var getName = Marshal.GetDelegateForFunctionPointer<GetDisplayName>(
                NativeLibrary.GetExport(gdk, "gdk_display_get_name"));

            nint display = getDefault();
            if (display == 0)
            {
                return false;
            }

            string? name = Marshal.PtrToStringUTF8(getName(display));
            return name?.StartsWith("wayland", StringComparison.Ordinal) == true;
        }
        catch (Exception)
        {
            // 拿不到 GDK（不是 GTK 应用、库名不同…）就按 X11 处理：
            // 那是更严格的一侧（不跳过任何断言），失败信息会指向真正的问题。
            return false;
        }
    }
}
