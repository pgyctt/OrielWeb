using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 把托管的 RGBA 像素变成 <c>NSImage</c>（对标 Tauri 2 的 <c>Icon::from_rgba</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 路径：RGBA → <c>CGImageCreate</c> → <c>NSImage initWithCGImage:size:</c>。
/// CoreGraphics 与 AppKit 都是系统自带，不引入任何额外依赖。
/// </para>
/// <para>
/// <b>像素格式必须按 CoreGraphics 的约定重排</b>：<c>kCGImageAlphaPremultipliedFirst</c>
/// 配上 32 位小端字节序，表示内存里是"BGRA、alpha 在前、且颜色已乘过 alpha"。
/// 两处都不能省：
/// <list type="bullet">
///   <item><description>RGBA → BGRA 的通道重排。</description></item>
///   <item><description>颜色预乘 alpha（<c>C = C * A / 255</c>）：跳过它的表现是半透明边缘发亮，
///     在深色菜单栏上尤其明显。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>数据生命周期</b>：<c>CGDataProviderCreateWithData</c> 拿到裸指针后**既不复制、也不接管**内存，
/// 所以托管数组必须在 CGImage 存活期间保持 pin。这里用 <c>GCHandle</c> 钉住，
/// 并在 <see cref="ReleaseCGImage"/> 里解开——否则 GC 一移动数组，图像内容就变成垃圾。
/// </para>
/// <para>
/// <b>NSImage 会拷贝内容</b>（<c>initWithCGImage:</c> 内部完成解码/缓存），因此 CGImage
/// 在构造完 NSImage 之后就可以释放。
/// </para>
/// </remarks>
internal static unsafe class MacOSImageFactory
{
    /// <summary>由 RGBA 造 NSImage；失败返回 0（调用方据此保留原图标并上报）。</summary>
    /// <remarks>返回的 NSImage 归调用方所有（ObjC 对象，用 <c>objc_release</c> 释放）。</remarks>
    internal static nint CreateNSImage(OrielImage image)
    {
        nint cgImage = CreateCGImage(image);
        if (cgImage == 0)
        {
            return 0;
        }

        try
        {
            nint nsImage = ObjCRuntime.SendId(
                ObjCRuntime.GetClassOrThrow("NSImage"), ObjCRuntime.Sel("alloc"));
            nsImage = ObjCRuntime.SendIdObjDouble2(
                nsImage,
                ObjCRuntime.Sel("initWithCGImage:size:"),
                cgImage,
                image.Width,
                image.Height);

            if (nsImage == 0)
            {
                OrielDiagnostics.Report("[OrielWeb] 创建图标失败：NSImage initWithCGImage:size: 返回 nil。");
            }

            return nsImage;
        }
        finally
        {
            ReleaseCGImage(cgImage);
        }
    }

    /// <summary>RGBA → CGImage（BGRA 预乘）。失败返回 0。</summary>
    private static nint CreateCGImage(OrielImage image)
    {
        int width = image.Width;
        int height = image.Height;
        int byteCount = width * height * 4;

        // 转成 CoreGraphics 期望的 BGRA 预乘格式
        byte[] pixels = new byte[byteCount];
        ReadOnlySpan<byte> source = image.Rgba.Span;
        for (int i = 0; i < byteCount; i += 4)
        {
            byte r = source[i];
            byte g = source[i + 1];
            byte b = source[i + 2];
            byte a = source[i + 3];

            pixels[i] = (byte)(b * a / 255);     // B
            pixels[i + 1] = (byte)(g * a / 255); // G
            pixels[i + 2] = (byte)(r * a / 255); // R
            pixels[i + 3] = a;                   // A
        }

        // 钉住托管数组：CGDataProvider 不复制数据
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        nint data = handle.AddrOfPinnedObject();

        // kCGImageAlphaPremultipliedFirst(2) | kCGBitmapByteOrder32Little(0x2000)
        // 组合出来就是"内存里 BGRA、alpha 在前"——与上面写好的像素一致
        const uint kCGImageAlphaPremultipliedFirst = 2;
        const uint kCGBitmapByteOrder32Little = 2 << 12;
        uint bitmapInfo = kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little;

        nint colorSpace = ObjCRuntime.CGColorSpaceCreateDeviceRGB();
        if (colorSpace == 0)
        {
            handle.Free();
            OrielDiagnostics.Report("[OrielWeb] 创建图标失败：CGColorSpaceCreateDeviceRGB 返回 0。");
            return 0;
        }

        nint provider = 0;
        try
        {
            provider = ObjCRuntime.CGDataProviderCreateWithData(0, data, (nuint)byteCount, 0);
            if (provider == 0)
            {
                OrielDiagnostics.Report("[OrielWeb] 创建图标失败：CGDataProviderCreateWithData 返回 0。");
                handle.Free();
                return 0;
            }

            nint cgImage = ObjCRuntime.CGImageCreate(
                (nuint)width, (nuint)height,
                8, 32, (nuint)(width * 4),
                colorSpace, bitmapInfo, provider, 0, false, 0);

            if (cgImage == 0)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] 创建图标失败：CGImageCreate 返回 0（{width}x{height}）。");
                handle.Free();
                return 0;
            }

            RegisterPin(cgImage, handle);
            return cgImage;
        }
        finally
        {
            if (provider != 0)
            {
                ObjCRuntime.CGDataProviderRelease(provider);
            }

            ObjCRuntime.CGColorSpaceRelease(colorSpace);
        }
    }

    /// <summary>
    /// CGImage 句柄 → 被钉住的像素数组句柄。CGImage 释放时解除 pin。
    /// </summary>
    /// <remarks>
    /// 之所以要这张表：<c>CGDataProviderCreateWithData</c> 不复制、也不接管内存，
    /// 而 CGImage 上没有可挂 userdata 的槽。不解除 pin 的后果是像素数组永不回收——
    /// 托盘"按状态换图"是高频操作，长时间运行会持续涨内存。
    /// </remarks>
    private static readonly Dictionary<nint, GCHandle> s_pins = [];

    private static void RegisterPin(nint cgImage, GCHandle handle)
    {
        lock (s_pins)
        {
            s_pins[cgImage] = handle;
        }
    }

    /// <summary>释放 CGImage 并解除对应的 pin。</summary>
    internal static void ReleaseCGImage(nint cgImage)
    {
        if (cgImage == 0)
        {
            return;
        }

        lock (s_pins)
        {
            if (s_pins.Remove(cgImage, out GCHandle handle))
            {
                handle.Free();
            }
        }

        ObjCRuntime.CGImageRelease(cgImage);
    }
}
