using System.Globalization;
using OrielWeb;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 注入给页面的宿主事实快照（<c>window.oriel.system</c>）的单测：取值回退与数值格式化。
/// </summary>
/// <remarks>
/// 这三件事都必须能离线断言：回退值决定"取不到系统设置时页面看到什么"，
/// 而数值格式化一旦按当前文化写出逗号，整个注入脚本就是坏的（`scale: 1,5` 在 JS 里是逗号表达式）。
/// </remarks>
public sealed class SystemSnapshotTests
{
    [Fact]
    public void NormalizeKeepsUsableValues()
    {
        var snapshot = OrielSystemSnapshot.Normalize(400, 2.0);

        Assert.Equal(400, snapshot.DoubleClickTimeMs);
        Assert.Equal(2.0, snapshot.Scale);
    }

    [Fact]
    public void ScaleBelowOneIsAllowed()
    {
        // GDK_DPI_SCALE=0.5 这类环境真的存在；只要为正就不是"取不到"
        Assert.Equal(0.5, OrielSystemSnapshot.Normalize(500, 0.5).Scale);
    }

    [Theory]
    // 三平台各自的"取不到"：Windows 句柄无效时 GetDpiForWindow 返回 0、
    // GTK 没有 GtkSettings、macOS 没有显示器时 mainScreen 是 nil。
    [InlineData(0, 0.0)]
    [InlineData(-1, -2.0)]
    [InlineData(500, double.NaN)]
    [InlineData(500, double.PositiveInfinity)]
    public void NormalizeFallsBack(int rawMs, double rawScale)
    {
        var snapshot = OrielSystemSnapshot.Normalize(rawMs, rawScale);

        Assert.True(snapshot.DoubleClickTimeMs > 0);
        Assert.True(double.IsFinite(snapshot.Scale) && snapshot.Scale > 0);
    }

    [Fact]
    public void NormalizeUsesDocumentedDefaults()
    {
        var snapshot = OrielSystemSnapshot.Normalize(0, 0);

        Assert.Equal(OrielSystemSnapshot.DefaultDoubleClickTimeMs, snapshot.DoubleClickTimeMs);
        Assert.Equal(OrielSystemSnapshot.DefaultScale, snapshot.Scale);
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
            // 逗号是小数点的文化：按当前文化格式化会写出 scale: 1,5
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
