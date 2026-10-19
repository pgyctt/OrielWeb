namespace OrielWeb;

/// <summary>
/// 菜单 role 的平台无关解释：把 <see cref="OrielMenuRole"/> 的语义落到应用/窗口能力上。
/// </summary>
/// <remarks>
/// <para>
/// 集中在一处，而不是各平台各写一遍：role 的**语义**（"quit 是退出应用"）与平台无关，
/// 三平台不同的只是"菜单怎么画出来、选择怎么报回来"。
/// macOS 本可以让 AppKit 走响应链（nil target）白拿系统标准行为，但那样三平台的 role 行为会分叉，
/// 而响应链能做的恰好就是这几个动作；统一走这里，代价只是 macOS 上 role 项的文案要自己给
/// （已由各后端的默认文案表覆盖）。
/// </para>
/// <para>
/// <b>编辑类 role 是尽力而为</b>：它们经 <c>document.execCommand</c> 交给页面执行
/// （webview 没有"宿主侧选区"这回事）。其中 <c>copy</c>/<c>selectAll</c>/<c>undo</c>/<c>redo</c>
/// 通常可用，<c>cut</c>/<c>paste</c> 在多数 webview 里会被安全策略拦下——需要它们时应当由页面
/// 自己监听按键并处理，而不是依赖菜单项。
/// </para>
/// </remarks>
internal static class OrielMenuRoles
{
    /// <summary>
    /// 是否是编辑类 role（撤销/重做/剪切/复制/粘贴/全选/删除）。
    /// </summary>
    /// <remarks>
    /// 这几项与渲染引擎的"编辑命令"一一对应。接管式的内建右键菜单要弹的就是它们，
    /// 且必须走引擎自己的命令（见 <see cref="TryActivate"/> 的 nativeEditing）。
    /// </remarks>
    internal static bool IsEditingRole(string role) => role is
        OrielMenuRole.Undo or OrielMenuRole.Redo or OrielMenuRole.Cut or
        OrielMenuRole.Copy or OrielMenuRole.Paste or OrielMenuRole.SelectAll or
        OrielMenuRole.Delete;

    /// <summary>
    /// <see cref="OrielContextMenuPolicy.Editing"/> 下接管式菜单要弹的内容：只有剪切 / 复制 / 粘贴。
    /// </summary>
    /// <remarks>
    /// 顺序按各平台惯例（剪切、复制、粘贴）。文案由 <see cref="DefaultLabel"/> 提供，即英文
    /// Cut/Copy/Paste —— 与 Windows 内建菜单一致，也不假装本地化。
    /// </remarks>
    internal static IReadOnlyList<OrielMenuItem> EditingMenuItems() =>
    [
        OrielMenuItem.RoleItem(OrielMenuRole.Cut),
        OrielMenuItem.RoleItem(OrielMenuRole.Copy),
        OrielMenuItem.RoleItem(OrielMenuRole.Paste),
    ];

    /// <summary>
    /// 按 role 执行；返回 false 表示这一项没人处理（调用方应当忽略它，而不是抛异常——
    /// "某个 role 在该平台无法实现"不该让整个菜单失效）。
    /// </summary>
    /// <param name="role">菜单项的 role 名，取值见 <see cref="OrielMenuRole"/>。</param>
    /// <param name="app">应用门面（应用级 role 需要它，如 quit）。</param>
    /// <param name="window">发起动作的窗口；为 null 时窗口级与编辑类 role 都判定为"没人处理"。</param>
    /// <param name="nativeEditing">
    /// 宿主侧的"原生编辑命令"通道：接管式菜单弹出的剪切/复制/粘贴必须走它，因为
    /// <c>document.execCommand('cut'/'paste')</c> 会被 webview 的安全策略拦下（粘贴还要把系统
    /// 剪贴板送进页面，只有原生侧能做）。传 null 时退回 <c>execCommand</c>（尽力而为）。
    /// </param>
    internal static bool TryActivate(string role, OrielApp app, WebviewWindow? window,
        Func<string, bool>? nativeEditing = null)
    {
        if (nativeEditing is not null && IsEditingRole(role) && nativeEditing(role))
        {
            return true;
        }

        switch (role)
        {
            case OrielMenuRole.Quit:
                app.Quit();
                return true;

            case OrielMenuRole.Close:
                window?.Close();
                return window is not null;

            case OrielMenuRole.Minimize:
                window?.Minimize();
                return window is not null;

            case OrielMenuRole.Zoom:
                window?.ToggleMaximize();
                return window is not null;

            case OrielMenuRole.ToggleFullScreen:
                window?.ToggleFullscreen();
                return window is not null;

            case OrielMenuRole.Undo:
                return ExecCommand(window, "undo");

            case OrielMenuRole.Redo:
                return ExecCommand(window, "redo");

            case OrielMenuRole.Cut:
                return ExecCommand(window, "cut");

            case OrielMenuRole.Copy:
                return ExecCommand(window, "copy");

            case OrielMenuRole.Paste:
                return ExecCommand(window, "paste");

            case OrielMenuRole.SelectAll:
                return ExecCommand(window, "selectAll");

            case OrielMenuRole.Delete:
                return ExecCommand(window, "delete");

            case OrielMenuRole.About:
                return ShowAbout(app, window);

            default:
                return false;
        }
    }

    /// <summary>
    /// "关于"：弹一个含应用名与版本的宿主消息框。
    /// </summary>
    /// <remarks>
    /// <para>
    /// macOS 本可以走 <c>orderFrontStandardAboutPanel:</c>（系统标准面板，还会自动读
    /// Info.plist 的图标与版权），但那在 Windows/Linux 上没有对应物——于是三平台会给出
    /// 三种不同的"关于"体验，而这正是 <see cref="OrielMenuRoles"/> 存在要避免的分叉。
    /// 统一用一个消息框：信息少一点（没有图标/版权），但三平台一致、且用的是应用自己
    /// <see cref="OrielApp.Info"/> 里的名字与版本。
    /// </para>
    /// <para>
    /// 没有窗口时返回 false（调用方忽略该项）：消息框是**模态于某个窗口**的，
    /// 没有窗口就没有可挂的父窗口，弹一个游离的对话框不如不弹。
    /// </para>
    /// </remarks>
    private static bool ShowAbout(OrielApp app, WebviewWindow? window)
    {
        if (window is null)
        {
            return false;
        }

        string name = app.Info.Name;
        string version = app.Info.Version;

        _ = window.ShowDialog($"{name} {version}", $"关于 {name}", "info", MessageButtons.Ok, "确定", "取消");
        return true;
    }

    private static bool ExecCommand(WebviewWindow? window, string command)
    {
        if (window is null)
        {
            return false;
        }

        // 结果由页面侧决定（可能被安全策略拒绝），这里只负责把命令投进去
        _ = window.EvaluateJs($"document.execCommand('{command}')");
        return true;
    }

    /// <summary>
    /// role 项的默认文案（未显式给 <see cref="OrielMenuItem.Label"/> 时使用）。
    /// 用英文而不是本地化字符串：库没有语言包，硬编中文对非中文应用更糟；要本地化就自己给 Label。
    /// </summary>
    internal static string? DefaultLabel(string? role) => role switch
    {
        OrielMenuRole.Quit => "Quit",
        OrielMenuRole.Close => "Close",
        OrielMenuRole.Minimize => "Minimize",
        OrielMenuRole.Zoom => "Zoom",
        OrielMenuRole.ToggleFullScreen => "Full Screen",
        OrielMenuRole.About => "About",
        OrielMenuRole.Undo => "Undo",
        OrielMenuRole.Redo => "Redo",
        OrielMenuRole.Cut => "Cut",
        OrielMenuRole.Copy => "Copy",
        OrielMenuRole.Paste => "Paste",
        OrielMenuRole.SelectAll => "Select All",
        OrielMenuRole.Delete => "Delete",
        _ => null,
    };
}
