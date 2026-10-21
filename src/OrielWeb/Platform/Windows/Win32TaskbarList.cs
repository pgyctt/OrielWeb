using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 任务栏列表（<c>ITaskbarList3</c>）的最小封装：进度条、覆盖图标、按窗口增删任务栏按钮。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么手写 vtable 而不是 <c>[ComImport]</c>。</b>本库的 COM 互操作只有两条路
/// （见 <c>Win32DropTarget</c> 的文件头）：WebView2Aot 的源生成绑定，或者自己搭。
/// <c>ITaskbarList3</c> 不在 WebView2 的绑定里，而 .NET 5+ 又移除了内置的 COM 可调用包装
/// （<c>Marshal.GetComInterfaceForObject</c> 在 Core 上抛 <c>PlatformNotSupportedException</c>），
/// 所以这里用最直接的一种：<c>CoCreateInstance</c> 拿到接口指针，按槽位取函数指针直接调。
/// 我们**只调用、不实现**这个接口，因此不需要 vtable 分配与 <c>GCHandle</c> 那套。
/// </para>
/// <para>
/// <b>槽位号来自接口声明顺序</b>（<c>shobjidl_core.h</c>）：IUnknown 3 个，ITaskbarList 5 个
/// （HrInit/AddTab/DeleteTab/ActivateTab/SetActiveAlt），ITaskbarList2 1 个
/// （MarkFullscreenWindow），ITaskbarList3 从 9 开始。槽位写错的后果是**调错方法**而不是
/// 崩溃，所以每一个都注了名。
/// </para>
/// <para>
/// <b>单例 + 失败缓存。</b>这个组件每个进程只需要一个实例（它没有窗口状态）。创建失败
/// （无 Explorer 的会话、Server Core）只尝试一次：每次都试会让"任务栏不可用"这类环境
/// 在每个 setProgressBar 上都付一次失败的代价。
/// </para>
/// </remarks>
internal static unsafe class Win32TaskbarList
{
    private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IID_ITaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84");

    // 槽位（见类型注释）
    private const int SlotHrInit = 3;
    private const int SlotAddTab = 4;
    private const int SlotDeleteTab = 5;
    private const int SlotSetProgressValue = 9;
    private const int SlotSetProgressState = 10;
    private const int SlotSetOverlayIcon = 18;

    /// <summary><c>TBPFLAG</c>。</summary>
    internal const int TBPF_NOPROGRESS = 0;
    internal const int TBPF_INDETERMINATE = 1;
    internal const int TBPF_NORMAL = 2;
    internal const int TBPF_ERROR = 4;
    internal const int TBPF_PAUSED = 8;

    private static nint s_instance;
    private static bool s_failed;

    /// <summary>取（或懒建）任务栏对象；不可用时返回 0。</summary>
    private static nint Instance()
    {
        if (s_instance != 0 || s_failed)
        {
            return s_instance;
        }

        Guid clsid = CLSID_TaskbarList;
        Guid iid = IID_ITaskbarList3;

        // 失败时不抛：任务栏是"锦上添花"的能力，抛出去会让 setProgressBar 在
        // 没有 Explorer 的会话里变成硬错误。调用方按返回值决定是否上报。
        int hr = Win32.CoCreateInstance(
            &clsid, 0, Win32.CLSCTX_INPROC_SERVER, &iid, out nint taskbar);
        if (hr < 0 || taskbar == 0)
        {
            s_failed = true;
            OrielDiagnostics.Report(
                $"[OrielWeb] 任务栏列表不可用（CoCreateInstance HRESULT=0x{hr:X8}）："
                + "进度条/覆盖图标/隐藏任务栏按钮将不生效。");
            return 0;
        }

        // HrInit 是 ITaskbarList 的初始化握手，必须在其它调用之前一次。
        _ = ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)taskbar)[SlotHrInit])(taskbar);

        s_instance = taskbar;
        return s_instance;
    }

    /// <summary>把窗口加入任务栏（<c>AddTab</c>）；返回是否成功。</summary>
    internal static bool AddTab(nint hwnd) => Call2(SlotAddTab, hwnd);

    /// <summary>把窗口从任务栏摘掉（<c>DeleteTab</c>）；返回是否成功。</summary>
    internal static bool DeleteTab(nint hwnd) => Call2(SlotDeleteTab, hwnd);

    /// <summary>设置进度百分比（<c>SetProgressValue</c>，0–100）。</summary>
    internal static bool SetProgressValue(nint hwnd, ulong completed, ulong total)
    {
        nint taskbar = Instance();
        if (taskbar == 0)
        {
            return false;
        }

        var fn = (delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong, int>)
            (*(nint**)taskbar)[SlotSetProgressValue];
        return fn(taskbar, hwnd, completed, total) >= 0;
    }

    /// <summary>设置进度状态（<c>SetProgressState</c>，取值为 <c>TBPF_*</c>）。</summary>
    internal static bool SetProgressState(nint hwnd, int state)
    {
        nint taskbar = Instance();
        if (taskbar == 0)
        {
            return false;
        }

        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int, int>)
            (*(nint**)taskbar)[SlotSetProgressState];
        return fn(taskbar, hwnd, state) >= 0;
    }

    /// <summary>设置覆盖图标（<c>SetOverlayIcon</c>）；<paramref name="icon"/> 传 0 表示清除。</summary>
    internal static bool SetOverlayIcon(nint hwnd, nint icon, string? description)
    {
        nint taskbar = Instance();
        if (taskbar == 0)
        {
            return false;
        }

        // 描述是给屏幕阅读器用的：HICON 本身对辅助技术不可读
        nint text = description is null ? 0 : ObjCText(description);
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)
                (*(nint**)taskbar)[SlotSetOverlayIcon];
            return fn(taskbar, hwnd, icon, text) >= 0;
        }
        finally
        {
            if (text != 0)
            {
                Marshal.FreeHGlobal(text);
            }
        }
    }

    /// <summary>造一个调用期间有效的 UTF-16 字符串（COM 会立刻拷走，不需要活到调用之后）。</summary>
    private static nint ObjCText(string value)
    {
        nint buffer = Marshal.AllocHGlobal((value.Length + 1) * sizeof(char));
        var span = new Span<char>((char*)buffer, value.Length + 1);
        value.AsSpan().CopyTo(span);
        span[value.Length] = '\0';
        return buffer;
    }

    /// <summary>只有 hwnd 一个参数的两种方法（AddTab / DeleteTab）。</summary>
    private static bool Call2(int slot, nint hwnd)
    {
        nint taskbar = Instance();
        if (taskbar == 0)
        {
            return false;
        }

        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)taskbar)[slot];
        return fn(taskbar, hwnd) >= 0;
    }
}
