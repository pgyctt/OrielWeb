namespace OrielWeb;

/// <summary>
/// 拖放事件的阶段（对标 Tauri 的 <c>DragDropEvent.type</c>）。
/// </summary>
public enum OrielDragEventType
{
    /// <summary>进入窗口：载荷已解析。</summary>
    Enter,

    /// <summary>在窗口内移动：只有坐标。</summary>
    Over,

    /// <summary>在窗口内松手：载荷已解析。</summary>
    Drop,

    /// <summary>离开窗口：没有坐标，也没有载荷。</summary>
    Leave,
}

/// <summary>
/// 一次拖放事件（宿主侧）。
/// </summary>
/// <remarks>
/// <b>坐标是客户区物理像素</b>（左上原点）。这一点与 <c>window.cursorPosition</c> 不同——
/// 那个给的是屏幕坐标。拖放要画的是"在窗口里的哪里"，用屏幕坐标还得再减一次窗口位置。
/// </remarks>
public sealed class OrielDragEventArgs(OrielDragEventType type, IReadOnlyList<string> paths, int x, int y)
    : EventArgs
{
    /// <summary>事件阶段。</summary>
    public OrielDragEventType Type { get; } = type;

    /// <summary>本地文件系统路径；<see cref="OrielDragEventType.Over"/> 与 <see cref="OrielDragEventType.Leave"/> 时为空。</summary>
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>客户区物理像素的 X（<see cref="OrielDragEventType.Leave"/> 时无意义，为 0）。</summary>
    public int X { get; } = x;

    /// <summary>客户区物理像素的 Y（<see cref="OrielDragEventType.Leave"/> 时无意义，为 0）。</summary>
    public int Y { get; } = y;
}
