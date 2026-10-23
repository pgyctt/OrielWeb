using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>原生对话框（打开/保存/选目录/消息框）。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        // NSAlert：alert = [[NSAlert alloc] init]; setAlertStyle; setMessageText; setInformativeText; runModal
        var alert = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSAlert"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));
        var style = icon switch
        {
            OrielMessageBoxIcon.Warning => 1, // NSAlertStyleWarning
            OrielMessageBoxIcon.Error => 2,   // NSAlertStyleCritical
            _ => 0,                            // NSAlertStyleInformational
        };
        ObjCRuntime.SendVoidNint(alert, ObjCRuntime.Sel("setAlertStyle:"), style);
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setMessageText:"), ObjCRuntime.MakeNSString(title ?? _title));
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setInformativeText:"), ObjCRuntime.MakeNSString(text));
        _ = ObjCRuntime.SendId(alert, ObjCRuntime.Sel("runModal"));
    }

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cocoa 这边**支持自定义按钮文案**（<c>addButtonWithTitle:</c> 直接收文本），
    /// 所以 Windows 的限制在 macOS 上不存在。
    /// </para>
    /// <para>
    /// <c>addButtonWithTitle:</c> 的返回值是<b>按钮索引 + 1</b>（1 = 第一个按钮），
    /// 而 <c>runModal</c> 返回的 <c>NSModalResponse</c> 用的就是同一套编号。
    /// 所以顺序必须与下面的映射一致，而"哪个是肯定性按钮"由**添加顺序**决定
    /// （第一个添加的也是回车默认触发的那个）。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        nint alert = ObjCRuntime.SendId(
            ObjCRuntime.SendId(ObjCRuntime.GetClass("NSAlert"), ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("init"));

        if (alert == 0)
        {
            return null;
        }

        int style = kind switch
        {
            "warning" => 1,  // NSAlertStyleWarning
            "error" => 2,    // NSAlertStyleCritical
            _ => 0,          // NSAlertStyleInformational
        };

        ObjCRuntime.SendVoidNint(alert, ObjCRuntime.Sel("setAlertStyle:"), style);
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setMessageText:"), ObjCRuntime.MakeNSString(title ?? _title));
        ObjCRuntime.SendVoidObj(alert, ObjCRuntime.Sel("setInformativeText:"), ObjCRuntime.MakeNSString(message));

        AddButton(alert, okLabel);

        if (buttons is MessageButtons.OkCancel or MessageButtons.YesNo or MessageButtons.YesNoCancel)
        {
            AddButton(alert, cancelLabel);
        }

        if (buttons is MessageButtons.YesNoCancel)
        {
            // 第三个按钮没有独立文案参数（Tauri 的 yes/no/cancel 也没有）——用惯例的"取消"
            AddButton(alert, "取消");
        }

        nint response = ObjCRuntime.SendId(alert, ObjCRuntime.Sel("runModal"));

        // 响应码 = 按钮索引 + 1。第 1 个按钮的语义取决于按钮集合：
        // Ok / OkCancel 是"确认"，YesNo / YesNoCancel 是"是"。
        bool firstIsOk = buttons is MessageButtons.Ok or MessageButtons.OkCancel;

        return response switch
        {
            1 => firstIsOk ? MessageResult.Ok : MessageResult.Yes,
            // 第 2 个按钮：OkCancel 是"取消"，YesNo 与 YesNoCancel 都是"否"。
            // **不能把 YesNoCancel 的第 2 个当成取消**：调用方要能区分"用户选了否"
            // 与"用户放弃了"，而那正是三按钮的用途。
            2 when buttons is MessageButtons.OkCancel => MessageResult.Cancel,
            2 => MessageResult.No,
            3 => MessageResult.Cancel,
            _ => null,                    // 被外部关掉（Esc / 关窗）时没有结果
        };
    }

    // ---- 文件对话框（NSOpenPanel / NSSavePanel）----

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
    {
        nint panel = CreatePanel(isSave: false, options.Title, options.Filters, options.InitialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseFiles:"), true);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseDirectories:"), false);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setAllowsMultipleSelection:"), options.AllowMultiple);

        return RunPanel(panel);
    }

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        nint panel = CreatePanel(isSave: true, options.Title, options.Filters, options.InitialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setExtensionHidden:"), true);

        if (!string.IsNullOrWhiteSpace(options.DefaultFileName))
        {
            // 预填文件名；扩展名不在这里补——Cocoa 由 allowedFileTypes 与系统行为共同决定
            ObjCRuntime.SendVoidObj(
                panel,
                ObjCRuntime.Sel("setNameFieldStringValue:"),
                ObjCRuntime.MakeNSString(options.DefaultFileName));
        }

        string[] paths = RunPanel(panel);
        return paths.Length > 0 ? paths[0] : null;
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        nint panel = CreatePanel(isSave: false, title, filters: null, initialDirectory);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseFiles:"), false);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setCanChooseDirectories:"), true);
        ObjCRuntime.SendVoidBool(panel, ObjCRuntime.Sel("setAllowsMultipleSelection:"), false);

        string[] paths = RunPanel(panel);
        return paths.Length > 0 ? paths[0] : null;
    }
}
