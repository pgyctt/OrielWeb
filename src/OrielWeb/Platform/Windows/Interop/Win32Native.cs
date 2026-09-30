using System.Runtime.InteropServices;

namespace OrielWeb.Platform.Windows.Interop;

// Win32 常量（仅 M1 所需子集）
internal static class Win32Constants
{
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOVE = 0x0003;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_NCCREATE = 0x0081;
    /// <summary>客户区大小计算（0x0083）。注意不要与 <c>WM_NCHITTEST</c>（0x0084）混淆：
    /// 后者的 lParam 是鼠标坐标而非指针，误按 RECT* 写入会直接 AccessViolation。</summary>
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;

    // WM_NCHITTEST 的调整大小命中值
    public static readonly nint HTLEFT = 10;
    public static readonly nint HTRIGHT = 11;
    public static readonly nint HTTOP = 12;
    public static readonly nint HTTOPLEFT = 13;
    public static readonly nint HTTOPRIGHT = 14;
    public static readonly nint HTBOTTOM = 15;
    public static readonly nint HTBOTTOMLEFT = 16;
    public static readonly nint HTBOTTOMRIGHT = 17;
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_DISPATCH = WM_APP + 1;
    /// <summary>托盘图标回调消息（用不同的 WM_APP 消息区分回调来源，V4 下回调不传 uID）。</summary>
    public const uint WM_APP_TRAY = WM_APP + 2;
    /// <summary>通知气球（独立于用户托盘的隐藏托盘项）的回调消息。</summary>
    public const uint WM_APP_NOTIFY = WM_APP + 3;

    public const uint WM_NULL = 0x0000;
    public const uint WM_CONTEXTMENU = 0x007B;
    /// <summary>菜单栏与加速键发来的命令消息；LOWORD(wParam) 是命令 id。</summary>
    public const uint WM_COMMAND = 0x0111;

    // ---- 托盘（Shell_NotifyIcon）----

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_SHOWTIP = 0x00000080;

    /// <summary>隐藏状态（配 NIF_STATE 用）：占位但不在托盘区画图标，只用来发气球。</summary>
    public const uint NIS_HIDDEN = 0x00000001;

    /// <summary>V4 行为：回调 wParam 是事件类型、坐标在 lParam 里，右键走 WM_CONTEXTMENU。</summary>
    public const uint NOTIFYICON_VERSION_4 = 4;

    /// <summary>V4 下左键单击（WM_USER + 0）。</summary>
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_BALLOONTIMEOUT = 0x0404;
    /// <summary>用户点了气球本体（WM_USER + 5）。</summary>
    public const uint NIN_BALLOONUSERCLICK = 0x0405;

    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_ERROR = 0x00000003;

    // ---- 弹出菜单 ----

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_CHECKED = 0x00000008;
    /// <summary>子菜单：此时 itemId 位置传的是子菜单句柄而不是命令 id。</summary>
    public const uint MF_POPUP = 0x00000010;
    /// <summary>右键也能选（托盘菜单必须带，否则鼠标按键一松菜单就关了）。</summary>
    public const uint TPM_RIGHTBUTTON = 0x0002;
    /// <summary>返回选中项 id 而不是发 WM_COMMAND；0 表示用户没选任何项。</summary>
    public const uint TPM_RETURNCMD = 0x0100;

    /// <summary>WM_NCLBUTTONDOWN 的命中值：令窗口进入标题栏拖动的模态循环（见 BeginDrag）。</summary>
    public static readonly nint HTCAPTION = 2;

    /// <summary>系统窗口边框厚度（不含内边距）；需配合 GetSystemMetricsForDpi 使用。</summary>
    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;

    /// <summary>窗口无重定向表面：内容必须由 DirectComposition 提供（Composition 宿主要求）。</summary>
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;

    // ---- 窗口图标 ----

    public const uint WM_SETICON = 0x0080;
    /// <summary>WM_SETICON 的 wParam：小图标（标题栏 / Alt-Tab）与大图标（任务栏）。</summary>
    public const nuint ICON_SMALL = 0;
    public const nuint ICON_BIG = 1;
    /// <summary>LoadImage 的 type 参数：图标。</summary>
    public const uint IMAGE_ICON = 1;
    /// <summary>LoadImage 的 fuLoad 标志：从文件路径加载（而非资源）。</summary>
    public const uint LR_LOADFROMFILE = 0x0010;
    /// <summary>图标尺寸度量（按显示设置加载窗口图标用）。</summary>
    public const int SM_CXICON = 11;
    public const int SM_CYICON = 12;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    // ---- 系统主题 ----

    public const uint WM_SETTINGCHANGE = 0x001A;
    /// <summary>注册表根：HKEY_CURRENT_USER。</summary>
    public static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001);
    /// <summary>RegGetValue 的可接受类型：只要 REG_DWORD。</summary>
    public const uint RRF_RT_REG_DWORD = 0x00000010;

    // ---- 剪贴板 ----

    /// <summary>剪贴板格式：UTF-16 文本。</summary>
    public const uint CF_UNICODETEXT = 13;
    /// <summary>GlobalAlloc 标志：可移动（剪贴板数据必须是可移动的全局内存块）。</summary>
    public const uint GMEM_MOVEABLE = 0x0002;

    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_SHIFT = 0x0004;
    public const int MK_CONTROL = 0x0008;
    public const int MK_MBUTTON = 0x0010;
    public const int MK_XBUTTON1 = 0x0020;
    public const int MK_XBUTTON2 = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_RESTORE = 9;

    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public static readonly nint HWND_MESSAGE = -3;

    public const int GWL_STYLE = -16;
    public const int GWLP_USERDATA = -21;

    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_DBLCLKS = 0x0008;

    public static readonly nint IDC_ARROW = 32512;
    public static readonly nint IDI_APPLICATION = 32512;

    public const int MONITOR_DEFAULTTONEAREST = 2;

    public const uint OFN_HIDEREADONLY = 0x4;
    public const uint OFN_OVERWRITEPROMPT = 0x2;
    public const uint OFN_NOCHANGEDIR = 0x8;
    public const uint OFN_PATHMUSTEXIST = 0x800;
    public const uint OFN_FILEMUSTEXIST = 0x1000;

    public const uint MB_OK = 0x0;
    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONQUESTION = 0x20;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_ICONINFORMATION = 0x40;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>WM_NCCALCSIZE 的 lParam（wParam 非 0 时）。rgrc0 传入为窗口矩形，返回时须为客户区矩形。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}

/// <summary>WM_NCCALCSIZE 的 lParam（wParam 非 0 时）。rgrc0 传入为窗口矩形，返回时须为客户区矩形。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NCCALCSIZE_PARAMS
{
    public RECT rgrc0;
    public RECT rgrc1;
    public RECT rgrc2;
    public nint lppos;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CREATESTRUCTW
{
    public nint lpCreateParams;
    public nint hInstance;
    public nint hMenu;
    public nint hwndParent;
    public int cy;
    public int cx;
    public int y;
    public int x;
    public uint style;
    public nint lpszName;
    public nint lpszClass;
    public uint dwExStyle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OPENFILENAMEW
{
    public uint lStructSize;
    public nint hwndOwner;
    public nint hInstance;
    public nint lpstrFilter;
    public nint lpstrCustomFilter;
    public uint nMaxCustFilter;
    public uint nFilterIndex;
    public nint lpstrFile;
    public uint nMaxFile;
    public nint lpstrFileTitle;
    public uint nMaxFileTitle;
    public nint lpstrInitialDir;
    public nint lpstrTitle;
    public uint Flags;
    public ushort nFileOffset;
    public ushort nFileExtension;
    public nint lpstrDefExt;
    public nint lCustData;
    public nint lpfnHook;
    public nint lpTemplateName;
    public nint pvReserved;
    public uint dwReserved;
    public uint FlagsEx;
}

internal static unsafe partial class Win32
{
    // ---- user32 ----

    [LibraryImport("user32", SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", SetLastError = true)]
    internal static partial nint CreateWindowExW(
        uint extendedStyle,
        nint className,
        nint windowName,
        uint style,
        int x, int y, int width, int height,
        nint parentWindow,
        nint menu,
        nint instance,
        void* creationParameter);

    [LibraryImport("user32")]
    internal static partial nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32")]
    internal static partial int GetMessageW(out MSG message, nint window, uint minFilter, uint maxFilter);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG message);

    [LibraryImport("user32")]
    internal static partial nint DispatchMessageW(ref MSG message);

    [LibraryImport("user32")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32")]
    internal static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [LibraryImport("user32")]
    internal static partial nint SetCursor(nint cursor);



    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowTextW(nint hwnd, string text);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32")]
    internal static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursorW(nint instance, nint cursorName);

    [LibraryImport("user32", EntryPoint = "LoadIconW")]
    internal static partial nint LoadIconW(nint instance, nint iconName);

    [LibraryImport("user32")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    /// <summary>从文件或资源加载图标/光标/位图；窗口图标用 <c>IMAGE_ICON | LR_LOADFROMFILE</c>。</summary>
    [LibraryImport("user32", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImageW(nint hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>按指定 DPI 取系统度量（per-monitor DPI 感知下 <see cref="GetSystemMetrics"/> 不可靠）。</summary>
    [LibraryImport("user32")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    // ---- shell32 ----

    /// <summary>提取可执行文件中的图标，按当前显示设置给出大/小两个尺寸；返回提取到的图标个数。</summary>
    [LibraryImport("shell32", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconExW(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    // ---- kernel32 ----

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32")]
    internal static partial uint GetCurrentThreadId();

    // ---- 剪贴板（user32）----

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hwndNewOwner);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    /// <summary>把全局内存块交给剪贴板。成功时所有权转移给系统（不要再释放）。</summary>
    [LibraryImport("user32", SetLastError = true)]
    internal static partial nint SetClipboardData(uint format, nint memory);

    /// <summary>注册自定义剪贴板格式（如 "HTML Format"），返回格式 ID；同名重复注册返回同一个 ID。</summary>
    [LibraryImport("user32", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormatW(string format);

    // ---- 全局内存（kernel32）----

    [LibraryImport("kernel32", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32", SetLastError = true)]
    internal static partial nint GlobalFree(nint memory);

    // ---- 注册表（advapi32）----

    /// <summary>
    /// 读注册表值。flags 用 <see cref="Win32Constants.RRF_RT_REG_DWORD"/> 限定类型；
    /// 值不存在或类型不符时返回非 0（ERROR_*），此时 data 的内容无意义。
    /// </summary>
    [LibraryImport("advapi32", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegGetValueW(
        nint hkey,
        string? subKey,
        string? valueName,
        uint flags,
        nint type,
        nint data,
        ref uint dataSize);

    // ---- comdlg32 ----

    [LibraryImport("comdlg32", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOpenFileNameW(ref OPENFILENAMEW openFileName);

    [LibraryImport("comdlg32", EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSaveFileNameW(ref OPENFILENAMEW openFileName);

    [LibraryImport("comdlg32")]
    internal static partial uint CommDlgExtendedError();

    // ---- 托盘（shell32）----

    /// <summary>增删改托盘图标；data.cbSize 必须是本结构体的实际大小。</summary>
    [LibraryImport("shell32", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    // ---- 弹出菜单（user32）----

    [LibraryImport("user32")]
    internal static partial nint CreatePopupMenu();

    /// <summary>追加菜单项。<paramref name="itemId"/> 是 WM_COMMAND/TrackPopupMenu 回传的标识；
    /// 子菜单用 <c>MF_POPUP</c> 时该参数收子菜单句柄。</summary>
    [LibraryImport("user32", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(nint menu, uint flags, nuint itemId, string? itemText);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);

    /// <summary>弹出并跟踪菜单；带 TPM_RETURNCMD 时返回选中项 id（0 = 未选择）。</summary>
    [LibraryImport("user32", EntryPoint = "TrackPopupMenuEx")]
    internal static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpmParams);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    /// <summary>给窗口设置菜单栏；传 0 移除。菜单句柄的所有权转移给窗口（窗口销毁时释放）。</summary>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMenu(nint hwnd, nint menu);
}

/// <summary>
/// 托盘图标数据（V4 布局，64 位下 cbSize = 976）。
/// </summary>
/// <remarks>
/// 定长字符数组用 fixed buffer 而不是 <c>ByValTStr</c>：后者会把字符串 marshal 在托管侧
/// 临时分配，而这个结构体在 AOT 下要能直接往原生调用递——句柄与数组都必须是纯值。
/// 字段顺序与 Windows SDK 完全一致（V4 布局里 uTimeout 与 uVersion 是同一个联合体位置）。
/// </remarks>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersionOrTimeout;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}
