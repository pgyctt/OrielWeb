using System.Text.Json;
using OrielWeb.Platform.Windows;
using OrielWeb.Platform.Windows.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <see cref="Win32Conversions"/> 的表驱动测试。
/// </summary>
/// <remarks>
/// <para>
/// 这些函数原先藏在 4000 行的 <c>Win32WindowHost</c> 里，与 P/Invoke 混在一起，
/// 没有任何托管测试覆盖。抽到静态类之后它们变成"可以直接调用"的东西——
/// 这是 R4b 的目标：把"能不能测"变成"要不要测"。
/// </para>
/// <para>
/// 为什么不测宿主本身：窗口创建、消息循环、WebView2 装配都需要真实的 Win32 环境，
/// 单测里只能靠替身，而替身测出来的是替身的行为。真正的覆盖在
/// <c>smoke-windows</c> 的 <c>--selftest</c> 里。
/// </para>
/// </remarks>
public sealed class Win32ConversionsTests
{
    // ---- ARGB → ABGR ----
    //
    // DWM 的 GradientColor 用的是 ABGR 序（Windows 文档写作 "ABGR"），
    // 而库的公开面用 ARGB。这层换序写错了不会报错，只会让强调色红蓝互换。

    [Theory]
    [InlineData(0xFFFF0000u, 0xFF0000FFu)]   // 不透明红
    [InlineData(0xFF0000FFu, 0xFFFF0000u)]   // 不透明蓝
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]   // 白
    [InlineData(0xFF00FF00u, 0xFF00FF00u)]   // 绿：R 与 B 对称，换序后不变
    [InlineData(0x00112233u, 0x00332211u)]   // 全透明，只验证通道顺序
    public void ToAbgr_SwapsRedAndBlue(uint argb, uint expected)
        => Assert.Equal(expected, Win32Conversions.ToAbgr(argb));

    [Theory]
    [InlineData(0x00ABCDEFu)]
    [InlineData(0x7F102030u)]
    [InlineData(0xFFDEADBEu)]
    public void ToAbgr_IsItsOwnInverse(uint argb)
    {
        // 换序两次回到原值：这条比逐例对照更能挡住"漏搬一个通道"的错误
        Assert.Equal(argb, Win32Conversions.ToAbgr(Win32Conversions.ToAbgr(argb)));
    }

    [Fact]
    public void ToAbgr_PreservesAlpha()
    {
        // alpha 是强调色唯一不能丢的通道：丢了之后整个 DWM 属性会被系统忽略
        foreach (uint alpha in new uint[] { 0x00, 0x40, 0x80, 0xC0, 0xFF })
        {
            uint argb = (alpha << 24) | 0x00123456u;
            Assert.Equal(alpha, Win32Conversions.ToAbgr(argb) >> 24);
        }
    }

    // ---- 窗口样式位 ----

    [Theory]
    [InlineData(true, true)]    // 无边框 + 可缩放
    [InlineData(true, false)]   // 无边框 + 固定尺寸
    [InlineData(false, true)]   // 普通窗口 + 可缩放
    [InlineData(false, false)]  // 普通窗口 + 固定尺寸
    public void ComputeStyle_MatchesFramelessAndResizable(bool frameless, bool resizable)
    {
        var options = new OrielWindowOptions { Frameless = frameless, Resizable = resizable };

        uint style = Win32Conversions.ComputeStyle(options);

        if (frameless)
        {
            Assert.NotEqual(0u, style & Win32Constants.WS_POPUP);

            // 无边框窗口仍带系统菜单：留着它才有 Alt+Space 与任务栏右键菜单
            Assert.NotEqual(0u, style & Win32Constants.WS_SYSMENU);
        }
        else
        {
            // 普通窗口从 WS_OVERLAPPEDWINDOW 出发，固定尺寸时再挖掉两个位
            uint expected = resizable
                ? Win32Constants.WS_OVERLAPPEDWINDOW
                : Win32Constants.WS_OVERLAPPEDWINDOW
                    & ~(Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX);

            Assert.Equal(expected, style);
        }

        // 可缩放 = 有 WS_THICKFRAME（能拖边）与两个最大化/最小化按钮位
        uint resizeBits = Win32Constants.WS_THICKFRAME
            | Win32Constants.WS_MAXIMIZEBOX
            | Win32Constants.WS_MINIMIZEBOX;

        if (resizable)
        {
            Assert.Equal(resizeBits, style & resizeBits);
        }
        else
        {
            Assert.Equal(0u, style & Win32Constants.WS_THICKFRAME);
            Assert.Equal(0u, style & Win32Constants.WS_MAXIMIZEBOX);
        }
    }

    // ---- 消息字段读取 ----

    [Theory]
    [InlineData("""{"level":"warning"}""", "level", "warning")]
    [InlineData("""{"text":""}""", "text", "")]
    [InlineData("""{"level":null}""", "level", "")]      // JSON null → 空串，不是 "null"
    [InlineData("""{"level":42}""", "level", "")]        // 类型不对 → 空串，不抛
    [InlineData("""{"other":"x"}""", "level", "")]       // 缺字段 → 空串
    public void ReadStringProperty_NeverThrows(string json, string name, string expected)
    {
        // 入站消息来自页面，字段形状不可信：这里读的是**日志/诊断字段**，
        // 读不到时给空串比抛异常更合适——一条格式不对的 console 消息不该中断整条通道。
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal(expected, Win32Conversions.ReadStringProperty(doc.RootElement, name));
    }

    // ---- 运行时缺失提示 ----

    [Fact]
    public void RuntimeMissingMessage_WithoutFolder_PointsToDownload()
    {
        string message = Win32Conversions.RuntimeMissingMessage(null);

        Assert.Contains("未检测到 Microsoft Edge WebView2 运行时", message, StringComparison.Ordinal);
        Assert.Contains(OrielWebView2RuntimeMissingEventArgs.DownloadUrl, message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeMissingMessage_WithFolder_NamesFolderAndEnvVar()
    {
        // 固定版本运行时是**显式配置**出来的：提示必须说清"你配的那个目录不对"，
        // 而不是让用户去装一个其实已经装了的运行时。
        string message = Win32Conversions.RuntimeMissingMessage(@"C:\fixed-runtime");

        Assert.Contains(@"C:\fixed-runtime", message, StringComparison.Ordinal);
        Assert.Contains("ORIEL_WEBVIEW2_FOLDER", message, StringComparison.Ordinal);
        Assert.DoesNotContain(OrielWebView2RuntimeMissingEventArgs.DownloadUrl, message, StringComparison.Ordinal);
    }
}
