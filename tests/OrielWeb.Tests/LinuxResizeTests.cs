using OrielWeb.Platform.Linux;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 无边框窗口的边缘命中判定（<see cref="LinuxResizeSupport"/>）。
/// </summary>
/// <remarks>
/// 这段判定是纯函数，因此能在任意平台上单测——而它要防的两个问题（边缘没反应、
/// 或页面一片区域的点击被吃掉）**只在 Linux 真机上才看得见**，正是最该被钉住的那类。
/// </remarks>
public sealed class LinuxResizeTests
{
    private const int W = 800;
    private const int H = 600;
    private const int B = LinuxResizeSupport.BorderThickness;

    [Fact]
    public void Center_IsNotAnEdge()
        => Assert.Null(LinuxResizeSupport.ResolveEdge(W / 2.0, H / 2.0, W, H, B));

    // 注意 InlineData 里用 int 而不是 GdkWindowEdge：该枚举是 internal，
    // 而 xUnit 的测试方法必须是 public——参数类型可访问性对不上会编译不过（CS0051）。
    [Theory]
    [InlineData(0, 0, (int)GdkWindowEdge.NorthWest)]
    [InlineData(B - 1, B - 1, (int)GdkWindowEdge.NorthWest)]
    [InlineData(W - 1, 0, (int)GdkWindowEdge.NorthEast)]
    [InlineData(0, H - 1, (int)GdkWindowEdge.SouthWest)]
    [InlineData(W - 1, H - 1, (int)GdkWindowEdge.SouthEast)]
    public void Corners_AreResolved(double x, double y, int expected)
        => Assert.Equal((GdkWindowEdge)expected, LinuxResizeSupport.ResolveEdge(x, y, W, H, B));

    [Theory]
    [InlineData(B - 1, H / 2.0, (int)GdkWindowEdge.West)]
    [InlineData(W - B, H / 2.0, (int)GdkWindowEdge.East)]
    [InlineData(W / 2.0, B - 1, (int)GdkWindowEdge.North)]
    [InlineData(W / 2.0, H - B, (int)GdkWindowEdge.South)]
    public void Edges_AreResolved(double x, double y, int expected)
        => Assert.Equal((GdkWindowEdge)expected, LinuxResizeSupport.ResolveEdge(x, y, W, H, B));

    [Fact]
    public void BorderBoundary_IsHalfOpen()
    {
        // 热区是 [0, limit) 与 [width-limit, width)：limit 本身不算边缘。
        // 钉住这一点，免得日后把判定写成 <= 让热区悄悄变宽——页面可点区域会跟着缩小。
        Assert.Equal(GdkWindowEdge.West, LinuxResizeSupport.ResolveEdge(B - 1, H / 2.0, W, H, B));
        Assert.Null(LinuxResizeSupport.ResolveEdge(B, H / 2.0, W, H, B));

        Assert.Null(LinuxResizeSupport.ResolveEdge(W - B - 1, H / 2.0, W, H, B));
        Assert.Equal(GdkWindowEdge.East, LinuxResizeSupport.ResolveEdge(W - B, H / 2.0, W, H, B));
    }

    [Fact]
    public void CornerTakesPrecedenceOverEdge()
    {
        // (0, 0) 同时满足 left 与 top，必须是 NorthWest 而不是 West/North
        Assert.Equal(GdkWindowEdge.NorthWest, LinuxResizeSupport.ResolveEdge(0, 0, W, H, B));
        Assert.Equal(GdkWindowEdge.SouthEast, LinuxResizeSupport.ResolveEdge(W - 1, H - 1, W, H, B));
    }

    [Fact]
    public void DisabledBorder_NeverResolves()
    {
        Assert.Null(LinuxResizeSupport.ResolveEdge(0, 0, W, H, 0));
        Assert.Null(LinuxResizeSupport.ResolveEdge(0, 0, W, H, -1));
    }

    [Fact]
    public void DegenerateWindow_NeverResolves()
    {
        Assert.Null(LinuxResizeSupport.ResolveEdge(0, 0, 0, 0, B));
        Assert.Null(LinuxResizeSupport.ResolveEdge(0, 0, 10, 0, B));
        Assert.Null(LinuxResizeSupport.ResolveEdge(0, 0, -5, 100, B));
    }

    [Fact]
    public void TinyWindow_ShrinksBorderSoTheCenterStaysClickable()
    {
        // 12x12 的窗口配 5px 热区：不收窄的话左右各 5px 就把整宽吃光，页面一个点都点不到。
        // 收窄到 12/3 = 4 之后，中间仍有 4px 是可点的。
        Assert.Null(LinuxResizeSupport.ResolveEdge(6, 6, 12, 12, B));
        Assert.Equal(GdkWindowEdge.West, LinuxResizeSupport.ResolveEdge(0, 6, 12, 12, B));
        Assert.Equal(GdkWindowEdge.East, LinuxResizeSupport.ResolveEdge(11, 6, 12, 12, B));
    }

    [Fact]
    public void EveryEdge_MapsToADistinctCursor()
    {
        var cursors = Enum.GetValues<GdkWindowEdge>().Select(LinuxResizeSupport.CursorFor).ToArray();

        Assert.Equal(cursors.Length, cursors.Distinct().Count());
    }

    [Fact]
    public void EdgeValues_MatchGdkEnumOrder()
    {
        // 顺序错一位就是"拖右下角却在改左边"，而且只在真机上看得出来
        Assert.Equal(0, (int)GdkWindowEdge.NorthWest);
        Assert.Equal(1, (int)GdkWindowEdge.North);
        Assert.Equal(2, (int)GdkWindowEdge.NorthEast);
        Assert.Equal(3, (int)GdkWindowEdge.West);
        Assert.Equal(4, (int)GdkWindowEdge.East);
        Assert.Equal(5, (int)GdkWindowEdge.SouthWest);
        Assert.Equal(6, (int)GdkWindowEdge.South);
        Assert.Equal(7, (int)GdkWindowEdge.SouthEast);
    }

    [Fact]
    public void CursorValues_MatchGdkConstants()
    {
        // 这几个值直接对应 X 光标字体里的某张图，写错就显示成另一个形状。
        // 尤其注意 TOP_LEFT_CORNER = 134（**不是** 132——132 是 TOP_LEFT_ARROW）。
        Assert.Equal(12, (int)GdkCursorType.BottomLeftCorner);
        Assert.Equal(14, (int)GdkCursorType.BottomRightCorner);
        Assert.Equal(16, (int)GdkCursorType.BottomSide);
        Assert.Equal(70, (int)GdkCursorType.LeftSide);
        Assert.Equal(96, (int)GdkCursorType.RightSide);
        Assert.Equal(134, (int)GdkCursorType.TopLeftCorner);
        Assert.Equal(136, (int)GdkCursorType.TopRightCorner);
        Assert.Equal(138, (int)GdkCursorType.TopSide);
    }
}
