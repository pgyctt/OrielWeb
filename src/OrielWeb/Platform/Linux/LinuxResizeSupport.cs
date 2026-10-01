namespace OrielWeb.Platform.Linux;

/// <summary>
/// GDK 的窗口边缘（<c>GdkWindowEdge</c>）。
/// </summary>
/// <remarks>
/// 数值必须与 GDK 的枚举**逐一对应**——它会被直接当整数传给
/// <c>gtk_window_begin_resize_drag</c>，错一位就是"拖右下角却在改左边"。
/// 取值来自 GTK3 头文件（<c>gdk/gdkwindow.h</c>）：NW=0、N=1、NE=2、W=3、E=4、SW=5、S=6、SE=7。
/// </remarks>
internal enum GdkWindowEdge
{
    NorthWest = 0,
    North = 1,
    NorthEast = 2,
    West = 3,
    East = 4,
    SouthWest = 5,
    South = 6,
    SouthEast = 7,
}

/// <summary>
/// 边缘 resize 用到的 <c>GdkCursorType</c> 成员。
/// </summary>
/// <remarks>
/// 这个枚举的值**不连续**（照抄自 X 光标字体），因此只能把用到的几个显式写出来。
/// 数值取自 GTK3 的 <c>gdk/gdkcursor.h</c>；注意 <c>TOP_LEFT_CORNER = 134</c> 而不是 132——
/// 132 是 <c>TOP_LEFT_ARROW</c>，差这两位就会显示成另一个形状。
/// </remarks>
internal enum GdkCursorType
{
    BottomLeftCorner = 12,
    BottomRightCorner = 14,
    BottomSide = 16,
    LeftSide = 70,
    RightSide = 96,
    TopLeftCorner = 134,
    TopRightCorner = 136,
    TopSide = 138,
}

/// <summary>
/// 无边框窗口的"边缘命中"判定（Linux/GTK）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：窗口一旦 <c>gtk_window_set_decorated(false)</c>，窗口管理器就**不再提供 resize 边框**，
/// 于是"拖边缘改大小"整个失效（Ubuntu 22.04 实测确认，见 ROADMAP 的「已确认的缺陷」）。
/// 系统不会白送这份能力，只能自己判命中再把 resize 交回给 WM/合成器。
/// </para>
/// <para>
/// 抽成纯函数是为了**能被测到**：真正的动作（<c>gtk_window_begin_resize_drag</c>）只有真机验得了，
/// 而"这个坐标算不算边缘、算哪条边"是它可断言的那一半。判定写错的后果很具体——
/// 要么边缘没反应，要么把页面一片区域的点击全吃掉，而这两种都只在真机上才看得出来。
/// </para>
/// </remarks>
internal static class LinuxResizeSupport
{
    /// <summary>边缘热区厚度（GTK 逻辑像素；HiDPI 由 GTK 自己折算）。</summary>
    internal const int BorderThickness = 5;

    /// <summary>
    /// 判定 (<paramref name="x"/>, <paramref name="y"/>) 落在窗口的哪条边/哪个角上；不在边缘时返回 null。
    /// </summary>
    /// <param name="x">指针在窗口内的横坐标（逻辑像素，原点在左上）。</param>
    /// <param name="y">指针在窗口内的纵坐标（逻辑像素，原点在左上）。</param>
    /// <param name="width">窗口宽（逻辑像素）。</param>
    /// <param name="height">窗口高（逻辑像素）。</param>
    /// <param name="border">热区厚度；&lt;= 0 表示不启用边缘 resize。</param>
    internal static GdkWindowEdge? ResolveEdge(double x, double y, int width, int height, int border)
    {
        if (border <= 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        // 窗口很小时把热区收窄：否则左右两条各 5px 就把整个宽度吃掉，页面一个点都点不到。
        // 取 1/3 是为了保证中间至少还剩 1/3 宽度是可点的。
        int limit = Math.Max(1, Math.Min(border, Math.Min(width, height) / 3));

        bool left = x < limit;
        bool right = x >= width - limit;
        bool top = y < limit;
        bool bottom = y >= height - limit;

        // 先判角再判边：角是两个条件同时成立，switch 的顺序保证了这一点
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => GdkWindowEdge.NorthWest,
            (true, _, _, true) => GdkWindowEdge.SouthWest,
            (_, true, true, _) => GdkWindowEdge.NorthEast,
            (_, true, _, true) => GdkWindowEdge.SouthEast,
            (true, _, _, _) => GdkWindowEdge.West,
            (_, true, _, _) => GdkWindowEdge.East,
            (_, _, true, _) => GdkWindowEdge.North,
            (_, _, _, true) => GdkWindowEdge.South,
            _ => null,
        };
    }

    /// <summary>该边缘对应的鼠标指针形状。</summary>
    internal static GdkCursorType CursorFor(GdkWindowEdge edge) => edge switch
    {
        GdkWindowEdge.NorthWest => GdkCursorType.TopLeftCorner,
        GdkWindowEdge.North => GdkCursorType.TopSide,
        GdkWindowEdge.NorthEast => GdkCursorType.TopRightCorner,
        GdkWindowEdge.West => GdkCursorType.LeftSide,
        GdkWindowEdge.East => GdkCursorType.RightSide,
        GdkWindowEdge.SouthWest => GdkCursorType.BottomLeftCorner,
        GdkWindowEdge.South => GdkCursorType.BottomSide,
        _ => GdkCursorType.BottomRightCorner,
    };
}
