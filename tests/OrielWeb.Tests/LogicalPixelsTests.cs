using OrielWeb.Platform.Windows;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 逻辑像素 → 物理像素的换算（Windows Per-Monitor-V2）。
/// </summary>
/// <remarks>
/// 回归防护：评审 2026-10-08 发现 2 —— 窗口尺寸按 DPI 折算、位置（<c>At</c>/<c>MoveTo</c>）
/// 却漏了，于是 150% 屏上二者语义不一致、也与另外两个平台不一致。两条路径现在共用
/// <see cref="LogicalPixels.ToPhysical"/>，这里把换算本身钉住。
/// </remarks>
public sealed class LogicalPixelsTests
{
    [Theory]
    [InlineData(96, 1000, 1000)]   // 100%：恒等
    [InlineData(120, 1000, 1250)]  // 125%
    [InlineData(144, 1000, 1500)]  // 150%
    [InlineData(192, 1000, 2000)]  // 200%
    public void ToPhysical_ScalesByWindowDpi(uint dpi, int logical, int expected)
        => Assert.Equal(expected, LogicalPixels.ToPhysical(logical, dpi));

    [Fact]
    public void ToPhysical_RoundsToWholePixels()
    {
        Assert.Equal(1, LogicalPixels.ToPhysical(1, 120)); // 1.25 → 1
        Assert.Equal(3, LogicalPixels.ToPhysical(2, 144)); // 3.0
        Assert.Equal(4, LogicalPixels.ToPhysical(3, 144)); // 4.5 → 4（.5 向偶数，与迁移前的实现一致）
    }

    [Fact]
    public void ToPhysical_KeepsSignAndZero()
    {
        // 屏幕坐标可以是负的（副屏在主屏左侧/上方），位置折算不能丢符号
        Assert.Equal(-1500, LogicalPixels.ToPhysical(-1000, 144));
        Assert.Equal(0, LogicalPixels.ToPhysical(0, 192));
    }
}
