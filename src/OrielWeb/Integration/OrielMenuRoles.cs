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
    /// 按 role 执行；返回 false 表示这一项没人处理（调用方应当忽略它，而不是抛异常——
    /// "某个 role 在该平台无法实现"不该让整个菜单失效）。
    /// </summary>
    internal static bool TryActivate(string role, OrielApp app, WebviewWindow? window)
    {
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

            default:
                return false;
        }
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
