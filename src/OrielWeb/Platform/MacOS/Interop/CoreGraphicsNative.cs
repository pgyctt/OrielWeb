using System.Runtime.InteropServices;

namespace OrielWeb.Platform.MacOS.Interop;

/// <summary>
/// CoreGraphics 里与**光标**相关的那几个入口（macOS 专用）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么光标位置与抓取要走 CoreGraphics 而不是 AppKit：AppKit 没有"把光标移到某点"
/// 的公开入口，<c>NSCursor</c> 只能改形状；而 <c>CGWarpMouseCursorPosition</c> 与
/// <c>CGAssociateMouseAndMouseCursorPosition</c> 正是 Tauri（tao）在 macOS 上用的那两个。
/// </para>
/// <para>
/// 坐标系：CG 的全局坐标原点在**主显示器左上角**，单位是点（point，逻辑像素）——
/// 与本库对页面暴露的逻辑坐标同一口径，所以不需要乘缩放比。<c>NSScreen</c> 的坐标
/// 也在同一个空间里，所以"窗口位置 + 客户区内偏移"可以直接相加。
/// </para>
/// <para>
/// 库路径写成绝对路径：这两个符号不在 <c>libSystem</c> 的默认查找面里，
/// 而 <c>dlopen</c> 对 framework 的绝对路径是认的（与 <c>MacOSMonitorBackend</c>
/// 里的 CoreGraphics 声明同一个写法）。
/// </para>
/// </remarks>
internal static partial class CoreGraphicsNative
{
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>
    /// 建立/断开"鼠标移动 → 光标移动"的关联。
    /// </summary>
    /// <param name="connected">0 = 断开（光标不再跟随鼠标），非 0 = 恢复。</param>
    /// <returns>CGError（0 = 成功）。</returns>
    /// <remarks>
    /// <b>这是进程级状态，不是窗口级</b>：调用后整个应用的光标都不再跟随鼠标。
    /// 所以窗口销毁时必须恢复——否则用户会觉得"鼠标坏了"，而应用已经没了。
    /// </remarks>
    [LibraryImport(CoreGraphicsLib)]
    internal static partial int CGAssociateMouseAndMouseCursorPosition(int connected);

    /// <summary>把光标瞬移到全局坐标的某一点（单位：点）。</summary>
    /// <returns>CGError（0 = 成功）。</returns>
    /// <remarks>
    /// 与 <c>CGWarpMouseCursorPosition</c> 配套的坑：它会**顺带恢复**光标与鼠标的关联
    /// （看起来像"warp 把 grab 弄丢了"）。Tauri 那边也是这个行为，不做额外处理。
    /// </remarks>
    [LibraryImport(CoreGraphicsLib)]
    internal static partial int CGWarpMouseCursorPosition(CGPoint point);
}

/// <summary>CoreGraphics 的点（<c>CGFloat</c> 在 64 位上是 <c>double</c>）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;

    public double Y;

    public CGPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}
