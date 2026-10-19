using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 的显示器后端：GDK3 的 <c>gdk_display_get_monitor*</c> 家族。
/// </summary>
/// <remarks>
/// <para>
/// <b>GDK 给的是应用像素（logical）</b>，而本库对外的显示器坐标一律是物理像素
/// （见 <see cref="OrielMonitor"/>），所以这里统一乘 <c>gdk_monitor_get_scale_factor</c>。
/// 这样三平台的数值口径一致：同一个 200% 缩放的屏幕，无论跑在 Windows 还是 Linux 上，
/// <see cref="OrielMonitor.Size"/> 都是物理分辨率。
/// </para>
/// <para>
/// <b>Wayland 的已知限制（如实报告，不做假换算）</b>：Wayland 没有真正的全局像素坐标系，
/// 合成器给的是逻辑坐标，且不允许客户端枚举"屏幕之外的绝对位置"。因此在 Wayland 下：
/// </para>
/// <list type="bullet">
///   <item><description>多屏的相对排列可能与 X11 不同（尤其是旋转屏与分数缩放）；</description></item>
///   <item><description><c>monitorFromPoint</c> 只对落在某块屏内的点有效，桌面外一律 null；</description></item>
///   <item><description>分数缩放（1.25 / 1.5）下 <c>scale_factor</c> 只能是整数，物理尺寸会有取整误差。</description></item>
/// </list>
/// <para>
/// 这与 Tauri（tao）在 Linux 上的行为同源——两者都用 GDK，所以差异也一样。
/// </para>
/// </remarks>
internal sealed class LinuxMonitorBackend : IMonitorBackend
{
    public IReadOnlyList<OrielMonitor> GetMonitors()
    {
        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0)
        {
            return [];
        }

        int count = GtkNative.GdkDisplayGetNMonitors(display);
        if (count <= 0)
        {
            return [];
        }

        var monitors = new List<OrielMonitor>(count);
        for (int i = 0; i < count; i++)
        {
            OrielMonitor? monitor = Describe(GtkNative.GdkDisplayGetMonitor(display, i));
            if (monitor is not null)
            {
                monitors.Add(monitor);
            }
        }

        return monitors;
    }

    public OrielMonitor? GetPrimaryMonitor()
    {
        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0)
        {
            return null;
        }

        nint primary = GtkNative.GdkDisplayGetPrimaryMonitor(display);
        if (primary != 0)
        {
            return Describe(primary);
        }

        // gdk_display_get_primary_monitor 在没有主屏概念的后端上返回 NULL。退回到
        // "is_primary 为真的那块"，再退到第一块——比直接返回 null 有用，且不会误导
        // （主屏本来就是"用户/系统认为的那块"，第一块是最接近的合理猜测）。
        IReadOnlyList<OrielMonitor> all = GetMonitors();
        foreach (OrielMonitor monitor in all)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return all.Count > 0 ? all[0] : null;
    }

    public OrielMonitor? MonitorFromPoint(double x, double y)
    {
        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0)
        {
            return null;
        }

        // 入参是物理像素，GDK 要应用像素——先按"该点所在屏"的缩放反算。
        // 这里有个先有鸡还是先有蛋：不知道是哪块屏就不知道缩放。GDK 的
        // gdk_display_get_monitor_at_point 收的就是应用像素，所以先用比例 1 试探，
        // 拿到屏后再按它的缩放精算一次。两次调用都很便宜（纯内存查表）。
        nint monitor = GtkNative.GdkDisplayGetMonitorAtPoint(
            display, (int)Math.Round(x), (int)Math.Round(y));

        if (monitor != 0)
        {
            int scale = Math.Max(1, GtkNative.GdkMonitorGetScaleFactor(monitor));
            if (scale != 1)
            {
                nint retry = GtkNative.GdkDisplayGetMonitorAtPoint(
                    display, (int)Math.Round(x / scale), (int)Math.Round(y / scale));
                if (retry != 0)
                {
                    monitor = retry;
                }
            }
        }

        if (monitor == 0)
        {
            return null;
        }

        // GDK 在 X11 上会把屏幕外的点**吸附**到最近的显示器（负坐标按 0 处理），
        // 于是 (-100000,-100000) 也会返回第一块屏。Windows 的 MONITOR_DEFAULTTONULL
        // 与 macOS 的包含判定都不这样——为了让三平台同义，这里再按"点是否真的落在
        // 该显示器的矩形内"复核一次，不在就返回 null。
        //
        // 这一条是 --selftest monitors 在 CI 上抓到的（Linux 红、Windows 绿）：
        // 读代码看不出 GDK 会吸附，只有真机跑一遍才知道。
        OrielMonitor? described = Describe(monitor);
        return described is not null && Contains(described, x, y) ? described : null;
    }

    public OrielMonitor? MonitorFromWindow(nint nativeWindowHandle)
    {
        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0 || nativeWindowHandle == 0)
        {
            return null;
        }

        // nativeWindowHandle 是 GtkWindow*（见 IMonitorBackend.MonitorFromWindow 的说明）。
        // GDK 要的是 GdkWindow*（底层表面）——未 realize 的窗口拿不到它，返回 null。
        nint gdkWindow = GtkNative.GtkWidgetGetWindow(nativeWindowHandle);
        if (gdkWindow == 0)
        {
            return null;
        }

        nint monitor = GtkNative.GdkDisplayGetMonitorAtWindow(display, gdkWindow);
        return monitor == 0 ? null : Describe(monitor);
    }

    public OrielPosition GetCursorPosition()
    {
        nint display = GtkNative.GdkDisplayGetDefault();
        if (display == 0)
        {
            return new OrielPosition(0, 0);
        }

        nint seat = GtkNative.GdkDisplayGetDefaultSeat(display);
        nint pointer = seat == 0 ? 0 : GtkNative.GdkSeatGetPointer(seat);
        if (pointer == 0)
        {
            return new OrielPosition(0, 0);
        }

        GtkNative.GdkDeviceGetPosition(pointer, out _, out int x, out int y);
        if (x < 0 || y < 0)
        {
            // GTK 的约定：拿不到位置时给 -1。原样返回会让调用方以为光标在屏幕外的左上角，
            // 按 (0,0) 报告更接近"没有信息"且不至于画出越界的 UI。
            return new OrielPosition(0, 0);
        }

        // 应用像素 → 物理像素：用该点所在屏的缩放，而不是"主屏缩放"——多屏不同缩放时
        // 后者会把副屏上的光标算错。MonitorFromPoint 自己也做同样的换算，所以两者一致。
        double scale = MonitorFromPoint(x, y)?.ScaleFactor ?? 1.0;
        return new OrielPosition(Math.Round(x * scale), Math.Round(y * scale));
    }

    /// <summary>显示器几何（应用像素）是否包含该点（物理像素）。</summary>
    private static bool Contains(OrielMonitor monitor, double x, double y)
        => x >= monitor.Position.X && x < monitor.Position.X + monitor.Size.Width &&
           y >= monitor.Position.Y && y < monitor.Position.Y + monitor.Size.Height;

    /// <summary>把一个 <c>GdkMonitor*</c> 读成 <see cref="OrielMonitor"/>。</summary>
    private static OrielMonitor? Describe(nint monitor)
    {
        if (monitor == 0)
        {
            return null;
        }

        GtkNative.GdkMonitorGetGeometry(monitor, out GdkRectangle rect);
        int scale = Math.Max(1, GtkNative.GdkMonitorGetScaleFactor(monitor));

        string? name = null;
        nint model = GtkNative.GdkMonitorGetModel(monitor);
        if (model != 0)
        {
            name = Marshal.PtrToStringUTF8(model);
            if (string.IsNullOrEmpty(name))
            {
                name = null;
            }
        }

        return new OrielMonitor(
            Name: name,
            // GDK 的几何是应用像素，乘缩放才是物理像素（见类注释）。
            Position: new OrielPosition(rect.X * scale, rect.Y * scale),
            Size: new OrielSize(rect.Width * scale, rect.Height * scale),
            ScaleFactor: scale)
        {
            IsPrimary = GtkNative.GdkMonitorIsPrimary(monitor) != 0,
        };
    }
}
