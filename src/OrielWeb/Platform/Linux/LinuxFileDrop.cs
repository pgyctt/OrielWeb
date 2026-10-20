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

    // B10 第二批：本平台目前只上报 drop（见 OnFilesDropped）。enter/over/leave 要 GTK 的
    // drag-motion / drag-leave 信号，并把 widget 逻辑像素乘 scaleFactor 换成物理像素；
    // 与页面事件面同批上线——只在 Windows 收到 enter 的平台分裂比三平台都没有更难查。
    private event Action<OrielDragEventArgs>? DragDrop;

    event Action<OrielDragEventArgs>? IWindowBackend.DragDrop
    {
        add => DragDrop += value;
        remove => DragDrop -= value;
    }

    /// <summary>本次拖动是否已上报过 enter。GTK 没有单独的 enter 信号，首帧 <c>drag-motion</c> 顶上。</summary>
    private bool _dragInside;

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

    /// <summary>逻辑像素的 widget 内坐标 → 客户区物理像素。</summary>
    internal (int X, int Y) ToPhysicalDragPoint(int x, int y)
    {
        double scale = ScaleFactor;
        return ((int)Math.Round(x * scale), (int)Math.Round(y * scale));
    }

    /// <summary>
    /// 拖动进入。GTK 在 <c>drag-motion</c> 阶段**拿不到路径**（要等 <c>drag-data-received</c>），
    /// 所以这里的 <c>paths</c> 是空的——与 Tauri 的 GTK 实现一致，写进 API.md 而不是假装有值。
    /// </summary>
    internal void OnDragEnter(IReadOnlyList<string> paths, int x, int y)
    {
        _dragInside = true;
        DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Enter, paths, x, y));
    }

    /// <summary>在窗口内移动。</summary>
    internal void OnDragOver(int x, int y)
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Over, [], x, y));

    /// <summary>拖出窗口。</summary>
    internal void OnDragLeave()
    {
        _dragInside = false;
        DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Leave, [], 0, 0));
    }

    /// <summary>本次拖动是否已经 enter 过（供 trampoline 判断该发 enter 还是 over）。</summary>
    internal bool DragSessionActive => _dragInside;

    /// <summary>拖放载荷里解析出的本地路径（由信号 trampoline 调用）。</summary>
    internal void OnFilesDropped(IReadOnlyList<string> paths, int x, int y)
    {
        _dragInside = false;
        DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Drop, paths, x, y));
        FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
    }
}
