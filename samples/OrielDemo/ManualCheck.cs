using OrielWeb;
using OrielWeb.Ipc;

namespace OrielDemo;

/// <summary>日志条目（宿主 → 页面）。</summary>
public sealed record ManualLog(string Text);

/// <summary>环境状态（页面顶部显示）。</summary>
public sealed record ManualState(
    bool TrayCreated,
    bool TrayVisible,
    bool NotificationsSupported,
    bool AutoStartEnabled);

/// <summary>
/// 手动验证操作台：把各项能力挂到 demo 上，并在**事件回调**里把结果推回页面。
/// </summary>
/// <remarks>
/// 为什么回显到页面而不是打印：demo 在 Windows 上是 <c>WinExe</c>，没有控制台，自检的 stdout 看不见。
/// 而这一批要验证的恰好都是"操作了才有回调"的东西（托盘菜单项、通知点击、快捷键、拖放），
/// 看不见回调就等于没验证。
/// <para>
/// 与 <see cref="ShellSelfTest"/> 的分工：那个是**无人自检**（跑完即退，给 CI 用），
/// 这个是**给人看的**（停在那里等你点）。能力面基本重合，但断言方式不同——
/// 自检断言"不崩且如实报告"，这里靠人眼确认"看得见的行为对不对"。
/// </para>
/// </remarks>
internal static class ManualCheck
{
    private static OrielApp? _app;
    private static WebviewWindow? _window;

    /// <summary>把一条信息推到页面的日志区。</summary>
    internal static void Log(string text)
    {
        try
        {
            _window?.EmitEvent("manual.log", new ManualLog(text), AppJsonContext.Default.ManualLog);
        }
        catch
        {
            // 窗口已销毁时的迟到回调：丢掉即可，不能让日志把业务带崩
        }
    }

    /// <summary>在窗口 <c>Loaded</c> 之后调用（那时 app 与托盘都已就位）。</summary>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        _app = app;
        _window = window;

        // 托盘：这一批里"看得见"的东西大多挂在它上面
        if (app.Tray is { } tray)
        {
            WireTray(tray);
            Log($"托盘已就绪（当前可见：{(tray.IsVisible ? "是" : "否")}）");
        }
        else
        {
            Log("托盘未创建——启动时带上 --manual-check 了吗？");
        }

        // 其余回调：每一项都是"操作了才会出现"的
        app.NotificationClicked += id => Log($"通知被点击：{id}");

        window.ContextMenuItemClicked += id => Log($"上下文菜单项被点击：{id}");
        window.FileDropped += e => Log($"拖入 {e.Paths.Count} 项：{string.Join("  |  ", e.Paths)}");

        Log("操作台就绪：点按钮做动作，托管侧的回调都会出现在这里。");
    }

    /// <summary>
    /// 订阅托盘事件并设置菜单。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是因为<b>重建托盘会造出一个新对象</b>：事件与菜单都不会跟过去，
    /// 必须重新挂一遍（<see cref="OrielApp.RestoreTray"/> 的注释里也写了这一点）。
    /// </remarks>
    internal static void WireTray(OrielTray tray)
    {
        tray.Tooltip = "OrielWeb 手动验证";
        tray.Clicked += () => Log("托盘图标被点击");
        tray.MenuItemClicked += id => Log($"托盘菜单项被点击：{id}");
        tray.SetMenu(TrayMenu());
        // 不再订阅 RawEvent：它一次点击会刷好几行（鼠标移动也回调），把真正的结果淹掉。
        // 库里的这个事件保留着——排查"点了完全没反应"时它仍然是最快的手段。
    }

    /// <summary>托盘菜单：把各形态都摆上一份，点每一项都会回到日志里。</summary>
    internal static IReadOnlyList<OrielMenuItem> TrayMenu() =>
    [
        OrielMenuItem.Item("tray-hello", "Hello（点我看回调）"),
        OrielMenuItem.Separator(),
        new OrielMenuItem { Id = "tray-checked", Label = "勾选项", Checked = true },
        new OrielMenuItem { Id = "tray-disabled", Label = "禁用项（应当点不动）", Enabled = false },
        new OrielMenuItem
        {
            Label = "子菜单（悬停展开）",
            Items = [OrielMenuItem.Item("tray-sub", "子项")],
        },
        OrielMenuItem.Separator(),
        // quit 是托盘里唯一有明确平台语义的 role
        OrielMenuItem.RoleItem(OrielMenuRole.Quit),
    ];
}

/// <summary>操作台按钮对应的命令（页面经 <c>oriel.invoke</c> 调用，返回值直接显示在日志里）。</summary>
public sealed partial class ManualCommands
{
    private static WebviewWindow Window => Program.Window
        ?? throw new InvalidOperationException("窗口尚未创建。");

    private static OrielApp App => Window.App;

    // ---- 环境 ----

    [OrielCommand("manual.state")]
    public static ManualState State() => new(
        TrayCreated: App.Tray is not null,
        TrayVisible: App.Tray?.IsVisible ?? false,
        NotificationsSupported: App.NotificationsSupported,
        AutoStartEnabled: App.IsAutoStartEnabled);

    // ---- 托盘与通知 ----

    [OrielCommand("manual.notify")]
    public static void Notify()
    {
        bool delivered = App.ShowNotification(new OrielNotificationOptions
        {
            Title = "OrielWeb 手动验证",
            Body = "这条没有 Id，点它不会有回调。",
        });

        ManualCheck.Log(delivered
            ? "通知已提交给系统（无 Id）。看不到横幅不是本库的问题——查系统通知设置与专注助手"
            : "通知提交失败：这是实现侧的问题（Windows 上走 PowerShell + WinRT toast，脚本非 0 退出）");
    }

    [OrielCommand("manual.notifyId")]
    public static void NotifyWithId()
    {
        bool delivered = App.ShowNotification(new OrielNotificationOptions
        {
            Title = "点我试试",
            Body = "点击这条通知，日志里应当出现「通知被点击」。",
            Id = "manual-notify",
        });

        ManualCheck.Log(delivered
            ? "通知已提交给系统（带 Id）。注意：Windows 上是 WinRT toast，点击激活尚未实现（未打包应用的限制）"
            : "通知提交失败：这是实现侧的问题（Windows 上走 PowerShell + WinRT toast，脚本非 0 退出）");
    }

    /// <summary>重设托盘菜单（顺带验证 <c>SetMenu</c> 可以重复调用）。</summary>
    [OrielCommand("manual.trayMenu")]
    public static void ResetTrayMenu()
    {
        App.Tray?.SetMenu(ManualCheck.TrayMenu());
        ManualCheck.Log("托盘菜单已重设（应当还能弹出同样的菜单）");
    }

    /// <summary>
    /// 移除托盘图标（<c>Shell_NotifyIcon(NIM_DELETE)</c>）。
    /// </summary>
    /// <remarks>
    /// 这一项的看点不是"图标没了"，而是**图标没了而进程还在**——Windows 上真正难查的是反过来的
    /// 那种：进程退出后图标还留在通知区（幽灵图标），那是没调 <c>NIM_DELETE</c> 的典型症状。
    /// 点它、再点"重建"，可以来回验证原生资源确实被撤销又建立。
    /// </remarks>
    [OrielCommand("manual.trayRemove")]
    public static bool TrayRemove()
    {
        bool removed = App.RemoveTray();
        ManualCheck.Log(removed
            ? "托盘已移除：通知区的图标应当立刻消失。若图标还在，那正是要抓的幽灵图标"
            : "本来就没有托盘可移除");
        return removed;
    }

    /// <summary>按构建期选项重建托盘，并重新挂上事件与菜单。</summary>
    [OrielCommand("manual.trayRestore")]
    public static bool TrayRestore()
    {
        if (App.RestoreTray() is not { } tray)
        {
            ManualCheck.Log("重建失败：本次启动没有用 AddTray 配置过托盘");
            return false;
        }

        // 重建出来的是新对象，事件与菜单都得重挂
        ManualCheck.WireTray(tray);
        ManualCheck.Log($"托盘已重建（可见：{(tray.IsVisible ? "是" : "否")}）：图标应重新出现，菜单也应能弹出");
        return true;
    }

    // ---- 上下文菜单 ----

    [OrielCommand("manual.contextMenu")]
    public static void ShowContextMenu()
    {
        ManualCheck.Log("上下文菜单已弹出（Windows 上会阻塞到选择或取消，属原生行为）");
        Window.ShowContextMenu(
        [
            OrielMenuItem.Item("ctx-hello", "Hello from context menu"),
            OrielMenuItem.Separator(),
            new OrielMenuItem { Id = "ctx-checked", Label = "勾选项", Checked = true },
            new OrielMenuItem { Id = "ctx-disabled", Label = "禁用项", Enabled = false },
            new OrielMenuItem
            {
                Label = "子菜单",
                Items = [OrielMenuItem.Item("ctx-sub", "子项")],
            },
            OrielMenuItem.Separator(),
            OrielMenuItem.RoleItem(OrielMenuRole.Copy),
            OrielMenuItem.RoleItem(OrielMenuRole.Close),
        ]);
    }

    // ---- 内建右键菜单（渲染引擎自己弹的那个，不是 ShowContextMenu）----

    [OrielCommand("manual.cmEditing")]
    public static void ContextMenuEditing() => SetContextMenuPolicy(OrielContextMenuPolicy.Editing);

    [OrielCommand("manual.cmNative")]
    public static void ContextMenuNative() => SetContextMenuPolicy(OrielContextMenuPolicy.Native);

    [OrielCommand("manual.cmDisabled")]
    public static void ContextMenuDisabled() => SetContextMenuPolicy(OrielContextMenuPolicy.Disabled);

    private static void SetContextMenuPolicy(OrielContextMenuPolicy policy)
    {
        foreach (WebviewWindow window in App.Windows)
        {
            window.ContextMenuPolicy = policy;
        }

        ManualCheck.Log($"内建右键菜单策略 → {policy}：在页面任意处（或下面那个输入框里）右键看效果");
    }

    // ---- 对话框 ----

    [OrielCommand("manual.messageBox")]
    public static void MessageBox()
        => Window.ShowMessage(
            "这是一条消息框。\n\n它会阻塞消息循环直到你关掉它——模态对话框的固有行为。",
            "手动验证",
            OrielMessageBoxIcon.Info);

    [OrielCommand("manual.openSingle")]
    public static string OpenSingle()
        => Window.ShowOpenFileDialog(new OrielOpenFileDialogOptions
        {
            Title = "打开一个文件",
            Filters = [OrielFileFilter.Of("文本文件", "*.txt", "*.md", "*.log"), OrielFileFilter.AllFiles],
        }) is [var only, ..] ? only : "（取消）";

    [OrielCommand("manual.openMultiple")]
    public static string OpenMultiple()
    {
        string[] paths = Window.ShowOpenFileDialog(new OrielOpenFileDialogOptions
        {
            Title = "打开多个文件（按住 Ctrl 多选）",
            Filters = [OrielFileFilter.Of("文本文件", "*.txt", "*.md", "*.log"), OrielFileFilter.AllFiles],
            AllowMultiple = true,
        });

        if (paths.Length == 0)
        {
            return "（取消）";
        }

        ManualCheck.Log($"多选返回 {paths.Length} 项");
        return string.Join("  |  ", paths);
    }

    [OrielCommand("manual.save")]
    public static string Save()
        => Window.ShowSaveFileDialog(new OrielSaveFileDialogOptions
        {
            Title = "保存为（输入不带扩展名的名字，落盘应自动补 .txt）",
            Filters = [OrielFileFilter.Of("文本文件", "*.txt"), OrielFileFilter.AllFiles],
            DefaultExtension = "txt",
            DefaultFileName = "手动验证",
        }) ?? "（取消）";

    [OrielCommand("manual.pickFolder")]
    public static string PickFolder() => Window.ShowFolderDialog("选一个文件夹") ?? "（取消）";

    // ---- Shell ----

    [OrielCommand("manual.openExternal")]
    public static bool OpenExternal() => App.OpenExternal("https://example.com/oriel-manual-check");

    [OrielCommand("manual.revealSelf")]
    public static bool RevealSelf()
        => Environment.ProcessPath is { } path && App.RevealInFileManager(path);

    [OrielCommand("manual.openSelf")]
    public static bool OpenSelf()
        => Environment.ProcessPath is { } path && App.OpenWithDefaultApp(path);

    /// <summary>
    /// 危险目标必须**全部被拒**：返回 true 表示安全边界正确（这批里最该看的一项）。
    /// </summary>
    /// <remarks>
    /// 这里用的都是"按策略绝不该放行"的输入：非 http(s) 协议（<c>file:</c>/<c>javascript:</c>）、
    /// 裸路径（不是 URL）、相对路径与空串（不是可用的绝对路径）。
    /// 注意**不能**拿"存在且绝对的可执行文件"来测——那种输入按设计是放行的，
    /// 用它做断言只会得到一个看起来像"边界坏了"的假警报。
    /// </remarks>
    [OrielCommand("manual.dangerous")]
    public static bool RejectedDangerous()
        => !App.OpenExternal("file:///etc/passwd")
            && !App.OpenExternal("/etc/passwd")
            && !App.OpenExternal("javascript:alert(1)")
            && !App.RevealInFileManager("relative/path.txt")
            && !App.RevealInFileManager("")
            && !App.OpenWithDefaultApp("relative/path.txt");

    // ---- 开机自启 ----

    [OrielCommand("manual.autoStartOn")]
    public static bool AutoStartOn()
    {
        bool ok = App.EnableAutoStart(["--minimized"]);
        ManualCheck.Log(ok ? "自启已启用（可在 regedit 的 HKCU\\...\\Run 下核对）" : "自启启用失败");
        return ok;
    }

    [OrielCommand("manual.autoStartQuery")]
    public static bool AutoStartQuery() => App.IsAutoStartEnabled;

    [OrielCommand("manual.autoStartOff")]
    public static bool AutoStartOff()
    {
        bool ok = App.DisableAutoStart();
        ManualCheck.Log(ok ? "自启已禁用（项已从注册表移除）" : "自启禁用失败");
        return ok;
    }
}
