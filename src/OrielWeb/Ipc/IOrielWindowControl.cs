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
    /// <summary>
    /// 本窗口所属的应用。
    /// </summary>
    /// <remarks>
    /// 内建 <c>tray.*</c> 命令要用它：托盘是**应用级**资源（不属于任何窗口），
    /// 但命令是从某个窗口的页面发起的，只能经这个属性拿到应用。
    /// </remarks>
    OrielApp App { get; }

    /// <summary>窗口当前是否"显示中"（供内建 <c>win.isVisible</c> 查询）。</summary>
    bool IsVisible { get; }

    /// <summary>
    /// 本窗口的 label。
    /// </summary>
    /// <remarks>
    /// <c>window.*</c> 族要用它算**默认目标**：命令的 <c>label</c> 参数缺省时作用于发起者，
    /// 而"发起者是谁"只能经这个属性问出来——IPC 层没有别的途径知道来源窗口的身份。
    /// </remarks>
    string? Label { get; }

    void Minimize();

    bool ToggleMaximize();

    void Close();

    /// <summary>强制销毁窗口：绕过 <c>Closing</c> 与页面接管（对标 Tauri 2 的 <c>destroy</c>）。</summary>
    void Destroy();

    /// <summary>
    /// 是否让页面接管关闭；页面侧用它实现"确认后关闭"。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Destroy"/> 同属"关闭"这一族能力，因此也归在 <c>win.*</c> 的放行名单里。
    /// 它只能影响**自己这个窗口**，且最坏后果是"把窗口变得更难关"（自伤而非越权）——
    /// 应用始终可以从宿主侧关掉它。
    /// </remarks>
    bool CloseInterceptEnabled { get; set; }

    bool ToggleFullscreen();

    bool ToggleOnTop();

    void BeginDrag();

    void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh);

    void DragTo(double dx, double dy);

    void EndDrag();

    string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory);

    void ShowContextMenu(IReadOnlyList<OrielMenuItem> items);
}
