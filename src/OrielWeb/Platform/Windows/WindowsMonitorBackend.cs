using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 的显示器后端：<c>EnumDisplayMonitors</c> + <c>GetMonitorInfo</c> +
/// <c>GetDpiForMonitor</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标天然就是物理像素</b>：进程在启动时声明了 <c>PER_MONITOR_AWARE_V2</c>
/// （见 <c>WindowsPlatformBackend.EnsureDpiAwareness</c>），所以 <c>rcMonitor</c>
/// 给的是真实像素，不需要按缩放比换算。
/// </para>
/// <para>
/// 与 Tauri 的差别只有一处：Tauri 的 Windows 实现也走同一组 API，所以两者的数值应当逐像素一致。
/// </para>
/// </remarks>
internal sealed unsafe class WindowsMonitorBackend : IMonitorBackend
{
    /// <summary>显示器枚举上限。超过 32 块的机器上会截断——这是"够用"而不是"正确"的阈值，见实现说明。</summary>
    private const int MaxMonitors = 32;

    private static readonly delegate* unmanaged<nint, nint, nint, nint, int> s_enumProc = &EnumProc;

    public IReadOnlyList<OrielMonitor> GetMonitors()
    {
        var handles = new List<nint>(MaxMonitors);

        // GCHandle 而不是 ref 参数：回调是原生调用，托管的局部变量地址不能跨调用传递。
        GCHandle pin = GCHandle.Alloc(handles);
        try
        {
            // 失败（返回 FALSE）时 handles 是空的，下面按空列表返回——枚举失败不该让
            // "问一句有几块屏"抛异常。
            _ = Win32.EnumDisplayMonitors(0, 0, s_enumProc, GCHandle.ToIntPtr(pin));
        }
        finally
        {
            pin.Free();
        }

        var monitors = new List<OrielMonitor>(handles.Count);
        foreach (nint handle in handles)
        {
            if (TryDescribe(handle, out OrielMonitor? monitor))
            {
                monitors.Add(monitor);
            }
        }

        return monitors;
    }

    public OrielMonitor? GetPrimaryMonitor()
    {
        foreach (OrielMonitor monitor in GetMonitors())
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return null;
    }

    public OrielMonitor? MonitorFromPoint(double x, double y)
    {
        var point = new POINT((int)Math.Round(x), (int)Math.Round(y));

        // MONITOR_DEFAULTTONULL：点落在所有屏幕之外时返回 NULL。**不能用** TONEAREST——
        // 那会把桌面外的一个点吸附到最近的屏幕，于是 availableMonitors 之外的坐标
        // 也能拿到一台"包含它"的显示器，与 Tauri 的语义不符。
        nint handle = Win32.MonitorFromPoint(point, Win32Constants.MONITOR_DEFAULTTONULL);
        return handle != 0 && TryDescribe(handle, out OrielMonitor? monitor) ? monitor : null;
    }

    public OrielMonitor? MonitorFromWindow(nint nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
        {
            return null;
        }

        // DEFAULTTONEAREST 是"窗口跨屏时取相交最大的那块"的语义，与 Tauri / 系统一致。
        nint handle = Win32.MonitorFromWindow(nativeWindowHandle, (uint)Win32Constants.MONITOR_DEFAULTTONEAREST);
        return handle != 0 && TryDescribe(handle, out OrielMonitor? monitor) ? monitor : null;
    }

    public OrielPosition GetCursorPosition()
    {
        if (!Win32.GetCursorPos(out POINT point))
        {
            return new OrielPosition(0, 0);
        }

        return new OrielPosition(point.X, point.Y);
    }

    /// <summary>枚举回调。恒返回 1（TRUE）——返回 0 会让 <c>EnumDisplayMonitors</c> 提前停止。</summary>
    [UnmanagedCallersOnly]
    private static int EnumProc(nint hMonitor, nint hdc, nint clip, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> handles)
        {
            handles.Add(hMonitor);
        }

        return 1;
    }

    /// <summary>把一个 <c>HMONITOR</c> 读成 <see cref="OrielMonitor"/>；读失败返回 false。</summary>
    private static bool TryDescribe(nint hMonitor, [NotNullWhen(true)] out OrielMonitor? monitor)
    {
        monitor = null;

        var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        if (!Win32.GetMonitorInfoEx(hMonitor, ref info))
        {
            return false;
        }

        // 设备名是固定 32 字符的 UTF-16，可能没有 NUL 结尾（恰好 31 字符时）——
        // PtrToStringUni 会在遇到 NUL 或给定长度处停下，这里按整段读再裁掉尾部 NUL。
        string name = new string(info.szDevice, 0, 32).TrimEnd('\0');
        if (name.Length == 0)
        {
            name = string.Empty;
        }

        monitor = new OrielMonitor(
            Name: name.Length == 0 ? null : name,
            Position: new OrielPosition(info.rcMonitor.Left, info.rcMonitor.Top),
            Size: new OrielSize(
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top),
            ScaleFactor: ReadScaleFactor(hMonitor))
        {
            IsPrimary = (info.dwFlags & Win32Constants.MONITORINFOF_PRIMARY) != 0,
        };

        return true;
    }

    /// <summary>读显示器缩放比；拿不到时按 1.0（不可缩放）处理。</summary>
    /// <remarks>
    /// 失败不抛：<c>GetDpiForMonitor</c> 在 <c>shcore.dll</c> 缺失（Win7）或进程 DPI 感知声明
    /// 被外部改写时返回失败。那种情况下"缩放比 1.0"是与肉眼所见一致的退化值，
    /// 而抛异常会让整条显示器查询在旧机器上不可用。
    /// </remarks>
    private static double ReadScaleFactor(nint hMonitor)
    {
        try
        {
            if (Win32.GetDpiForMonitor(hMonitor, Win32Constants.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // Win7 没有 shcore：按无缩放处理。
        }
        catch (EntryPointNotFoundException)
        {
            // 同上，防御性写法。
        }

        return 1.0;
    }
}
