using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建窗口命令（<c>win.*</c>）：无边框窗口的标题栏按钮与拖动由**库**实现，应用不必注册任何命令。
/// </summary>
/// <remarks>
/// <para>
/// 这一组命令此前要求每个应用自己写一遍（demo 里的 <c>WindowCommands</c> 就是那样一份参考实现）。
/// 但它们的实现只依赖窗口本身，而且要正确就得吸收三平台的差异——Windows 的原生模态拖动会吞掉后续点击、
/// Wayland 下窗口移动必须交给合成器、双击必须在 <c>mousedown</c> 内同步判定……让每个应用各写一遍，
/// 等于让每个应用各踩一遍。所以现在由库提供：应用侧一行不写，页面侧只标一个拖动区域（见 API.md 第 2 节）。
/// </para>
/// <para>
/// <b>内建先于应用路由</b>：命中这里的名字不会再走应用注册的命令，因此应用自定义的 <c>win.*</c>
/// 会被遮蔽。<c>win.</c> 本来就是保留前缀（见 <see cref="OrielCapabilityRules.BuiltInCommandPrefix"/>），
/// 现在这一点由"名字被库占用"落到了实处。
/// </para>
/// </remarks>
internal static class OrielBuiltInWindowCommands
{
    /// <summary>取某个 <c>win.*</c> 命令的处理函数；不是内建命令时返回 false。</summary>
    /// <param name="name">命令名（区分大小写，与命令授权用的是同一套 Ordinal 比较）。</param>
    /// <param name="handler">处理函数：窗口 + args → 回执里的 value。</param>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        switch (name)
        {
            case "win.minimize":
                handler = static (window, _) => Mutate(window.Minimize);
                return true;

            case "win.toggleMaximize":
                // 返回值是**意图值**：页面用它驱动最大化图标，真实状态随后由宿主上报纠正
                handler = static (window, _) => window.ToggleMaximize();
                return true;

            case "win.close":
                handler = static (window, _) => Mutate(window.Close);
                return true;

            case "win.toggleFullscreen":
                handler = static (window, _) => window.ToggleFullscreen();
                return true;

            case "win.toggleOnTop":
                handler = static (window, _) => window.ToggleOnTop();
                return true;

            case "win.drag":
                // Windows：进入原生模态拖动（阻塞到松开鼠标）；macOS/Linux 上是 no-op
                handler = static (window, _) => Mutate(window.BeginDrag);
                return true;

            case "win.dragStart":
                handler = static (window, args) =>
                {
                    const string Command = "win.dragStart";
                    window.BeginDragStreaming(
                        Number(args, "px", Command),
                        Number(args, "py", Command),
                        Number(args, "wx", Command),
                        Number(args, "wy", Command),
                        Number(args, "ww", Command),
                        Number(args, "wh", Command),
                        Number(args, "sh", Command));
                    return null;
                };
                return true;

            case "win.dragTo":
                handler = static (window, args) =>
                {
                    const string Command = "win.dragTo";
                    window.DragTo(Number(args, "dx", Command), Number(args, "dy", Command));
                    return null;
                };
                return true;

            case "win.dragEnd":
                handler = static (window, _) => Mutate(window.EndDrag);
                return true;

            case "win.pickFile":
                // 标题与过滤器可选：不给就用库自己的默认值
                handler = static (window, args) =>
                    window.ShowOpenFileDialog(Text(args, "title"), Text(args, "filter"), null);
                return true;

            case "win.contextMenu":
                handler = static (window, _) => Mutate(() => window.ShowContextMenu(StandardContextMenu()));
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    private static object? Mutate(Action action)
    {
        action();
        return null;
    }

    /// <summary>
    /// 内建上下文菜单：编辑三项 + 全选 + 关闭。
    /// </summary>
    /// <remarks>
    /// 只放**跨应用通用**的项——应用自己的菜单项（业务动作、勾选项）仍应由应用用
    /// <see cref="WebviewWindow.ShowContextMenu"/> 自己弹，那是它才知道的内容。
    /// 编辑类 role 是尽力而为的（见 <see cref="OrielMenuRoles"/> 的说明）。
    /// </remarks>
    private static IReadOnlyList<OrielMenuItem> StandardContextMenu() =>
    [
        OrielMenuItem.RoleItem(OrielMenuRole.Cut),
        OrielMenuItem.RoleItem(OrielMenuRole.Copy),
        OrielMenuItem.RoleItem(OrielMenuRole.Paste),
        OrielMenuItem.Separator(),
        OrielMenuItem.RoleItem(OrielMenuRole.SelectAll),
        OrielMenuItem.Separator(),
        OrielMenuItem.RoleItem(OrielMenuRole.Close),
    ];

    /// <summary>读一个必给的数字参数；缺了或不是数字都报错——静默当 0 会让拖动跳到屏幕左上角。</summary>
    private static double Number(JsonElement args, string property, string commandName)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number)
        {
            return element.GetDouble();
        }

        throw new OrielIpcException($"命令 '{commandName}' 缺少数字参数 '{property}'。");
    }

    /// <summary>读一个可选字符串参数。</summary>
    private static string? Text(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
