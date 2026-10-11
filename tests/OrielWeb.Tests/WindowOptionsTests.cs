using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 窗口选项里那些"纯数据"的规矩：底色的分量拆法与默认值。
/// </summary>
/// <remarks>
/// 三个平台的实现都从 <see cref="OrielWindowOptions.TryGetBackgroundArgb"/> 取分量
/// （见 API.md 的 <c>BackgroundColor</c> 行）——R 与 B 搞反在深色底上不容易一眼看出来，
/// 所以在这里把它钉住。
/// </remarks>
public sealed class WindowOptionsTests
{
    [Fact]
    public void BackgroundColor_NotSet_ReportsFalse()
    {
        var options = new OrielWindowOptions();

        Assert.Null(options.BackgroundColor);
        Assert.False(options.TryGetBackgroundArgb(out _, out _, out _, out _));
    }

    [Fact]
    public void BackgroundColor_UnpacksAsA_R_G_B()
    {
        var options = new OrielWindowOptions().WithBackgroundColor(0xFF123456);

        Assert.True(options.TryGetBackgroundArgb(out byte a, out byte r, out byte g, out byte b));
        Assert.Equal(0xFF, a);
        Assert.Equal(0x12, r);
        Assert.Equal(0x34, g);
        Assert.Equal(0x56, b);
    }

    [Fact]
    public void BackgroundColor_ThreeByteOverload_IsOpaque()
    {
        var options = new OrielWindowOptions().WithBackgroundColor(0x11, 0x22, 0x33);

        Assert.True(options.TryGetBackgroundArgb(out byte a, out byte r, out byte g, out byte b));
        Assert.Equal(((byte)0xFF, (byte)0x11, (byte)0x22, (byte)0x33), (a, r, g, b));
    }
}
