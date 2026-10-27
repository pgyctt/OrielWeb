using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionDevice;

using DCompTarget = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionTarget;
using DCompVisual = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>原生对话框（打开/保存/选目录/消息框）。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        uint flags = Win32Constants.MB_OK | icon switch
        {
            OrielMessageBoxIcon.Warning => Win32Constants.MB_ICONWARNING,
            OrielMessageBoxIcon.Error => Win32Constants.MB_ICONERROR,
            OrielMessageBoxIcon.Question => Win32Constants.MB_ICONQUESTION,
            _ => Win32Constants.MB_ICONINFORMATION,
        };
        Win32.MessageBoxW(_hwnd, text, title ?? _title, flags);
    }

    // ------------------------------------------------------------------
    // 对话框
    // ------------------------------------------------------------------

    public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
        => ShowFileDialog(
            isSave: false,
            options.Title,
            options.Filters,
            defaultExtension: null,
            options.InitialDirectory,
            options.AllowMultiple);

    public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
    {
        string[] paths = ShowFileDialog(
            isSave: true,
            options.Title,
            options.Filters,
            options.DefaultExtension,
            options.InitialDirectory,
            allowMultiple: false);

        return paths.Length > 0 ? paths[0] : null;
    }

    public string? ShowFolderDialog(string? title, string? initialDirectory)
    {
        // initialDirectory 在 Windows 上被忽略：SHBrowseForFolder 要设初始位置得挂 BFFM_INITIALIZED 回调
        // （见 Win32Native 里选型说明）。这里显式丢弃，而不是假装支持。
        _ = initialDirectory;

        nint titlePtr = title is null ? 0 : Marshal.StringToHGlobalUni(title);
        nint pathBuffer = Marshal.AllocHGlobal((Win32Constants.MAX_PATH + 1) * sizeof(char));
        try
        {
            Marshal.WriteInt16(pathBuffer, 0);

            var browseInfo = new BROWSEINFOW
            {
                hwndOwner = _hwnd,
                lpszTitle = titlePtr,
                ulFlags = Win32Constants.BIF_RETURNONLYFSDIRS | Win32Constants.BIF_USENEWUI,
            };

            nint pidl = Win32.SHBrowseForFolderW(ref browseInfo);
            if (pidl == 0)
            {
                return null; // 取消
            }

            try
            {
                if (!Win32.SHGetPathFromIDListW(pidl, pathBuffer))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(pathBuffer) is { Length: > 0 } path ? path : null;
            }
            finally
            {
                Win32.CoTaskMemFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
            if (titlePtr != 0)
            {
                Marshal.FreeHGlobal(titlePtr);
            }
        }
    }

    /// <summary>
    /// 打开/保存对话框的公共实现。返回路径数组（取消为空数组）。
    /// </summary>
    private string[] ShowFileDialog(
        bool isSave,
        string? title,
        IReadOnlyList<OrielFileFilter>? filters,
        string? defaultExtension,
        string? initialDirectory,
        bool allowMultiple)
    {
        string filterString = OrielFileFilter.RenderForWin32(filters ?? []);
        nint filterPtr = Marshal.StringToHGlobalUni(filterString);
        nint bufferPtr = Marshal.AllocHGlobal(32768 * sizeof(char));
        nint titlePtr = title is null ? 0 : Marshal.StringToHGlobalUni(title);
        nint initialDirPtr = initialDirectory is null ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        nint defExtPtr = defaultExtension is null ? 0 : Marshal.StringToHGlobalUni(defaultExtension);

        try
        {
            // 预填当前目录，避免对话框落在系统目录
            Marshal.WriteInt16(bufferPtr, 0);

            uint flags = isSave
                ? Win32Constants.OFN_OVERWRITEPROMPT | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR
                : Win32Constants.OFN_FILEMUSTEXIST | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR;

            if (allowMultiple && !isSave)
            {
                // OFN_EXPLORER 必须一起带：否则原生对话框按旧格式返回，且单个文件也会带出目录段
                flags |= Win32Constants.OFN_ALLOWMULTISELECT | Win32Constants.OFN_EXPLORER;
            }

            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = _hwnd,
                lpstrFilter = filterPtr,
                lpstrFile = bufferPtr,
                nMaxFile = 32768,
                lpstrTitle = titlePtr,
                lpstrInitialDir = initialDirPtr,
                lpstrDefExt = defExtPtr,
                Flags = flags,
            };

            bool ok = isSave ? Win32.GetSaveFileNameW(ref ofn) : Win32.GetOpenFileNameW(ref ofn);
            if (!ok)
            {
                return []; // 取消或错误（CommDlgExtendedError 区分，统一当取消）
            }

            // 多选时缓冲区里是"目录\0名\0名\0\0"这种多段结构，所以**必须按长度读**：
            // Marshal.PtrToStringUni(ptr) 遇到第一个 \0 就截断，那样拿到的是目录段（或被截短的文件名）。
            return OrielFileDialogSupport.ParseWin32MultiSelect(Marshal.PtrToStringUni(bufferPtr, 32768));
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(bufferPtr);
            if (titlePtr != 0) Marshal.FreeHGlobal(titlePtr);
            if (initialDirPtr != 0) Marshal.FreeHGlobal(initialDirPtr);
            if (defExtPtr != 0) Marshal.FreeHGlobal(defExtPtr);
        }
    }

    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);

    /// <summary>
    /// 询问型消息框：返回用户点了哪个语义按钮（见 <see cref="IWindowBackend.ShowMessageDialog"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自定义按钮文案在 Windows 上不生效</b>：<c>MessageBoxW</c> 只显示系统文案
    /// （"确定"/"取消"/"是"/"否"），要自定义得上 <c>TaskDialogIndirect</c>，
    /// 而那个 API 需要 comctl32 v6 的激活清单——为一个文案引入那套机制不划算。
    /// 返回的是**语义**，所以页面侧的判断不受影响，只有显示出来的文字是系统的。
    /// </para>
    /// <para>
    /// <c>MessageBoxW</c> 返回按钮 ID（<c>IDOK</c>=1、<c>IDCANCEL</c>=2、<c>IDYES</c>=6、
    /// <c>IDNO</c>=7），而 <c>MB_YESNO</c> 与 <c>MB_OKCANCEL</c> 的"取消"返回的都是
    /// <c>IDCANCEL</c>——所以映射是"ID → 语义"，不需要（也做不到）靠传进来的按钮集合反推。
    /// </para>
    /// </remarks>
    MessageResult? IWindowBackend.ShowMessageDialog(
        string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
    {
        // 显示文案只能用系统的（MessageBoxW 没有自定义入口）。
        // 显式丢弃而不是"忘了用"：这两个参数在另两个平台上是有用的。
        _ = okLabel;
        _ = cancelLabel;

        uint flags = buttons switch
        {
            MessageButtons.OkCancel => Win32Constants.MB_OKCANCEL,
            MessageButtons.YesNo => Win32Constants.MB_YESNO,
            MessageButtons.YesNoCancel => Win32Constants.MB_YESNOCANCEL,
            _ => Win32Constants.MB_OK,
        };

        flags |= kind switch
        {
            "warning" => Win32Constants.MB_ICONWARNING,
            "error" => Win32Constants.MB_ICONERROR,
            "question" => Win32Constants.MB_ICONQUESTION,
            _ => Win32Constants.MB_ICONINFORMATION,
        };

        int result = Win32.MessageBoxW(_hwnd, message, title ?? _title, flags);

        return result switch
        {
            Win32Constants.IDOK => MessageResult.Ok,
            Win32Constants.IDCANCEL => MessageResult.Cancel,
            Win32Constants.IDYES => MessageResult.Yes,
            Win32Constants.IDNO => MessageResult.No,
            _ => null,                  // 被外部关掉（比如任务管理器）时没有结果
        };
    }
}
