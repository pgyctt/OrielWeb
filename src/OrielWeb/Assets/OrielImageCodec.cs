using System.Buffers.Binary;
using System.IO.Compression;

namespace OrielWeb;

/// <summary>
/// PNG 与 ICO 的托管解码器（纯 BCL，不引入任何第三方图像库）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：Tauri 2 把"图标"从路径字符串改成了**已解码的 RGBA 对象**
/// （<c>tauri::image::Image</c>），代价是它依赖 Rust <c>image</c> crate 与
/// <c>image-png</c>/<c>image-ico</c> 两个 Cargo feature。OrielWeb 的定位是
/// **零额外原生依赖**，因此这里自己实现——只做托盘/窗口图标真正需要的那个子集：
/// PNG 的 8 位真彩（含 alpha、含调色板、含灰度）与 ICO 的内嵌 PNG/BMP。
/// </para>
/// <para>
/// 不做的事（且是刻意的）：隔行扫描（Adam7）、16 位每通道、iCCP/gAMA 等色彩管理块、
/// 动画 APNG、ICO 的 1/4 位色深。这些在"应用图标"这个用途上不会出现，
/// 而实现它们要付出的是数倍代码与测试面。遇到不支持的结构**明确报错**，
/// 而不是解出一张花屏图（静默错误比明确失败难查得多）。
/// </para>
/// </remarks>
internal static class OrielImageCodec
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>按文件头识别并解码 PNG / ICO。</summary>
    internal static OrielImage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new OrielImageFormatException("图像数据为空。");
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature))
        {
            return DecodePng(bytes);
        }

        // ICO/CUR：ICONDIR 的保留字段为 0，类型 1 = ICO、2 = CUR。
        if (bytes.Length >= 6
            && BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..4]) is 1 or 2)
        {
            return DecodeIco(bytes);
        }

        throw new OrielImageFormatException(
            "无法识别的图像格式：只支持 PNG 与 ICO（按文件头判定）。");
    }

    // ========================================================================
    // PNG
    // ========================================================================

    private static OrielImage DecodePng(ReadOnlySpan<byte> bytes)
    {
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlace = 0;
        bool sawHeader = false;

        // 调色板（colorType=3）与 tRNS 透明表
        byte[]? palette = null;
        byte[]? paletteAlpha = null;

        using var idat = new MemoryStream();

        int offset = 8; // 跳过签名
        while (offset + 8 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
            if (length < 0 || offset + 12 + length > bytes.Length)
            {
                throw new OrielImageFormatException("PNG 结构损坏：块长度越界。");
            }

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = bytes.Slice(offset + 8, length);
            offset += 12 + length; // 长度(4) + 类型(4) + 数据 + CRC(4)

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13)
                {
                    throw new OrielImageFormatException("PNG 的 IHDR 块长度不足。");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
                sawHeader = true;

                if (width <= 0 || height <= 0)
                {
                    throw new OrielImageFormatException($"PNG 的尺寸非法：{width}x{height}。");
                }

                if (interlace != 0)
                {
                    throw new OrielImageFormatException(
                        "暂不支持隔行扫描（Adam7）的 PNG——请另存为非隔行版本。");
                }

                if (bitDepth != 8)
                {
                    throw new OrielImageFormatException(
                        $"暂不支持 {bitDepth} 位每通道的 PNG（只支持 8 位）。");
                }

                if (colorType is not (0 or 2 or 3 or 4 or 6))
                {
                    throw new OrielImageFormatException($"PNG 的颜色类型 {colorType} 不受支持。");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                paletteAlpha = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (!sawHeader)
        {
            throw new OrielImageFormatException("PNG 缺少 IHDR 块。");
        }

        if (idat.Length == 0)
        {
            throw new OrielImageFormatException("PNG 缺少 IDAT 数据。");
        }

        // 每像素通道数（按颜色类型）
        int channels = colorType switch
        {
            0 => 1, // 灰度
            2 => 3, // 真彩
            3 => 1, // 调色板索引
            4 => 2, // 灰度 + alpha
            6 => 4, // 真彩 + alpha
            _ => throw new OrielImageFormatException($"PNG 的颜色类型 {colorType} 不受支持。"),
        };

        int stride = width * channels;
        int rawLength = checked((stride + 1) * height); // 每行前置一个 filter 字节

        byte[] raw = Inflate(idat.ToArray(), rawLength, "PNG 的 IDAT");
        byte[] pixels = Unfilter(raw, width, height, channels, stride);

        return colorType switch
        {
            0 => GrayscaleToRgba(pixels, width, height, hasAlpha: false),
            4 => GrayscaleToRgba(pixels, width, height, hasAlpha: true),
            2 => TruecolorToRgba(pixels, width, height, hasAlpha: false),
            6 => TruecolorToRgba(pixels, width, height, hasAlpha: true),
            3 => PaletteToRgba(pixels, width, height, palette, paletteAlpha),
            _ => throw new OrielImageFormatException($"PNG 的颜色类型 {colorType} 不受支持。"),
        };
    }

    /// <summary>zlib 解压（PNG 的 IDAT 就是 zlib 流）。</summary>
    private static byte[] Inflate(byte[] compressed, int expectedLength, string what)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedLength);
            zlib.CopyTo(output);

            if (output.Length < expectedLength)
            {
                throw new OrielImageFormatException(
                    $"{what} 解压后数据不足：期望 {expectedLength} 字节，实到 {output.Length} 字节。");
            }

            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new OrielImageFormatException($"{what} 解压失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 逐行还原 PNG 的行过滤器（filter type 0~4）。
    /// </summary>
    /// <remarks>
    /// Paeth 预测器（type 4）是四者里唯一需要三个邻居的；其余三个都是它的退化情形。
    /// 每行都必须解过滤后才能作为下一行的"上一行"参与运算——顺序不能乱。
    /// </remarks>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels, int stride)
    {
        byte[] pixels = new byte[stride * height];
        int bpp = channels; // 8 位每通道时，滤波的"字节每像素"就是通道数

        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int rawRow = y * (stride + 1) + 1;
            int outRow = y * stride;
            int prevRow = outRow - stride;

            for (int x = 0; x < stride; x++)
            {
                byte value = raw[rawRow + x];
                byte left = x >= bpp ? pixels[outRow + x - bpp] : (byte)0;
                byte up = y > 0 ? pixels[prevRow + x] : (byte)0;
                byte upLeft = y > 0 && x >= bpp ? pixels[prevRow + x - bpp] : (byte)0;

                pixels[outRow + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) >> 1)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new OrielImageFormatException($"PNG 第 {y} 行的过滤器类型 {filter} 非法。"),
                };
            }
        }

        return pixels;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        // 平局时的取舍顺序是 PNG 规范定的（a → b → c），不能随手改
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static OrielImage GrayscaleToRgba(byte[] pixels, int width, int height, bool hasAlpha)
    {
        int channels = hasAlpha ? 2 : 1;
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0, o = 0; i < width * height; i++, o += 4)
        {
            byte gray = pixels[i * channels];
            rgba[o] = gray;
            rgba[o + 1] = gray;
            rgba[o + 2] = gray;
            rgba[o + 3] = hasAlpha ? pixels[i * channels + 1] : (byte)255;
        }

        return new OrielImage(rgba, width, height);
    }

    private static OrielImage TruecolorToRgba(byte[] pixels, int width, int height, bool hasAlpha)
    {
        int channels = hasAlpha ? 4 : 3;
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0, o = 0; i < width * height; i++, o += 4)
        {
            rgba[o] = pixels[i * channels];
            rgba[o + 1] = pixels[i * channels + 1];
            rgba[o + 2] = pixels[i * channels + 2];
            rgba[o + 3] = hasAlpha ? pixels[i * channels + 3] : (byte)255;
        }

        return new OrielImage(rgba, width, height);
    }

    private static OrielImage PaletteToRgba(
        byte[] indices, int width, int height, byte[]? palette, byte[]? paletteAlpha)
    {
        if (palette is null || palette.Length < 3)
        {
            throw new OrielImageFormatException("调色板 PNG 缺少 PLTE 块。");
        }

        int colors = palette.Length / 3;
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0, o = 0; i < width * height; i++, o += 4)
        {
            int index = indices[i];
            if (index >= colors)
            {
                throw new OrielImageFormatException($"调色板索引 {index} 超出 PLTE 表范围（{colors} 色）。");
            }

            rgba[o] = palette[index * 3];
            rgba[o + 1] = palette[index * 3 + 1];
            rgba[o + 2] = palette[index * 3 + 2];
            // tRNS 缺省时全不透明；给了就是每个调色板项一个 alpha
            rgba[o + 3] = paletteAlpha is not null && index < paletteAlpha.Length
                ? paletteAlpha[index]
                : (byte)255;
        }

        return new OrielImage(rgba, width, height);
    }

    // ========================================================================
    // ICO
    // ========================================================================

    /// <summary>
    /// 解 ICO：挑**最大**的那张（图标文件常带多尺寸，大的缩放质量更好）。
    /// </summary>
    /// <remarks>
    /// 内嵌 PNG（Vista 起常见）直接走 PNG 解码；老式 BMP（DIB）要自己处理它的
    /// 倒置行序与 AND 掩码。BMP 的"字节每行 4 字节对齐"是最容易写错的一处。
    /// </remarks>
    private static OrielImage DecodeIco(ReadOnlySpan<byte> bytes)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
        if (count <= 0)
        {
            throw new OrielImageFormatException("ICO 目录里没有条目。");
        }

        int bestWidth = -1, bestHeight = -1;
        int bestOffset = 0, bestLength = 0;

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            if (entry + 16 > bytes.Length)
            {
                throw new OrielImageFormatException("ICO 目录条目越界。");
            }

            // 宽高字段是 1 字节：0 表示 256（规范如此，因为 256 放不进一个字节）
            int w = bytes[entry] == 0 ? 256 : bytes[entry];
            int h = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(entry + 8)..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(entry + 12)..]);

            if (offset < 0 || length <= 0 || offset + length > bytes.Length)
            {
                continue; // 单条损坏不该让整份 ICO 不可用
            }

            if (w * h > bestWidth * bestHeight)
            {
                bestWidth = w;
                bestHeight = h;
                bestOffset = offset;
                bestLength = length;
            }
        }

        if (bestLength == 0)
        {
            throw new OrielImageFormatException("ICO 里没有可用的图像条目。");
        }

        ReadOnlySpan<byte> payload = bytes.Slice(bestOffset, bestLength);
        if (payload.Length >= 8 && payload[..8].SequenceEqual(PngSignature))
        {
            return DecodePng(payload);
        }

        return DecodeIcoBitmap(payload, bestWidth, bestHeight);
    }

    /// <summary>解 ICO 内嵌的 DIB（BITMAPINFOHEADER + 像素 + AND 掩码）。</summary>
    private static OrielImage DecodeIcoBitmap(ReadOnlySpan<byte> payload, int fallbackWidth, int fallbackHeight)
    {
        if (payload.Length < 40)
        {
            throw new OrielImageFormatException("ICO 的位图头长度不足。");
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int width = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        // DIB 的高度字段在 ICO 里是"实际高度 x2"（像素 + AND 掩码各占一半）
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]) / 2;
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);

        if (headerSize < 40 || width <= 0 || height <= 0)
        {
            throw new OrielImageFormatException("ICO 的位图头字段非法。");
        }

        if (width != fallbackWidth || height != fallbackHeight)
        {
            // 目录与头不一致时以头为准，但记一笔（这类文件通常是打包工具写歪了）
            OrielDiagnostics.Report(
                $"[OrielWeb] ICO 目录声明 {fallbackWidth}x{fallbackHeight}，位图头声明 {width}x{height}，按头处理。");
        }

        if (compression != 0)
        {
            throw new OrielImageFormatException($"暂不支持压缩的 ICO 位图（compression={compression}）。");
        }

        int pixelOffset = headerSize;
        byte[] rgba = new byte[width * height * 4];

        if (bitCount == 32)
        {
            // BGRA，行自下而上，每行 4 字节对齐
            int stride = ((width * 4) + 3) & ~3;
            for (int y = 0; y < height; y++)
            {
                int srcRow = pixelOffset + (height - 1 - y) * stride;
                if (srcRow + width * 4 > payload.Length)
                {
                    throw new OrielImageFormatException("ICO 的 32 位像素数据越界。");
                }

                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = (y * width + x) * 4;
                    rgba[d] = payload[s + 2];     // R ← B 位序
                    rgba[d + 1] = payload[s + 1]; // G
                    rgba[d + 2] = payload[s];     // B ← R
                    rgba[d + 3] = payload[s + 3];
                }
            }

            // 32 位图标的 alpha 有时全为 0（老工具写的），此时 AND 掩码才是真 alpha。
            // 全透明会让图标彻底消失，所以这里回退到掩码。
            if (!HasAnyAlpha(rgba))
            {
                ApplyAndMask(payload, pixelOffset, width, height, bitCount, rgba);
            }

            return new OrielImage(rgba, width, height);
        }

        if (bitCount is 1 or 4 or 8)
        {
            DecodeIndexedBitmap(payload, pixelOffset, width, height, bitCount, rgba);
            ApplyAndMask(payload, pixelOffset, width, height, bitCount, rgba);
            return new OrielImage(rgba, width, height);
        }

        if (bitCount == 24)
        {
            int stride = ((width * 3) + 3) & ~3;
            for (int y = 0; y < height; y++)
            {
                int srcRow = pixelOffset + (height - 1 - y) * stride;
                if (srcRow + width * 3 > payload.Length)
                {
                    throw new OrielImageFormatException("ICO 的 24 位像素数据越界。");
                }

                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 3;
                    int d = (y * width + x) * 4;
                    rgba[d] = payload[s + 2];
                    rgba[d + 1] = payload[s + 1];
                    rgba[d + 2] = payload[s];
                    rgba[d + 3] = 255;
                }
            }

            ApplyAndMask(payload, pixelOffset, width, height, bitCount, rgba);
            return new OrielImage(rgba, width, height);
        }

        throw new OrielImageFormatException($"暂不支持 {bitCount} 位色的 ICO 位图。");
    }

    private static bool HasAnyAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解 1/4/8 位索引色：像素后面紧跟调色板（每项 4 字节 BGRA）。</summary>
    private static void DecodeIndexedBitmap(
        ReadOnlySpan<byte> payload, int pixelOffset, int width, int height, int bitCount, byte[] rgba)
    {
        int stride = (((width * bitCount) + 31) / 32) * 4; // 每行按 4 字节对齐
        int paletteOffset = pixelOffset + stride * height;
        int colors = bitCount == 8 ? 256 : 1 << bitCount;

        // 调色板缺了就只能当黑图，但那通常是文件损坏——报错比给一张全黑图好
        if (paletteOffset + colors * 4 > payload.Length)
        {
            throw new OrielImageFormatException("ICO 的调色板数据越界。");
        }

        for (int y = 0; y < height; y++)
        {
            int srcRow = pixelOffset + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int index = bitCount switch
                {
                    8 => payload[srcRow + x],
                    4 => (x % 2 == 0 ? payload[srcRow + x / 2] >> 4 : payload[srcRow + x / 2] & 0x0F),
                    _ => (payload[srcRow + x / 8] >> (7 - (x % 8))) & 0x01,
                };

                int p = paletteOffset + index * 4;
                int d = (y * width + x) * 4;
                rgba[d] = payload[p + 2];
                rgba[d + 1] = payload[p + 1];
                rgba[d + 2] = payload[p];
                rgba[d + 3] = 255; // 索引色的透明由 AND 掩码补，见 ApplyAndMask
            }
        }
    }

    /// <summary>
    /// 用 AND 掩码补 alpha：掩码位为 1 表示**透明**（1 位每像素，行自下而上、4 字节对齐）。
    /// </summary>
    private static void ApplyAndMask(
        ReadOnlySpan<byte> payload, int pixelOffset, int width, int height, int bitCount, byte[] rgba)
    {
        int pixelStride = bitCount switch
        {
            32 => ((width * 4) + 3) & ~3,
            24 => ((width * 3) + 3) & ~3,
            _ => (((width * bitCount) + 31) / 32) * 4,
        };

        int maskOffset = pixelOffset + pixelStride * height;
        int maskStride = ((width + 31) / 32) * 4; // 1bpp，每行 4 字节对齐

        if (maskOffset + maskStride * height > payload.Length)
        {
            return; // 掩码缺失：保留已有 alpha（不当作错误，很多 ICO 就是这样）
        }

        for (int y = 0; y < height; y++)
        {
            int srcRow = maskOffset + (height - 1 - y) * maskStride;
            for (int x = 0; x < width; x++)
            {
                bool transparent = ((payload[srcRow + x / 8] >> (7 - (x % 8))) & 0x01) != 0;
                if (transparent)
                {
                    rgba[(y * width + x) * 4 + 3] = 0;
                }
            }
        }
    }
}
