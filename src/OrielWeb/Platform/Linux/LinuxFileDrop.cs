using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 的文件拖放：把 webview 注册成 <c>text/uri-list</c> 落点，载荷解析后上报。
/// </summary>
/// <remarks>
/// 落点设在 **webview** 而不是窗口上：拖放要落在页面区域内才算数，而 webview 铺满整个客户区；
/// 两处都设会产生两份事件。载荷的读取在 <see cref="LinuxSignalHandlers.OnDragDataReceivedTrampoline"/>
/// （GTK 的调用入口在那里），这里只管"注册"与"上报"。
/// </remarks>
internal sealed partial class LinuxWindowHost
{
    /// <summary>进程级的 GtkTargetEntry 数组，只分配一次（GTK 不保证拷贝，长期有效最安全）。</summary>
    private static nint s_dropTargets;

    private event Action<OrielFileDropEventArgs>? FileDropped;

    event Action<OrielFileDropEventArgs>? IWindowBackend.FileDropped
    {
        add => FileDropped += value;
        remove => FileDropped -= value;
    }

    /// <summary>把 webview 注册为文件拖放目标（在 Create 里、webview 建好之后调用）。</summary>
    private static void EnableFileDrop(nint webview)
    {
        if (s_dropTargets == 0)
        {
            // GtkTargetEntry { gchar* target; guint flags; guint info; }：按实际布局手工摆放，
            // 因为 GtkNative 只声明函数、不声明结构体
            nint entry = Marshal.AllocHGlobal(IntPtr.Size + (sizeof(uint) * 2));
            Marshal.WriteIntPtr(entry, Marshal.StringToCoTaskMemUTF8("text/uri-list"));
            Marshal.WriteInt32(entry, IntPtr.Size, 0);                          // flags
            Marshal.WriteInt32(entry, IntPtr.Size + sizeof(uint), 0);           // info
            s_dropTargets = entry;
        }

        GtkNative.GtkDragDestSet(webview, GtkNative.GtkDestDefaultAll, s_dropTargets, 1, GtkNative.GdkActionCopy);
    }

    /// <summary>拖放载荷里解析出的本地路径（由信号 trampoline 调用）。</summary>
    internal void OnFilesDropped(IReadOnlyList<string> paths)
        => FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
}
