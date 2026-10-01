using System.Globalization;
using OrielWeb;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 拖动实现所需宿主事实（双击间隔）的单测：取值回退与数值格式化。
/// </summary>
/// <remarks>
/// 两者都必须能离线断言：回退值决定"取不到系统设置时用什么"；而数值格式化一旦按当前文化
/// 写出逗号，整个注入脚本就是坏的（语法错误的表现是"页面功能全无"）。
/// </remarks>
public sealed class SystemSnapshotTests
{
    [Fact]
    public void NormalizeKeepsUsableValues()
        => Assert.Equal(400, OrielSystemSnapshot.Normalize(400).DoubleClickTimeMs);

    [Theory]
    // 三平台各自的"取不到"：Windows 句柄无效、GTK 没有 GtkSettings、macOS 查不到 NSEvent 类
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NormalizeFallsBack(int rawMilliseconds)
    {
        var settings = OrielSystemSnapshot.Normalize(rawMilliseconds);

        Assert.Equal(OrielSystemSnapshot.DefaultDoubleClickTimeMs, settings.DoubleClickTimeMs);
    }

    [Fact]
    public void DragThresholdIsSmallButNotZero()
    {
        // 阈值是"这一次按下算不算拖动"的判据：0 会让最轻微的抖动都开始拖动，
        // 太大则拖动要等很久才响应。
        Assert.InRange(OrielSystemSnapshot.DragThresholdPx, 1, 8);
    }
}

/// <summary>
/// 数值 → JS 字面量的单测。
/// </summary>
/// <remarks>
/// 单独一个类 + 不可并行的集合：它要临时改 <see cref="CultureInfo.CurrentCulture"/>，
/// 而并行执行下那会渗到别的测试里。
/// </remarks>
[Collection("CultureSensitive")]
public sealed class BridgeTemplateNumberTests
{
    [Fact]
    public void NumberLiteralUsesInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // 逗号是小数点的文化：按当前文化格式化会写出 1,5
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("1.5", OrielBridgeTemplate.NumberLiteral(1.5));
            Assert.Equal("2", OrielBridgeTemplate.NumberLiteral(2.0));
            Assert.Equal("500", OrielBridgeTemplate.NumberLiteral(500));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NumberLiteralNeverEmitsNonFiniteSyntax()
    {
        // NaN/Infinity 写进脚本是语法错误，会让整个桥接脚本加载失败（表现为"页面功能全无"）
        Assert.Equal("1", OrielBridgeTemplate.NumberLiteral(double.NaN));
        Assert.Equal("1", OrielBridgeTemplate.NumberLiteral(double.PositiveInfinity));
    }
}

/// <summary>标记为不可并行执行的测试集合（见 <see cref="BridgeTemplateNumberTests"/>）。</summary>
[CollectionDefinition("CultureSensitive", DisableParallelization = true)]
public sealed class CultureSensitiveCollection;
