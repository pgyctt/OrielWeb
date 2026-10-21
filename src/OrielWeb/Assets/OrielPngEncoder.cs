using System.Buffers.Binary;
using System.IO.Compression;

namespace OrielWeb;

/// <summary>
/// 把裸 RGBA 编码成 PNG（剪贴板读图用）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写而不用现成库</b>：本库是 Native AOT、零第三方依赖，而 "RGBA → PNG" 在
/// BCL 里没有对应物（<c>System.Drawing.Common</c> 自 .NET 6 起只支持 Windows，且不是 AOT 友好的）。
/// PNG 的结构本身很简单：签名 + IHDR + IDAT + IEND，压缩交给 <see cref="ZLibStream"/>
/// （它产出的正是 PNG 要求的 zlib 封装 deflate），CRC32 一张表就够。
/// </para>
/// <para>
/// <b>只写一种形态</b>：8 位色深、颜色类型 6（RGBA）、无隔行、每行 filter 0。
/// 这是"位图进剪贴板"的最短路径——不做自适应滤波（省几十行代码与一次全图扫描），
/// 代价是压缩率略低。剪贴板图片通常只有几 MB，这点体积不值得用复杂度换。
/// </para>
/// <para>
/// 解码方向见 <see cref="OrielImage.FromBytes"/>（那是完整解码器）。
/// 本编码器与它构成往返：<c>Decode(Encode(rgba))</c> 必须逐字节等于 <c>rgba</c>，
/// 这条由单测钉着。
/// </para>
/// </remarks>
internal static class OrielPngEncoder
{
    /// <summary>PNG 文件签名。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>IHDR 的长度（13 字节，固定）。</summary>
    private const int IhdrLength = 13;

    /// <summary>颜色类型 6 = 真彩 + alpha。</summary>
    private const byte ColorTypeRgba = 6;

    /// <summary>CRC32 查表（PNG 用的多项式 0xEDB88320，与 zip 相同）。</summary>
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <summary>
    /// 编码成 PNG 字节。
    /// </summary>
    /// <param name="rgba">行优先、自上而下的 RGBA 像素，长度必须是 <c>width * height * 4</c>。</param>
    /// <param name="width">宽度（像素，必须为正）。</param>
    /// <param name="height">高度（像素，必须为正）。</param>
    /// <exception cref="ArgumentException">尺寸与数据长度不匹配。</exception>
    internal static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"PNG 编码需要正数宽高（收到 {width}x{height}）。");
        }

        long expected = (long)width * height * 4;
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"RGBA 数据长度与宽高不匹配：期望 {expected} 字节（{width}x{height}x4），实到 {rgba.Length} 字节。");
        }

        using var output = new MemoryStream(capacity: 1024 + (int)Math.Min(expected, 64 * 1024 * 1024));
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[IhdrLength];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;                 // 位深
        ihdr[9] = ColorTypeRgba;     // 颜色类型
        ihdr[10] = 0;                // 压缩方法（只有 0）
        ihdr[11] = 0;                // 滤波方法（只有 0）
        ihdr[12] = 0;                // 隔行（0 = 不隔行）
        WriteChunk(output, "IHDR"u8, ihdr);

        WriteChunk(output, "IDAT"u8, CompressScanlines(rgba, width, height));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);

        return output.ToArray();
    }

    /// <summary>
    /// 把像素打包成 PNG 的 IDAT 内容（每行前面加一个 filter 字节，然后 zlib 压缩）。
    /// </summary>
    /// <remarks>
    /// filter 一律用 0（None）：省掉自适应滤波的一次全图扫描。代价是压缩率低一些，
    /// 而剪贴板图片的典型体积下这不值得用复杂度换。
    /// </remarks>
    private static byte[] CompressScanlines(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];

        for (int y = 0; y < height; y++)
        {
            int target = y * (stride + 1);
            raw[target] = 0;                                        // filter = None
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(target + 1, stride));
        }

        using var compressed = new MemoryStream(raw.Length / 2 + 64);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    /// <summary>写一个 PNG 块：长度 + 类型 + 数据 + CRC（CRC 覆盖类型与数据）。</summary>
    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        stream.Write(type);
        stream.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc ^ 0xFFFFFFFFu);
        stream.Write(buffer);
    }

    /// <summary>建 CRC32 查表（一次，之后每条块只查表）。</summary>
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
