using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 剪贴板的两种线格式（B19）：PNG 编码器与 CF_HTML 头。
/// </summary>
/// <remarks>
/// 这两块都是**纯计算**，而它们的错误后果又都是"静默的"：
/// PNG 编错 → 粘贴出来是一张花屏或打不开的图；CF_HTML 偏移错 → 粘贴出来是乱码或被截断，
/// 而写入方这边一切正常。所以必须在这一层逐字节验证，不能靠真机点一遍。
/// </remarks>
public sealed class ClipboardFormatTests
{
    // ---- PNG ----

    [Fact]
    public void PngStartsWithTheSignatureAndIhdr()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        byte[] png = OrielPngEncoder.Encode(rgba, 2, 2);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        // 第一个块必须是 IHDR，长度 13
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4)));
        Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]);   // 位深
        Assert.Equal(6, png[25]);   // 颜色类型 6 = RGBA

        // 末尾必须是 IEND
        Assert.Equal("IEND"u8.ToArray(), png[^8..^4]);
    }

    [Fact]
    public void PngRoundTripsThroughTheDecoder()
    {
        // 2x2：红、绿 / 蓝、半透明白——覆盖"三个通道 + alpha"
        byte[] rgba =
        [
            255, 0, 0, 255,     0, 255, 0, 255,
            0, 0, 255, 255,     255, 255, 255, 128,
        ];

        byte[] png = OrielPngEncoder.Encode(rgba, 2, 2);
        OrielImage decoded = OrielImage.FromBytes(png);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba.ToArray());
    }

    [Fact]
    public void PngRejectsMismatchedSize()
    {
        Assert.Throws<ArgumentException>(() => OrielPngEncoder.Encode(new byte[7], 2, 2));
        Assert.Throws<ArgumentException>(() => OrielPngEncoder.Encode(ReadOnlySpan<byte>.Empty, 0, 1));
    }

    [Fact]
    public void PngHandlesOnePixelAndLargeWidths()
    {
        byte[] one = OrielPngEncoder.Encode([1, 2, 3, 4], 1, 1);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, OrielImage.FromBytes(one).Rgba.ToArray());

        // 宽 300（行字节数 1200，超过 1024 的分块边界，用来撞 STJ/流式实现里常见的块大小假设）
        var wide = new byte[300 * 4];
        Array.Fill(wide, (byte)7);
        OrielImage decoded = OrielImage.FromBytes(OrielPngEncoder.Encode(wide, 300, 1));
        Assert.Equal(300, decoded.Width);
        Assert.Equal(wide, decoded.Rgba.ToArray());
    }

    // ---- CF_HTML ----

    /// <summary>从头部取某个偏移字段（形如 <c>StartHTML:0000000105</c>）。</summary>
    private static int Offset(string text, string key)
    {
        int start = text.IndexOf(key + ":", StringComparison.Ordinal);
        Assert.True(start >= 0, $"头部缺少 {key}");

        start += key.Length + 1;
        int end = text.IndexOf('\r', start);
        string digits = text[start..end];

        Assert.Equal(10, digits.Length);   // 固定宽度是偏移可计算的前提
        return int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void CfHtmlOffsetsPointAtTheRightPlaces()
    {
        const string Html = "<p>你好，世界 🎉</p>";
        byte[] bytes = OrielClipboardHtml.Build(Html);
        string text = Encoding.UTF8.GetString(bytes);

        int startHtml = Offset(text, "StartHTML");
        int endHtml = Offset(text, "EndHTML");
        int startFragment = Offset(text, "StartFragment");
        int endFragment = Offset(text, "EndFragment");

        // StartHTML 指向 <html>，EndHTML 是整段数据的长度
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.Equal(bytes.Length, endHtml);

        // 片段的字节范围必须**逐字节**等于原文——这是这个格式唯一真正重要的事
        Assert.Equal(Html, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));

        // 片段之前正好是 StartFragment 标记，之后正好是 EndFragment 标记
        Assert.True(startFragment >= OrielClipboardHtml.StartFragmentMark.Length);
        Assert.Equal(
            OrielClipboardHtml.StartFragmentMark,
            Encoding.UTF8.GetString(bytes, startFragment - OrielClipboardHtml.StartFragmentMark.Length,
                OrielClipboardHtml.StartFragmentMark.Length));
        Assert.Equal(
            OrielClipboardHtml.EndFragmentMark,
            Encoding.UTF8.GetString(bytes, endFragment, OrielClipboardHtml.EndFragmentMark.Length));
    }

    [Fact]
    public void CfHtmlHeaderUsesFixedWidthOffsets()
    {
        // 头部长度必须与正文长度无关，否则"先量后填"就不成立
        string small = Encoding.UTF8.GetString(OrielClipboardHtml.Build("a"));
        string large = Encoding.UTF8.GetString(OrielClipboardHtml.Build(new string('x', 50_000)));

        int smallHeader = small.IndexOf("<html>", StringComparison.Ordinal);
        int largeHeader = large.IndexOf("<html>", StringComparison.Ordinal);

        Assert.Equal(smallHeader, largeHeader);
        Assert.Equal(smallHeader, Offset(small, "StartHTML"));
    }

    [Fact]
    public void CfHtmlHandlesEmptyFragment()
    {
        byte[] bytes = OrielClipboardHtml.Build(string.Empty);
        string text = Encoding.UTF8.GetString(bytes);

        int startFragment = Offset(text, "StartFragment");
        int endFragment = Offset(text, "EndFragment");

        Assert.Equal(startFragment, endFragment);
        Assert.Equal(bytes.Length, Offset(text, "EndHTML"));
    }
}
