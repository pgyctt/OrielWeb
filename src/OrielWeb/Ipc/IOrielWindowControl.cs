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
    /// 本窗口的缩放比（物理像素 / 逻辑像素）。
    /// </summary>
    /// <remarks>
    /// <c>menu.popup</c> 要用它把页面给的**物理**坐标换成宿主认的**逻辑**坐标。
    /// 换算放在命令层而不是各平台后端：三份实现各写一次换算会得到三个略有差异的舍入。
    /// </remarks>
    double ScaleFactor { get; }

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

    /// <summary>
    /// 打开文件对话框（**收 options**，支持多选与过滤器）。
    /// </summary>
    /// <remarks>
    /// 与上面那个"宽松签名"重载并存：那个是 <c>win.pickFile</c> 用的既有契约
    /// （只收一个过滤器字符串、返回单个路径），**冻结不动**；
    /// 这个是 <c>dialog.open</c> 用的新入口。两者共用底下的原生对话框。
    /// </remarks>
    string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options);

    /// <summary>保存文件对话框（<c>dialog.save</c> 用）。</summary>
    string? ShowSaveFileDialog(OrielSaveFileDialogOptions options);

    /// <summary>目录选择对话框（<c>dialog.open({ directory: true })</c> 用）。</summary>
    string? ShowFolderDialog(string? title, string? initialDirectory);

    /// <summary>询问型消息框（<c>dialog.message</c> 用），返回用户点了哪个语义按钮。</summary>
    MessageResult? ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel);

    void ShowContextMenu(IReadOnlyList<OrielMenuItem> items);

    /// <summary>
    /// 在指定位置弹出上下文菜单（<c>menu.popup</c> 用）。
    /// </summary>
    /// <param name="items">菜单树。</param>
    /// <param name="position">
    /// 弹出位置（**逻辑像素**，相对窗口客户区左上角）；<c>null</c> 表示鼠标位置。
    /// </param>
    /// <param name="autoDismissAfter">
    /// 自动关闭延时；<c>null</c> 表示等用户选择。
    /// </param>
    /// <remarks>
    /// 放在接口上而不是让命令层直接摸 <see cref="WebviewWindow"/> 的重载：命令层只认这个窄接口，
    /// 否则它就得依赖具体窗口类型——那是它一直在避免的事，也会让"菜单树解析"这段逻辑
    /// 在单测里完全够不着（假窗口实现不了 <see cref="WebviewWindow"/> 的额外重载）。
    /// </remarks>
    void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null);

    /// <summary>
    /// 关掉当前正在显示的上下文菜单（<c>menu.close</c> 用）。
    /// </summary>
    /// <remarks>
    /// 必须在接口上出现而不是让命令层直接摸 <see cref="WebviewWindow"/>：命令层只认这个窄接口，
    /// 否则它就得依赖具体窗口类型（那是它一直在避免的事）。
    /// </remarks>
    void CloseContextMenu();
}
