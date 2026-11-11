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
/// <b>像素数据的所有权</b>：<c>CGDataProviderCreateWithData</c> 拿到裸指针后**既不复制、也不接管**
/// 内存，只在 provider 释放时回调我们给的 release 函数。所以托管数组用 <c>GCHandle</c> 钉住，
/// 解钉动作就挂在那个回调上（<see cref="ReleasePixelData"/>）——pin 的存活期等于 provider 的存活期，
/// 而 provider 由 CGImage/NSImage 的引用计数保活。整条链是：托管数组 → provider → CGImage → NSImage。
/// </para>
/// <para>
/// <b>2026-11-11 评审 H4 的整改</b>：旧实现把 pin 记进 <c>Dictionary&lt;CGImage, GCHandle&gt;</c>，
/// 并由 <see cref="CreateNSImage"/> 的 finally 在构造完 NSImage 后立刻解除——那等于假设
/// "<c>initWithCGImage:</c> 会当场把像素复制进位图"。这个假设没有依据：NSImage 可以延迟解码，
/// 届时 provider 仍持有那个指针，而 pin 已经没了，数组一旦被 GC 移动/回收，图标内容就是垃圾
/// （严重时是访问违例）。现在 pin 归 provider，调用方不再解钉。
/// </para>
/// <para>
/// <b>验证状态（如实记录）</b>：本仓库在 Windows 上开发，**没有 macOS 真机**，只能在编译期与
/// 结构层核对"release 回调接上了、签名对得上"。"NSImage 是否即时拷贝、延迟解码是否真会发生"
/// 必须在真机上复核（建图标 → <c>GC.Collect</c> + 压实 → 显示并截图比对；重复创建/销毁看
/// provider 回调计数）。因此本次改动按<b>防御性修复</b>对待：即使 NSImage 确实即时拷贝，
/// "provider 释放即解钉"也无害，代价只是 pin 比旧实现多活到 NSImage 释放。
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
            // 只放掉本函数自己那一个 CGImage 引用，**不解钉**：像素的 pin 归 provider，
            // 由 CGImage（以及 NSImage 持有的那份引用）的引用计数决定何时解（见 ReleasePixelData）。
            ObjCRuntime.CGImageRelease(cgImage);
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

        // 钉住托管数组：CGDataProvider 不复制数据；解钉交给 provider 的 release 回调。
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
            // context 传 GCHandle 的整数形式：provider 释放时 CoreGraphics 会回调
            // ReleasePixelData，在那里把 pin 解开（data 与它指向同一块内存，成对传递）。
            provider = ObjCRuntime.CGDataProviderCreateWithData(
                GCHandle.ToIntPtr(handle),
                data,
                (nuint)byteCount,
                &ReleasePixelData);
            if (provider == 0)
            {
                // provider 没建成 = 回调没注册，pin 还归本函数
                handle.Free();
                OrielDiagnostics.Report("[OrielWeb] 创建图标失败：CGDataProviderCreateWithData 返回 0。");
                return 0;
            }

            // 从这行起 pin 的所有权归 provider：下面任何失败路径都**不再**自己 Free，
            // 交给 finally 里的 CGDataProviderRelease 触发回调去解（自己再 Free 就是重复释放）。
            nint cgImage = ObjCRuntime.CGImageCreate(
                (nuint)width, (nuint)height,
                8, 32, (nuint)(width * 4),
                colorSpace, bitmapInfo, provider, 0, false, 0);

            if (cgImage == 0)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] 创建图标失败：CGImageCreate 返回 0（{width}x{height}）。");
                return 0;
            }

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
    /// <c>CGDataProvider</c> 的 release 回调：把 context 里的 <see cref="GCHandle"/> 解钉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 签名必须精确对应 <c>CGDataProviderReleaseDataCallback</c>：
    /// <c>void (*)(void *info, const void *data, size_t size)</c>。它是 CoreGraphics 直接调用的
    /// 函数指针，所以只能是 <c>[UnmanagedCallersOnly]</c>——包成托管委托/闭包会把 GC 与
    /// 线程封送一起拖进来（Native AOT 下也不允许）。
    /// </para>
    /// <para>
    /// 回调可能在**任意线程**上被调（释放 NSImage 的那个线程），所以这里只做一件事；
    /// <c>GCHandle</c> 的解钉本身是线程安全的。
    /// </para>
    /// <para>
    /// <b>这里不调 <c>ObjCRuntime</c>、也不碰 <c>CGImageRelease</c></b>：pin 是 provider 的附属资源，
    /// provider 释放时自有 CoreGraphics 去放 CGImage 一侧；在回调里再走 ObjC/CG 会引入
    /// "释放期间重入 CoreGraphics"的可疑路径，而本函数对正确性只需要解 pin。
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void ReleasePixelData(nint info, nint data, nuint size)
    {
        // data/size 是 CoreGraphics 回传的原始参数，本库不需要（pin 已经从 info 拿到），
        // 但签名一个都不能少——少一个就是原生侧的栈错乱。
        _ = data;
        _ = size;
        ReleasePinnedPixels(info);
    }

    /// <summary>
    /// 解钉（release 回调的实际实现）。返回是否真的解掉了一个 pin。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 拆成独立方法是为了让"所有权契约"能在非 macOS 环境单测：给 <c>GCHandle.ToIntPtr</c> 的
    /// 值解钉、给 0 什么都不做。真机上"provider 何时回调"这一环仍需真机验证。
    /// </para>
    /// <para>
    /// <b>只应被调用一次</b>（原生回调本来就只来一次）。重复解钉不承诺幂等：
    /// <c>GCHandle</c> 的槽位可能已被别人复用，重复 Free 会误伤无关对象，所以这里不做"已释放"登记。
    /// </para>
    /// <para>
    /// <b>绝不抛</b>：调用它的是一条从原生帧进来的回调，异常逃出去在 .NET 上是 fail-fast
    /// （进程直接消失，崩溃栈里还看不到"图标"字样）。
    /// </para>
    /// </remarks>
    internal static bool ReleasePinnedPixels(nint info)
    {
        if (info == 0)
        {
            return false;
        }

        try
        {
            GCHandle.FromIntPtr(info).Free();
            return true;
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 图标像素的 pin 释放失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
