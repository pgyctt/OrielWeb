using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>原生对话框（打开/保存/选目录/消息框）。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        var type = icon switch
        {
            OrielMessageBoxIcon.Warning => 1,  // GTK_MESSAGE_WARNING
            OrielMessageBoxIcon.Error => 3,    // GTK_MESSAGE_ERROR
            OrielMessageBoxIcon.Question => 2, // GTK_MESSAGE_QUESTION
            _ => 0,                             // GTK_MESSAGE_INFO
        };
        var dialog = GtkNative.GtkMessageDialogNew(_gtkWindow, 0, type, 0 /*GTK_BUTTONS_NONE*/, "%s", text);
        GtkNative.GtkDialogAddButton(dialog, "确定", GtkResponseOk);
        GtkNative.GtkDialogRun(dialog);
        GtkNative.GtkWidgetDestroy(dialog);
    }

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// GTK 这边**支持自定义按钮文案**（<c>gtk_dialog_add_button</c> 直接收文本），
    /// 所以 Windows 的限制在 Linux 上不存在。
    /// </para>
    /// <para>
    /// 按钮顺序按**添加顺序**排（GTK 的惯例）：肯定性的在前、取消性的在后，
    /// 所以 <c>YesNoCancel</c> 的添加顺序是 是 → 否 → 取消。响应 ID 用自定义值
    /// （<c>GtkResponseOk</c>/<c>Cancel</c>/<c>Yes</c>/<c>No</c>）以便回读时区分——
    /// 全用 <c>GTK_RESPONSE_OK</c> 的话三个按钮就分不出来了。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        int type = kind switch
        {
            "warning" => 1,  // GTK_MESSAGE_WARNING
            "error" => 3,    // GTK_MESSAGE_ERROR
            "question" => 2, // GTK_MESSAGE_QUESTION
            _ => 0,          // GTK_MESSAGE_INFO
        };

        nint dialog = GtkNative.GtkMessageDialogNew(_gtkWindow, 0, type, 0 /*GTK_BUTTONS_NONE*/, "%s", message);
        if (dialog == 0)
        {
            return null;
        }

        try
        {
            if (!string.IsNullOrEmpty(title))
            {
                GtkNative.GtkWindowSetTitle(dialog, title);
            }

            switch (buttons)
            {
                case MessageButtons.OkCancel:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseOk);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseCancel);
                    break;

                case MessageButtons.YesNo:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseYes);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseNo);
                    break;

                case MessageButtons.YesNoCancel:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseYes);
                    GtkNative.GtkDialogAddButton(dialog, cancelLabel, GtkResponseNo);
                    // 第三个按钮没有独立文案参数（Tauri 的 yes/no/cancel 也没有）——
                    // 用系统惯例的"取消"。
                    GtkNative.GtkDialogAddButton(dialog, "取消", GtkResponseCancel);
                    break;

                default:
                    GtkNative.GtkDialogAddButton(dialog, okLabel, GtkResponseOk);
                    break;
            }

            int response = GtkNative.GtkDialogRun(dialog);

            return response switch
            {
                GtkResponseOk => MessageResult.Ok,
                GtkResponseCancel => MessageResult.Cancel,
                GtkResponseYes => MessageResult.Yes,
                GtkResponseNo => MessageResult.No,
                _ => null,
            };
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    // ---- 文件对话框（GtkFileChooserDialog）----

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
    {
        const int FileChooserActionOpen = 0;
        nint dialog = CreateChooser(options.Title ?? "打开", FileChooserActionOpen, "打开");
        try
        {
            ApplyFilters(dialog, options.Filters);
            SetInitialFolder(dialog, options.InitialDirectory);
            GtkNative.GtkFileChooserSetSelectMultiple(dialog, options.AllowMultiple);

            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSelectedPaths(dialog) : [];
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        const int FileChooserActionSave = 1;
        nint dialog = CreateChooser(options.Title ?? "保存", FileChooserActionSave, "保存");
        try
        {
            ApplyFilters(dialog, options.Filters);
            SetInitialFolder(dialog, options.InitialDirectory);
            GtkNative.GtkFileChooserSetDoOverwriteConfirmation(dialog, true);

            string suggested = options.DefaultFileName
                ?? (string.IsNullOrWhiteSpace(options.DefaultExtension) ? "" : "未命名." + options.DefaultExtension);
            if (suggested.Length > 0)
            {
                GtkNative.GtkFileChooserSetCurrentName(dialog, suggested);
            }

            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSinglePath(dialog, options.DefaultExtension) : null;
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        // GTK 的文件夹选择就是同一个 chooser 换个 action，不需要另一个对话框类型
        const int FileChooserActionSelectFolder = 2;
        nint dialog = CreateChooser(title ?? "选择文件夹", FileChooserActionSelectFolder, "选择");
        try
        {
            SetInitialFolder(dialog, initialDirectory);
            return GtkNative.GtkDialogRun(dialog) == GtkResponseOk ? ReadSinglePath(dialog, extension: null) : null;
        }
        finally
        {
            GtkNative.GtkWidgetDestroy(dialog);
        }
    }

    private static void ApplyFilters(nint dialog, IReadOnlyList<OrielFileFilter>? filters)
    {
        if (filters is not { Count: > 0 })
        {
            return;
        }

        foreach (OrielFileFilter filter in filters)
        {
            if (filter.Patterns.Count == 0)
            {
                continue;
            }

            // 一个 OrielFileFilter 对应一个 GtkFileFilter，模式逐条加进去；
            // "*.*" 的归一化在 OrielFileFilter.GtkPatterns 里（fnmatch 语义差异，那里有单测）
            nint gtkFilter = GtkNative.GtkFileFilterNew();
            GtkNative.GtkFileFilterSetName(gtkFilter, filter.Name);
            foreach (string pattern in OrielFileFilter.GtkPatterns([filter]))
            {
                GtkNative.GtkFileFilterAddPattern(gtkFilter, pattern);
            }

            GtkNative.GtkFileChooserAddFilter(dialog, gtkFilter);
        }
    }

    private static string? ReadSinglePath(nint dialog, string? extension)
    {
        nint filename = GtkNative.GtkFileChooserGetFilename(dialog);
        if (filename == 0)
        {
            return null;
        }

        string? path = Marshal.PtrToStringUTF8(filename);
        GtkNative.GFree(filename);

        // GTK 不像 Windows 的对话框那样自动补扩展名（见 OrielFileDialogSupport 的说明）
        return string.IsNullOrEmpty(path) ? null : OrielFileDialogSupport.EnsureExtension(path, extension);
    }

    private static string[] ReadSelectedPaths(nint dialog)
    {
        nint list = GtkNative.GtkFileChooserGetFilenames(dialog);
        if (list == 0)
        {
            return [];
        }

        try
        {
            uint count = GtkNative.GSListLength(list);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                nint item = GtkNative.GSListNthData(list, i);
                if (item == 0)
                {
                    continue;
                }

                string? path = Marshal.PtrToStringUTF8(item);
                GtkNative.GFree(item);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            return [.. paths];
        }
        finally
        {
            // 只释放链表节点；里面的字符串已在上面逐个 g_free
            GtkNative.GSListFree(list);
        }
    }
}
