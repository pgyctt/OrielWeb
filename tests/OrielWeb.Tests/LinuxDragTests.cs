using OrielWeb.Platform.Linux;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 无边框拖动的后端分叉判据。真正的后端识别要跑 GTK（<c>gdk_display_get_default</c>），
/// 这里覆盖它可断言的那一半：什么样的显示名算 Wayland。
/// </summary>
public sealed class LinuxDragTests
{
    [Theory]
    [InlineData("wayland-0")]
    [InlineData("wayland-1")]
    [InlineData("wayland")]
    public void WaylandDisplayNames_AreRecognized(string name)
        => Assert.True(LinuxDragSupport.IsWaylandDisplay(name));

    [Theory]
    [InlineData(":0")]
    [InlineData(":1")]
    [InlineData("host:0")]
    [InlineData("localhost:10.0")]     // ssh -X 转发
    public void X11DisplayNames_AreNotRecognized(string name)
        => Assert.False(LinuxDragSupport.IsWaylandDisplay(name));

    [Fact]
    public void MissingDisplayName_IsNotWayland()
        // 拿不到 GdkDisplay 时按 X11 一侧处理：流式拖动是保守的选择——它在 X11 上确实有效，
        // 而把移动交给一个并不存在的合成器则会无声无息地什么都不做。
        => Assert.False(LinuxDragSupport.IsWaylandDisplay(null));

    [Fact]
    public void EmptyDisplayName_IsNotWayland()
        => Assert.False(LinuxDragSupport.IsWaylandDisplay(string.Empty));

    [Fact]
    public void UppercaseIsNotWayland()
        // 判据用 Ordinal：GDK 给的显示名就是 WAYLAND_DISPLAY 里那个小写的 socket 名，
        // 大写形式只可能来自别的东西，不该被认成 Wayland。
        => Assert.False(LinuxDragSupport.IsWaylandDisplay("WAYLAND-0"));

    // ---- Wayland 的"动过才算拖动"阈值 ----

    [Fact]
    public void StillWithinThreshold_DoesNotStartMove()
    {
        // 按下一动不动、或只抖了一两像素（点击 / 双击）都不该把指针交给合成器——
        // 交出去就 grab 了，第二次点击被吞、dblclick 不触发。
        Assert.False(LinuxDragSupport.ExceedsMoveThreshold(0, 0));
        Assert.False(LinuxDragSupport.ExceedsMoveThreshold(1, 1));
        Assert.False(LinuxDragSupport.ExceedsMoveThreshold(LinuxDragSupport.MoveThresholdPx, 0));
        Assert.False(LinuxDragSupport.ExceedsMoveThreshold(0, -LinuxDragSupport.MoveThresholdPx));
    }

    [Fact]
    public void BeyondThreshold_StartsMove()
    {
        int over = LinuxDragSupport.MoveThresholdPx + 1;
        Assert.True(LinuxDragSupport.ExceedsMoveThreshold(over, 0));
        Assert.True(LinuxDragSupport.ExceedsMoveThreshold(0, -over));
        Assert.True(LinuxDragSupport.ExceedsMoveThreshold(-over, 0));
    }

    [Fact]
    public void AnySingleAxisIsEnough()
    {
        // 判据是"任一轴超过"，不是两轴都超过：只往右拖也算拖动
        Assert.True(LinuxDragSupport.ExceedsMoveThreshold(LinuxDragSupport.MoveThresholdPx + 1, 0));
        Assert.True(LinuxDragSupport.ExceedsMoveThreshold(0, LinuxDragSupport.MoveThresholdPx + 1));
    }
}
