namespace OrielWeb;

/// <summary>外部文件被拖进窗口时触发。</summary>
/// <remarks>
/// 只给路径、不给落点坐标：三平台的坐标系与 y 轴方向各不相同（Cocoa 的原点在左下、
/// GTK 的 y 轴向下、Win32 的客户区坐标还要算进 DPI 缩放），要一致就得再引入一层坐标换算，
/// 而拖放最常见的用途（导入文件）根本不需要它。需要落点做视觉反馈的场合，
/// 页面自己的 <c>dragover</c> 事件就能拿到位置——路径才必须来自原生侧（浏览器的安全模型不给页面路径）。
/// 位置作为待补项记在 <c>docs/ROADMAP.md</c>。
/// </remarks>
public sealed class OrielFileDropEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    /// <summary>
    /// 拖入项的**本地文件系统路径**（不是 <c>file://</c> URI）。目录也可能出现在其中（拖文件夹是允许的）。
    /// </summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}
