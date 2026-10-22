using System.Runtime.InteropServices;

namespace OrielDemo;

/// <summary>
/// 观感自检用的窗口截图：把**当前窗口**按合成后的真实颜色存成 PNG。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用虚拟机控制台的缩略图</b>：Hyper-V 的
/// <c>GetVirtualSystemThumbnailImage</c> 返回 RGB565，实测在这台环境里解出来的
/// <b>R 通道恒为 0</b>、纯白与纯黑读出来几乎一样——颜色信息整体不可信。
/// 用它判断"材质是什么颜色/有没有模糊"是没有意义的。
/// </para>
/// <para>
/// <b>为什么不用 <c>screencapture</c></b>：它截的是整屏，需要"屏幕录制"权限；
/// 没有授权时只得到菜单栏 + 一片灰。
/// </para>
/// <para>
/// <b>这条路径为什么可行</b>：<c>CGWindowListCreateImage</c> 截**自己进程**的窗口
/// 不需要屏幕录制权限，而且它取的是窗口服务器合成之后的画面——<c>NSVisualEffectView</c>
/// 的模糊与染色都在合成阶段完成，所以材质**能被拍进去**。
/// （<c>NSView.cacheDisplayInRect:</c> 拿不到，它走的是 drawRect 路径。）
/// </para>
/// <para>
/// <c>CGWindowListCreateImage</c> 在 macOS 14 起标记为废弃，但仍可用；
/// 返回 0 表示截不到（权限或窗口号失效），调用方据此决定是否降级。
/// </para>
/// </remarks>
internal static partial class MacScreenCapture
{
    private const uint ListOptionIncludingWindow = 1 << 3;
    private const uint ImageOptionBoundsIgnoreFraming = 1 << 0;
    private const uint ImageOptionNominalResolution = 1 << 4;
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const nint CfUrlPosixPathStyle = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        internal double X;
        internal double Y;
        internal double Width;
        internal double Height;
    }

    /// <summary><c>CGRectNull</c>：原点为无穷大、尺寸为 0（CoreGraphics 用它表示"整屏"）。</summary>
    private static CGRect NullRect => new()
    {
        X = double.PositiveInfinity,
        Y = double.PositiveInfinity,
        Width = 0,
        Height = 0,
    };

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial nint CGWindowListCreateImage(
        CGRect screenBounds, uint listOption, uint windowID, uint imageOption);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFStringCreateWithCString(nint allocator, nint cStr, uint encoding);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFURLCreateWithFileSystemPath(
        nint allocator, nint filePath, nint pathStyle, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [LibraryImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static partial nint CGImageDestinationCreateWithURL(
        nint url, nint type, nuint count, nint options);

    [LibraryImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);

    [LibraryImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGImageDestinationFinalize(nint destination);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

    /// <summary>
    /// 把 <paramref name="windowNumber"/> 对应的窗口存成 PNG。
    /// </summary>
    /// <param name="windowNumber">来自 <c>[NSWindow windowNumber]</c>。</param>
    /// <param name="path">输出路径（绝对路径）。</param>
    /// <returns>成功与否；失败不抛异常（观感自检不该因为拍不到图就中断）。</returns>
    internal static bool TrySaveWindow(uint windowNumber, string path)
    {
        if (!OperatingSystem.IsMacOS() || windowNumber == 0)
        {
            return false;
        }

        try
        {
            nint image = CGWindowListCreateImage(
                NullRect,
                ListOptionIncludingWindow,
                windowNumber,
                ImageOptionBoundsIgnoreFraming | ImageOptionNominalResolution);
            if (image == 0)
            {
                return false;
            }

            try
            {
                nint type = MakeCFString("public.png");
                nint url = MakeFileUrl(path);
                try
                {
                    if (url == 0)
                    {
                        return false;
                    }

                    nint destination = CGImageDestinationCreateWithURL(url, type, 1, 0);
                    if (destination == 0)
                    {
                        return false;
                    }

                    try
                    {
                        CGImageDestinationAddImage(destination, image, 0);
                        return CGImageDestinationFinalize(destination);
                    }
                    finally
                    {
                        CFRelease(destination);
                    }
                }
                finally
                {
                    if (url != 0)
                    {
                        CFRelease(url);
                    }

                    CFRelease(type);
                }
            }
            finally
            {
                CFRelease(image);
            }
        }
        catch (Exception)
        {
            // 截图是取证手段，不是被测对象：拍不到就让调用方降级，不要盖住真正的断言结果
            return false;
        }
    }

    private static nint MakeCFString(string value)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return CFStringCreateWithCString(0, utf8, CFStringEncodingUtf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static nint MakeFileUrl(string path)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            nint cfPath = CFStringCreateWithCString(0, utf8, CFStringEncodingUtf8);
            if (cfPath == 0)
            {
                return 0;
            }

            nint url = CFURLCreateWithFileSystemPath(0, cfPath, CfUrlPosixPathStyle, false);
            CFRelease(cfPath);
            return url;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
