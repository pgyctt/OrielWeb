using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 把托管的 RGBA 像素变成 Win32 的 <c>HICON</c>（对标 Tauri 2 的 <c>Icon::from_rgba</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 走 <c>CreateDIBSection</c>（32bpp 自顶向下）+ <c>CreateBitmap</c>（掩码）+ <c>CreateIconIndirect</c>。
/// 这条路不需要任何额外依赖——GDI 与 user32 本来就是 Windows 自带的。
/// </para>
/// <para>
/// <b>自顶向下</b>：<c>BITMAPINFOHEADER.biHeight</c> 写成负值，DIB 的行序就与托管 RGBA 一致；
/// 写成正值会让图标上下颠倒。
/// </para>
/// <para>
/// <b>掩码位图仍必须给</b>：32bpp 图标的透明度来自颜色位图的 alpha 通道，掩码在 XP 之后被忽略，
/// 但 <c>CreateIconIndirect</c> 对空掩码会失败。这里给一张全 0（= 全不透明）的掩码。
/// </para>
/// <para>
/// 所有中间句柄（DC、两个位图）在成功或失败路径上都要释放，否则每换一次图标泄漏三个 GDI 对象——
/// 托盘"按状态换图"是高频操作，GDI 句柄耗尽的表现是整个进程画不出东西。
/// </para>
/// </remarks>
internal static class Win32IconFactory
{
    /// <summary>
    /// 由 RGBA 造 HICON。失败返回 0（调用方据此保留原图标并上报）。
    /// </summary>
    /// <remarks>
    /// 返回的 HICON 归调用方所有，必须用 <c>DestroyIcon</c> 释放。
    /// </remarks>
    internal static nint CreateFromRgba(OrielImage image)
    {
        int width = image.Width;
        int height = image.Height;

        nint screenDc = Win32.GetDC(0);
        nint memoryDc = 0;
        nint colorBitmap = 0;
        nint maskBitmap = 0;
        nint colorBits = 0;

        try
        {
            memoryDc = Win32.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                OrielDiagnostics.Report("[OrielWeb] 创建图标失败：CreateCompatibleDC 返回 0。");
                return 0;
            }

            var header = BitmapInfoHeader.Create32BppTopDown(width, height);
            colorBitmap = Win32.CreateDIBSection(memoryDc, ref header, 0, out colorBits, 0, 0);
            if (colorBitmap == 0 || colorBits == 0)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] 创建图标失败：CreateDIBSection 返回 0（{width}x{height}）。");
                return 0;
            }

            // RGBA → BGRA：GDI 的 32bpp DIB 是蓝绿红 alpha 的字节序，与托管 RGBA 的
            // 红绿蓝 alpha 正好 R/B 互换。这一步写错的表现是"红蓝颠倒"，在单色图标上看不出来。
            ReadOnlySpan<byte> source = image.Rgba.Span;
            unsafe
            {
                var destination = new Span<byte>((void*)colorBits, width * height * 4);
                for (int i = 0; i < source.Length; i += 4)
                {
                    destination[i] = source[i + 2];     // B ← R
                    destination[i + 1] = source[i + 1]; // G
                    destination[i + 2] = source[i];     // R ← B
                    destination[i + 3] = source[i + 3]; // A
                }
            }

            // 掩码：单色位图，1 位每像素。全 0 表示"没有像素被掩掉"，透明度交给 alpha 通道。
            // 每行按 16 位（WORD）对齐——这是 CreateBitmap 对 1bpp 的要求。
            //
            // 位数据必须由我们提供（CreateBitmap 的 lpvBits）：不给的话内容是未定义的，
            // 未初始化的掩码位会把图标挖掉几块。这里传一块显式清零的缓冲。
            int maskStride = ((width + 15) / 16) * 2;
            byte[] maskBits = new byte[maskStride * height];
            unsafe
            {
                fixed (byte* bits = maskBits)
                {
                    maskBitmap = Win32.CreateBitmap(width, height, 1, 1, (nint)bits);
                }
            }

            if (maskBitmap == 0)
            {
                OrielDiagnostics.Report("[OrielWeb] 创建图标失败：CreateBitmap（掩码）返回 0。");
                return 0;
            }

            var info = new IconInfo
            {
                fIcon = 1,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };

            nint icon = Win32.CreateIconIndirect(ref info);
            if (icon == 0)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] 创建图标失败：CreateIconIndirect 返回 0（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            return icon;
        }
        finally
        {
            if (maskBitmap != 0)
            {
                _ = Win32.DeleteObject(maskBitmap);
            }
            if (colorBitmap != 0)
            {
                _ = Win32.DeleteObject(colorBitmap);
            }
            if (memoryDc != 0)
            {
                _ = Win32.DeleteDC(memoryDc);
            }
            if (screenDc != 0)
            {
                _ = Win32.ReleaseDC(0, screenDc);
            }
        }
    }
}
