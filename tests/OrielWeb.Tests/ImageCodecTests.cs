using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 托管图像解码器（PNG / ICO → RGBA）。对标 Tauri 2 的 <c>Image</c>：三平台图标统一吃 RGBA，
/// 于是"某平台加载器认不认这个格式"从运行期静默失败变成解码期明确失败。
/// </summary>
/// <remarks>
/// 这里**自己造 PNG 字节**（而不是依赖仓库里的某个样例文件）：解码器的正确性必须能用
/// 逐像素断言钉住，而"文件头 + IHDR + IDAT + IEND"这套结构本身不复杂，自己拼反而能把
/// 每种颜色类型、每个行过滤器都覆盖到。另有一个用例解**真实文件**（demo 的 app.png/app.ico），
/// 确认它对真实编码器的输出也成立。
/// </remarks>
public sealed class ImageCodecTests
{
    /// <summary>按给定原始像素（未过滤）造一张 PNG。</summary>
    private static byte[] BuildPng(int width, int height, byte colorType, byte[] rawRows)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;          // bit depth
        ihdr[9] = colorType;
        ihdr[10] = 0;         // compression
        ihdr[11] = 0;         // filter
        ihdr[12] = 0;         // interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        // IDAT：zlib 压缩后的 "每行前置 filter 字节 + 原始行数据"
        using var uncompressed = new MemoryStream();
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 4 };
        int stride = width * channels;
        for (int y = 0; y < height; y++)
        {
            uncompressed.WriteByte(0); // filter type 0（None）
            uncompressed.Write(rawRows, y * stride, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            uncompressed.Position = 0;
            uncompressed.CopyTo(zlib);
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        // CRC 必须真的算（解码器不校验，但一个 CRC 错的 PNG 不是"合法输入"）
        uint crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = Step(crc, b);
        }
        foreach (byte b in data)
        {
            crc = Step(crc, b);
        }
        return crc ^ 0xFFFFFFFF;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }
    }

    [Fact]
    public void Png_TruecolorWithAlpha_DecodesExactly()
    {
        // 2x1：纯红不透明 + 纯蓝半透明
        byte[] raw =
        [
            255, 0, 0, 255,
            0, 0, 255, 128,
        ];
        OrielImage image = OrielImage.FromBytes(BuildPng(2, 1, 6, raw));

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(raw, image.Rgba.ToArray());
    }

    [Fact]
    public void Png_TruecolorWithoutAlpha_IsOpaque()
    {
        byte[] raw = [10, 20, 30, 40, 50, 60]; // 2x1 RGB
        OrielImage image = OrielImage.FromBytes(BuildPng(2, 1, 2, raw));

        Assert.Equal(
            new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 },
            image.Rgba.ToArray());
    }

    [Fact]
    public void Png_Grayscale_ExpandsToRgb()
    {
        byte[] raw = [77, 200];
        OrielImage image = OrielImage.FromBytes(BuildPng(2, 1, 0, raw));

        Assert.Equal(
            new byte[] { 77, 77, 77, 255, 200, 200, 200, 255 },
            image.Rgba.ToArray());
    }

    [Fact]
    public void Png_GrayscaleWithAlpha_ExpandsToRgb()
    {
        byte[] raw = [77, 128, 200, 255];
        OrielImage image = OrielImage.FromBytes(BuildPng(2, 1, 4, raw));

        Assert.Equal(
            new byte[] { 77, 77, 77, 128, 200, 200, 200, 255 },
            image.Rgba.ToArray());
    }

    [Fact]
    public void Png_MultipleRows_RespectsRowOrder()
    {
        // 2x2：第一行红、第二行绿——行序搞反是这类解码器最典型的错
        byte[] raw =
        [
            255, 0, 0, 255, 255, 0, 0, 255,
            0, 255, 0, 255, 0, 255, 0, 255,
        ];
        OrielImage image = OrielImage.FromBytes(BuildPng(2, 2, 6, raw));

        Assert.Equal(raw, image.Rgba.ToArray());
    }

    /// <summary>
    /// 五种行过滤器（0=None / 1=Sub / 2=Up / 3=Average / 4=Paeth）逐个往返。
    /// </summary>
    /// <remarks>
    /// 这是解码器最容易写错、也最难靠"看一眼图"发现的部分：过滤器错了会解出花屏，
    /// 而花屏在 16x16 的图标上未必一眼可见。这里的做法是**先按规范正向过滤**、
    /// 再让解码器反解，断言逐字节回到原始像素——五种全过才算真的对。
    /// Paeth 尤其重要：它的平局取舍顺序（a→b→c）是规范定的，写反了在纯色块上测不出来。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Png_AllRowFilters_RoundTrip(int filterType)
    {
        // 3x3 真彩+alpha，像素值刻意有梯度（纯色块会让多数过滤器退化，测不出错误）
        byte[] pixels =
        [
            10, 20, 30, 40,   50, 60, 70, 80,   90, 100, 110, 120,
            130, 140, 150, 160,  170, 180, 190, 200,  210, 220, 230, 240,
            250, 5, 15, 25,   35, 45, 55, 65,   75, 85, 95, 105,
        ];

        byte[] filtered = ApplyFilter(pixels, width: 3, height: 3, channels: 4, filterType);
        byte[] png = BuildPngWithFilteredRows(3, 3, 6, filtered);

        OrielImage image = OrielImage.FromBytes(png);

        Assert.Equal(pixels, image.Rgba.ToArray());
    }

    /// <summary>按 PNG 规范正向应用某个行过滤器（与解码器的 Unfilter 互为逆运算）。</summary>
    private static byte[] ApplyFilter(byte[] pixels, int width, int height, int channels, int filterType)
    {
        int stride = width * channels;
        byte[] output = new byte[(stride + 1) * height];
        int bpp = channels;

        for (int y = 0; y < height; y++)
        {
            output[y * (stride + 1)] = (byte)filterType;
            int prevRow = (y - 1) * stride;

            for (int x = 0; x < stride; x++)
            {
                byte cur = pixels[y * stride + x];
                byte left = x >= bpp ? pixels[y * stride + x - bpp] : (byte)0;
                byte up = y > 0 ? pixels[prevRow + x] : (byte)0;
                byte upLeft = y > 0 && x >= bpp ? pixels[prevRow + x - bpp] : (byte)0;

                byte value = filterType switch
                {
                    0 => cur,
                    1 => (byte)(cur - left),
                    2 => (byte)(cur - up),
                    3 => (byte)(cur - ((left + up) >> 1)),
                    4 => (byte)(cur - PaethPredictor(left, up, upLeft)),
                    _ => throw new ArgumentOutOfRangeException(nameof(filterType)),
                };

                output[y * (stride + 1) + 1 + x] = value;
            }
        }

        return output;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    /// <summary>造一张 PNG，但 IDAT 里的行数据已经是过滤后的（每行首字节是 filter 类型）。</summary>
    private static byte[] BuildPngWithFilteredRows(int width, int height, byte colorType, byte[] filteredRows)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(filteredRows);
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    [Fact]
    public void Png_InvalidSignature_Throws()
    {
        var ex = Assert.Throws<OrielImageFormatException>(
            () => OrielImage.FromBytes([1, 2, 3, 4, 5, 6, 7, 8, 9]));

        Assert.Contains("无法识别的图像格式", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Png_Interlaced_ThrowsWithClearMessage()
    {
        byte[] png = BuildPng(1, 1, 6, [1, 2, 3, 4]);
        // 把 IHDR 的 interlace 字节（偏移 8 签名 + 8 长度/类型 + 12）改成 1
        png[8 + 8 + 12] = 1;

        var ex = Assert.Throws<OrielImageFormatException>(() => OrielImage.FromBytes(png));
        Assert.Contains("隔行扫描", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Png_SixteenBitDepth_ThrowsWithClearMessage()
    {
        byte[] png = BuildPng(1, 1, 6, [1, 2, 3, 4]);
        png[8 + 8 + 8] = 16; // bit depth

        var ex = Assert.Throws<OrielImageFormatException>(() => OrielImage.FromBytes(png));
        Assert.Contains("8 位", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrielImage_LengthMismatch_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OrielImage(new byte[7], 2, 1));
        Assert.Contains("不匹配", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrielImage_NonPositiveSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OrielImage(new byte[0], 0, 1));
        Assert.Throws<ArgumentException>(() => new OrielImage(new byte[0], 1, 0));
    }

    [Fact]
    public void FromFile_MissingFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.png");
        Assert.Throws<FileNotFoundException>(() => OrielImage.FromFile(missing));
    }

    [Fact]
    public void TryFromFile_MissingFile_ReturnsFalseWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.png");

        Assert.False(OrielImage.TryFromFile(missing, out OrielImage? image, out string? error));
        Assert.Null(image);
        Assert.NotNull(error);
    }

    [Fact]
    public void FromFile_RelativePath_ResolvesAgainstBaseDirectory()
    {
        // 相对路径按 exe 目录解析（与图标选项一致），不存在时抛的是那个解析后的绝对路径
        var ex = Assert.Throws<FileNotFoundException>(
            () => OrielImage.FromFile("definitely-not-here.png"));

        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            ex.FileName!,
            StringComparison.Ordinal);
    }

    /// <summary>解仓库里真实的 PNG/ICO——确认对真实编码器的输出也成立。</summary>
    [Theory]
    [InlineData("app.png")]
    [InlineData("app.ico")]
    public void RealFilesInDemo_DecodeToNonEmptyRgba(string name)
    {
        string? path = FindDemoAsset(name);
        if (path is null)
        {
            return; // 样例产物不在（例如未构建 demo）时不失败
        }

        OrielImage image = OrielImage.FromFile(path);

        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
        // 真的解出了内容，而不是一张全零的图
        Assert.Contains(image.Rgba.ToArray(), b => b != 0);
    }

    /// <summary>在仓库里找 demo 的图标资源（源码目录优先，其次构建产物）。</summary>
    private static string? FindDemoAsset(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "OrielDemo", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
