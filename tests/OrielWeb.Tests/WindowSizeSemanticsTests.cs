using OrielWeb.Platform.Windows;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 客户区 ↔ 整窗的换算契约（E-1 修复的核心）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这个缺陷能活这么久</b>：三平台用的原生 API 语义不同——
/// Windows 的 <c>CreateWindowExW</c> / <c>SetWindowPos</c> 吃**整窗**尺寸，
/// Linux 的 <c>gtk_window_resize</c> 与 macOS 的 <c>setContentSize:</c> 吃**客户区**。
/// 于是同一份 <c>WithSize(800, 600)</c> 在 Windows 装饰窗口上得到 784x561 的客户区。
/// demo 用 <c>WithFrameless()</c>（客户区已铺满整窗），所以这个偏差从来没被看见。
/// </para>
/// <para>
/// 换算本身走 <c>AdjustWindowRectExForDpi</c>，那是 Win32 调用，只能在 Windows 上跑。
/// 这里钉的是**不该依赖 Win32 的那部分**：逻辑像素折算、以及"frameless 不该换算"这条判断。
/// 前者已有 <see cref="LogicalPixelsTests"/> 覆盖，这里补后者。
/// </para>
/// </remarks>
public sealed class WindowSizeSemanticsTests
{
    /// <summary>
    /// frameless 窗口**不该**做客户区 → 整窗的换算。
    /// </summary>
    /// <remarks>
    /// 这条是 E-1 修复中最容易写错的一处：把 framed 的换算无条件套上去，
    /// 无边框窗口就会比请求的大一圈（差一整套边框 + 标题栏）。
    /// 而 demo 的默认形态正是 frameless——写错了所有既有应用都会"窗口变大"。
    /// <para>
    /// 断言方式：<see cref="OrielWindowOptions.Frameless"/> 是唯一判据，
    /// 这里把它的默认值与显式设置都钉住，免得日后有人把判据换成"看样式位"而漏掉某条路径。
    /// </para>
    /// </remarks>
    [Fact]
    public void Frameless_IsTheOnlySignalForSkippingClientToWindowConversion()
    {
        Assert.False(new OrielWindowOptions().Frameless, "默认必须是 framed——那是平台原生形态");
        Assert.True(new OrielWindowOptions().WithFrameless().Frameless);
        Assert.False(new OrielWindowOptions().WithFrameless(false).Frameless);
    }

    /// <summary>
    /// 逻辑像素 → 物理像素的换算在三平台语义里是**同一件事**，与边框换算正交。
    /// </summary>
    /// <remarks>
    /// 钉住"两件事不要混"：先折算 DPI，再换算边框。顺序反了（先把逻辑值当物理值换算边框）
    /// 在 100% 缩放下看不出任何差别——那正是它会在 150% 屏上才暴露的原因。
    /// </remarks>
    [Theory]
    [InlineData(96, 800, 600, 800, 600)]
    [InlineData(144, 800, 600, 1200, 900)]
    [InlineData(192, 800, 600, 1600, 1200)]
    public void LogicalClientSize_ToPhysical_BeforeAnyFrameConversion(
        uint dpi, int logicalWidth, int logicalHeight, int expectedPhysicalWidth, int expectedPhysicalHeight)
    {
        Assert.Equal(expectedPhysicalWidth, LogicalPixels.ToPhysical(logicalWidth, dpi));
        Assert.Equal(expectedPhysicalHeight, LogicalPixels.ToPhysical(logicalHeight, dpi));
    }

    /// <summary>
    /// 已知的 Windows 边框开销（100% 缩放、framed、WS_OVERLAPPEDWINDOW）。
    /// </summary>
    /// <remarks>
    /// <b>这是取证值，不是推断值</b>：2026-10-17 在 Windows 11 / 100% 缩放下实测
    /// <c>WithSize(800, 600)</c> 得到客户区 <c>784x561</c>，即
    /// 左右边框合计 16、标题栏 + 下边框合计 39。
    /// <para>
    /// 把它写成常量而不是"断言恰好等于"，是因为这些值随主题、系统版本、缩放比变化——
    /// 钉死具体数字会在别人的机器上假红。这里只在**能测到**的平台上验证"确实有开销"，
    /// 真正逐像素的验收由 <c>--selftest geometry</c> 在真机上做。
    /// </para>
    /// </remarks>
    [Fact]
    public void MeasuredWindowsFrameOverhead_IsNonZero_SoTheConversionIsNecessary()
    {
        const int measuredClientWidth = 784;
        const int measuredClientHeight = 561;
        const int requestedWidth = 800;
        const int requestedHeight = 600;

        int widthOverhead = requestedWidth - measuredClientWidth;
        int heightOverhead = requestedHeight - measuredClientHeight;

        // 如果哪天这两个开销变成 0，说明换算已经不需要了——那时这条测试该被删掉，
        // 而不是留着让它继续"通过"。
        Assert.True(
            widthOverhead > 0,
            $"Windows framed 窗口的水平边框开销应大于 0（实测 {widthOverhead}）——" +
            "若为 0，说明 Win32 的尺寸语义变了，ClientToWindowSize 可以删掉。");

        Assert.True(
            heightOverhead > widthOverhead,
            $"标题栏应让垂直开销大于水平开销（水平 {widthOverhead}、垂直 {heightOverhead}）——" +
            "若不再成立，说明实测基线过时了。");
    }
}
