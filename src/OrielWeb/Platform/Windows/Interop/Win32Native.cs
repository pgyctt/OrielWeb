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
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_DISPATCH = WM_APP + 1;

    public static readonly nint HTCAPTION = 2;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;

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

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint hwnd);

    // ---- kernel32 ----

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32")]
    internal static partial uint GetCurrentThreadId();

    // ---- comdlg32 ----

    [LibraryImport("comdlg32", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOpenFileNameW(ref OPENFILENAMEW openFileName);

    [LibraryImport("comdlg32", EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSaveFileNameW(ref OPENFILENAMEW openFileName);

    [LibraryImport("comdlg32")]
    internal static partial uint CommDlgExtendedError();
}
