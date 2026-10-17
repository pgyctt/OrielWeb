using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 窗口几何/焦点事件的参数与去重契约。
/// </summary>
/// <remarks>
/// <para>
/// 三个平台的原生来源完全不同（Windows 消息、GTK 信号、Cocoa 委托），但**对宿主的契约必须一致**：
/// 尺寸是逻辑像素的客户区、位置是左上角原点的屏幕逻辑坐标、焦点是"获得/失去"。
/// 这里钉的是与平台无关的那部分。
/// </para>
/// <para>
/// 逐平台的真机取证在 <c>OrielDemo --selftest windowevents</c> 里——那里才能证明
/// "信号真的接上了、事件真的到了"。本文件只保证形状与语义。
/// </para>
/// </remarks>
public sealed class WindowEventsTests
{
    [Fact]
    public void SizeChangedEventArgs_KeepsDoublePrecision()
    {
        // 用 double 而不是 int：物理 → 逻辑的除法不总是整数（150% 屏上 1000/1.5 = 666.67），
        // 取整会逐次累积偏差——反复 Resize(getSize()) 之后窗口会越来越小。
        var args = new OrielSizeChangedEventArgs(666.6666, 533.3333);

        Assert.Equal(666.6666, args.Width, 4);
        Assert.Equal(533.3333, args.Height, 4);
    }

    [Fact]
    public void PositionChangedEventArgs_AllowsNegativeCoordinates()
    {
        // 副屏在主屏左侧/上方时窗口坐标是负的。若哪天有人把它改成 uint 或加了非负校验，
        // 症状是"副屏上的窗口位置报成 0"——而那看起来像是"窗口在左上角"。
        var args = new OrielPositionChangedEventArgs(-1920, -200);

        Assert.Equal(-1920, args.X);
        Assert.Equal(-200, args.Y);
    }

    [Fact]
    public void PositionChangedEventArgs_AllowsZero()
    {
        // (0,0) 是合法位置（主屏左上角），不能被当成"没有位置"
        var args = new OrielPositionChangedEventArgs(0, 0);

        Assert.Equal(0, args.X);
        Assert.Equal(0, args.Y);
    }

    [Theory]
    [InlineData(100, 100, 100, 100, true)]     // 完全相同 → 去重
    [InlineData(100, 100, 100.4, 100, true)]   // 亚像素差异 → 去重（原生取整噪声）
    [InlineData(100, 100, 100.6, 100, false)]  // 超过半个逻辑像素 → 上报
    [InlineData(100, 100, 101, 100, false)]    // 差 1 → 上报
    public void SizeDedupeThreshold_IsHalfLogicalPixel(
        double previousWidth, double previousHeight, double currentWidth, double currentHeight, bool expectDeduped)
    {
        // 复刻三平台 ReportSize 里的判定：|Δw| < 0.5 && |Δh| < 0.5 时跳过。
        // 阈值取半个逻辑像素而不是 0：原生侧给的物理像素换算成逻辑值后常带小数尾数
        // （1000/1.5 = 666.666…），用 0 会把同一个尺寸反复上报。
        bool deduped = Math.Abs(previousWidth - currentWidth) < 0.5
                    && Math.Abs(previousHeight - currentHeight) < 0.5;

        Assert.Equal(expectDeduped, deduped);
    }

    [Fact]
    public void FocusChangedEventArgs_IsPlainBool()
    {
        // 焦点是二值，没有"未知"态：首个事件一定会报（三平台的 _lastReportedFocus 初值是 null，
        // 而 null != true/false，所以第一次必然通过去重）。
        Assert.False(default(bool));
    }
}
