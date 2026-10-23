using OrielWeb.Platform.Linux;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <see cref="LinuxMappings"/> 的表驱动测试。
/// </summary>
/// <remarks>
/// 目前只有一个映射函数，但这一个值得单独钉住：GTK 的 <c>GdkWindowEdge</c> 与库的
/// <see cref="OrielResizeDirection"/> 不是同名同序的枚举，映射错一个方向的表现是
/// "拖右边却在往上拉"——用户会觉得是窗口管理器坏了。
/// </remarks>
public sealed class LinuxMappingsTests
{
    [Theory]
    [InlineData(OrielResizeDirection.East, (int)GdkWindowEdge.East)]
    [InlineData(OrielResizeDirection.North, (int)GdkWindowEdge.North)]
    [InlineData(OrielResizeDirection.NorthEast, (int)GdkWindowEdge.NorthEast)]
    [InlineData(OrielResizeDirection.NorthWest, (int)GdkWindowEdge.NorthWest)]
    [InlineData(OrielResizeDirection.South, (int)GdkWindowEdge.South)]
    [InlineData(OrielResizeDirection.SouthEast, (int)GdkWindowEdge.SouthEast)]
    [InlineData(OrielResizeDirection.SouthWest, (int)GdkWindowEdge.SouthWest)]
    [InlineData(OrielResizeDirection.West, (int)GdkWindowEdge.West)]
    public void EdgeFor_MapsEveryDirection(OrielResizeDirection direction, int expected)
        => Assert.Equal((GdkWindowEdge)expected, LinuxMappings.EdgeFor(direction));

    [Fact]
    public void EdgeFor_NeverReturnsAnUndefinedEdge()
    {
        // 默认分支给 West（与 switch 的兜底一致）。这条挡的是"新增方向后忘了补 case"：
        // 届时所有新方向都会静默变成拖左边界，而那种错误在 Linux 上要真机拖动才看得见。
        foreach (OrielResizeDirection direction in Enum.GetValues<OrielResizeDirection>())
        {
            Assert.True(Enum.IsDefined(LinuxMappings.EdgeFor(direction)), direction.ToString());
        }
    }
}
