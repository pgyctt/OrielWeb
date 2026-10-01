namespace OrielWeb.Ipc;

/// <summary>
/// 内建窗口命令（<c>win.*</c>）需要的那部分窗口能力。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WebviewWindow"/> 实现它（成员都已在那个类上，是公开 API），因此三平台后端把
/// 自己的窗口交给分发器即可；测试用一个轻量假实现替代，不必造一个真的窗口后端。
/// </para>
/// <para>
/// 之所以不让分发器直接依赖 <see cref="WebviewWindow"/>：那会把"IPC 分发"与"窗口实现"
/// 绑死，而这里真正需要的只有十几个方法。
/// </para>
/// </remarks>
internal interface IOrielWindowControl
{
    void Minimize();

    bool ToggleMaximize();

    void Close();

    bool ToggleFullscreen();

    bool ToggleOnTop();

    void BeginDrag();

    void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh);

    void DragTo(double dx, double dy);

    void EndDrag();

    string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory);

    void ShowContextMenu(IReadOnlyList<OrielMenuItem> items);
}
