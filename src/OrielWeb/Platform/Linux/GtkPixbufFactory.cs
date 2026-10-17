using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// 把托管的 RGBA 像素变成 <c>GdkPixbuf</c>（对标 Tauri 2 的 <c>Icon::from_rgba</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <c>gdk_pixbuf_new_from_data</c> 支持"复制模式"：传 <c>data=0</c> 时它自己分配缓冲并**拷贝**
/// 我们随后写入的内容——这里正是这么用的，因此不需要 pin 托管数组，也就没有 macOS 那套
/// 生命周期管理。代价是多一次拷贝，但图标只有几十 KB，换图也不是每秒几十次。
/// </para>
/// <para>
/// <c>GdkPixbuf</c> 用 3 字节 RGB 或 4 字节 RGBA；这里统一用 4 字节（<c>hasAlpha=1</c>），
/// 与解码器输出的格式一致。
/// </para>
/// </remarks>
internal static unsafe class GtkPixbufFactory
{
    /// <summary>GDK_COLORSPACE_RGB = 0。</summary>
    private const int GdkColorspaceRgb = 0;

    /// <summary>由 RGBA 造 pixbuf；失败返回 0（调用方据此保留原图标并上报）。</summary>
    /// <remarks>返回的 pixbuf 归调用方所有，必须 <c>g_object_unref</c>。</remarks>
    internal static nint CreatePixbuf(OrielImage image)
    {
        int width = image.Width;
        int height = image.Height;
        int rowStride = width * 4;
        int byteCount = rowStride * height;

        // data=0 → gdk-pixbuf 自己分配并拷贝我们写入的内容（无需 pin 托管数组）
        nint pixbuf = GtkNative.GdkPixbufNewFromData(
            0, GdkColorspaceRgb, 1, 8, width, height, rowStride, 0, 0);

        if (pixbuf == 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] 创建图标失败：gdk_pixbuf_new_from_data 返回 0（{width}x{height}）。");
            return 0;
        }

        // 取它分配的那块缓冲，把 RGBA 写进去。gdk_pixbuf_get_pixels 返回的是内部指针，
        // 对"自己分配"的 pixbuf 而言可写。
        nint pixels = GtkNative.GdkPixbufGetPixels(pixbuf);
        if (pixels == 0)
        {
            GtkNative.GObjectUnref(pixbuf);
            OrielDiagnostics.Report("[OrielWeb] 创建图标失败：gdk_pixbuf_get_pixels 返回 0。");
            return 0;
        }

        ReadOnlySpan<byte> source = image.Rgba.Span;
        var destination = new Span<byte>((void*)pixels, byteCount);
        source.CopyTo(destination);

        return pixbuf;
    }
}
