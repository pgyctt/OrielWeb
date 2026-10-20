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

    /// <summary>
    /// 菜单项被选中（也用于加速键与按钮通知，靠 lParam 区分来源）。
    /// </summary>
    /// <remarks>
    /// wParam 低字是菜单项的命令 id（就是 <c>AppendMenuW</c> 传的那个 itemId），
    /// 高字对菜单项是 0。<b>lParam 为 0 表示来自菜单或加速键</b>；来自控件时是控件句柄。
    /// 本库不注册加速键，所以 lParam == 0 就是菜单点击。
    /// </remarks>
    public const uint WM_COMMAND = 0x0111;

    /// <summary>
    /// 取消当前的模态状态（菜单跟踪、拖拽、滚动条拖动）。
    /// </summary>
    /// <remarks>
    /// 这是**关掉 <c>TrackPopupMenuEx</c> 弹出的菜单**的可靠方式：那个函数用的是自己创建的
    /// 模态循环，<c>EndMenu</c> 对它无效，而 <c>WM_CANCELMODE</c> 会走进它的取消路径。
    /// 投递给**菜单的属主窗口**（就是弹出时传的那个 hwnd）。
    /// </remarks>
    public const uint WM_CANCELMODE = 0x001F;

    /// <summary>
    /// 窗口被移到不同 DPI 的显示器（或 DPI 设置变了）时收到。
    /// </summary>
    /// <remarks>
    /// wParam 高字是新 DPI（低字同值），lParam 是系统建议的新窗口矩形。
    /// <b>本库只用它来上报缩放变化，不接管消息</b>——不改窗口尺寸，
    /// 把 lParam 那个建议矩形的处理留给 <c>DefWindowProc</c>。
    /// </remarks>
    public const uint WM_DPICHANGED = 0x02E0;

    /// <summary>有文件被拖到窗口上；wParam 是 HDROP，用完必须 DragFinish。</summary>
    public const uint WM_DROPFILES = 0x0233;

    /// <summary>窗口扩展样式：接收文件拖放。</summary>
    public const uint WS_EX_ACCEPTFILES = 0x00000010;

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

    /// <summary>
    /// V4 回调的鼠标/悬停事件（<c>WM_USER + n</c>，经 <c>LOWORD(lParam)</c> 送达）。
    /// </summary>
    /// <remarks>
    /// <c>NIN_POPUPOPEN</c> / <c>NIN_POPUPCLOSE</c> 就是"指针进入/离开图标"——它们是
    /// Windows 上唯一能拿到 Enter/Leave 的入口（Vista+ 才有），因此必须用 V4 才拿得到。
    /// </remarks>
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;
    public const uint NIN_BALLOONSHOW = 0x0402;
    public const uint NIN_BALLOONHIDE = 0x0403;
    public const uint NIN_BALLOONTIMEOUT = 0x0404;
    /// <summary>用户点了气球本体（WM_USER + 5）。</summary>
    public const uint NIN_BALLOONUSERCLICK = 0x0405;
    /// <summary>指针悬停在图标上（Vista+）：对应 <see cref="OrielTrayEventType.Enter"/>。</summary>
    public const uint NIN_POPUPOPEN = 0x0406;
    /// <summary>指针不再悬停（Vista+）：对应 <see cref="OrielTrayEventType.Leave"/>。</summary>
    public const uint NIN_POPUPCLOSE = 0x0407;

    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_ERROR = 0x00000003;

    // ---- 注册表（advapi32）----

    /// <summary>注册表值类型：以 NUL 结尾的字符串。</summary>
    public const uint REG_SZ = 1;

    /// <summary>打开键时只请求"写值"权限（自启项只需要这个，权限要得越少越好）。</summary>
    public const uint KEY_SET_VALUE = 0x0002;

    /// <summary>RegGetValueW 的 flags：只接受 REG_SZ（用于"值存在与否"的探测）。</summary>
    public const uint RRF_RT_REG_SZ = 0x00000002;

    /// <summary>RegGetValueW 的返回码：缓冲区太小（我们只探大小，这个码说明值确实存在）。</summary>
    public const int ERROR_MORE_DATA = 234;

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

    // ---- 系统菜单项（"可关闭"落点用）----

    /// <summary>系统菜单里的"关闭"项 id。</summary>
    public const uint SC_CLOSE = 0xF060;

    /// <summary>系统菜单里的"最小化"项 id。</summary>
    public const uint SC_MINIMIZE = 0xF020;

    /// <summary>按命令 id 定位菜单项（<c>EnableMenuItem</c>/<c>GetMenuState</c> 的 uFlags）。</summary>
    public const uint MF_BYCOMMAND = 0x00000000;

    /// <summary>菜单项可用（值为 0，与 <see cref="MF_BYCOMMAND"/> 一样是"清零"含义，故不能按位或区分）。</summary>
    public const uint MF_ENABLED = 0x00000000;

    /// <summary>菜单项禁用（灰化并不可选）。</summary>
    public const uint MF_DISABLED = 0x00000002;

    /// <summary>WM_NCLBUTTONDOWN 的命中值：令窗口进入标题栏拖动的模态循环（见 BeginDrag）。</summary>
    public static readonly nint HTCAPTION = 2;

    /// <summary>系统窗口边框厚度（不含内边距）；需配合 GetSystemMetricsForDpi 使用。</summary>
    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;

    /// <summary>菜单栏高度（无边框窗口挂菜单栏时要在 <c>WM_NCCALCSIZE</c> 里让出这么多）。</summary>
    public const int SM_CYMENU = 15;
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
    /// <summary>
    /// 取窗口当前图标（wParam 同样是 <see cref="ICON_SMALL"/> / <see cref="ICON_BIG"/>）。
    /// </summary>
    /// <remarks>
    /// 自检用它把"图标真的设上去了"变成可断言的事实——否则那条断言只能靠目视。
    /// 注意它返回的是**当前生效的句柄**，窗口没设过图标时返回 0。
    /// </remarks>
    public const uint WM_GETICON = 0x007F;
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

    /// <summary>非客户区要重绘了（改了深色模式后发一次，让标题栏跟着重画）。</summary>
    public const uint WM_NCPAINT = 0x0085;
    /// <summary>DWM 属性：是否用深色模式画非客户区（Win10 2004+）。</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    /// <summary>Win10 1809/1903 上对应的旧属性名（值 20 在那些版本上返回 E_INVALIDARG）。</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    /// <summary><see cref="Win32.SetWindowThemeAttribute"/> 的属性类别：非客户区。</summary>
    public const int WTA_NONCLIENT = 1;
    /// <summary>让标题栏跟着应用主题画（而不是总是浅色）。</summary>
    public const int WTNCA_NODRAWCAPTION = 0x00000001;
    public const int WTNCA_NODRAWBORDER = 0x00000002;
    /// <summary>按主题画（而不是固定浅色）。</summary>
    public const int WTNCA_USEVISUALSTYLE = 0x00000004;

    // ---- 鼠标修饰键（wParam of WM_MOUSE*）----
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

    /// <summary><c>MonitorFromPoint</c> 的"点不在任何屏幕上时返回 NULL"（而不是就近或主屏）。</summary>
    public const uint MONITOR_DEFAULTTONULL = 0;
    /// <summary><c>MonitorFromPoint</c> 的"点不在任何屏幕上时返回主屏"。</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    /// <summary>主显示器标志（<c>MONITORINFO.dwFlags</c>）。</summary>
    public const uint MONITORINFOF_PRIMARY = 1;

    /// <summary><c>GetDpiForMonitor</c> 的"有效 DPI"（含用户缩放设置，Win8.1+）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    public const uint OFN_HIDEREADONLY = 0x4;
    public const uint OFN_OVERWRITEPROMPT = 0x2;
    public const uint OFN_NOCHANGEDIR = 0x8;
    public const uint OFN_PATHMUSTEXIST = 0x800;
    public const uint OFN_FILEMUSTEXIST = 0x1000;
    /// <summary>多选。必须与 <see cref="OFN_EXPLORER"/> 同用，否则返回的是旧格式。</summary>
    public const uint OFN_ALLOWMULTISELECT = 0x200;
    /// <summary>新版（资源管理器风格）对话框：多选返回值才有"目录 + 文件名单段"的形状。</summary>
    public const uint OFN_EXPLORER = 0x80000;

    // ---- 文件夹选择（SHBrowseForFolder）----

    /// <summary>只返回文件系统目录（否则列表里会出现"网上邻居"这类虚拟项）。</summary>
    public const uint BIF_RETURNONLYFSDIRS = 0x0001;
    /// <summary>现代外观（含"新建文件夹"）；老样式在 Win10+ 上很难看且没有新建入口。</summary>
    public const uint BIF_NEWDIALOGSTYLE = 0x0040;
    /// <summary>带一个可输入路径的编辑框。</summary>
    public const uint BIF_EDITBOX = 0x0010;
    public const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

    /// <summary>路径缓冲区的传统上限（含结尾 null）。<c>SHGetPathFromIDListW</c> 需要这么大一块。</summary>
    public const int MAX_PATH = 260;

    public const uint MB_OK = 0x0;

    /// <summary>按钮集合。这四个的低 4 位共同决定显示哪组按钮（互斥）。</summary>
    /// <remarks>
    /// <c>MB_OKCANCEL</c> = 0x1、<c>MB_YESNO</c> = 0x4、<c>MB_YESNOCANCEL</c> = 0x3——
    /// 注意它们**不是**按顺序排的（0x1/0x3/0x4），不能靠加减推。
    /// </remarks>
    public const uint MB_OKCANCEL = 0x1;
    public const uint MB_YESNOCANCEL = 0x3;
    public const uint MB_YESNO = 0x4;

    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONQUESTION = 0x20;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_ICONINFORMATION = 0x40;

    /// <summary><c>MessageBoxW</c> 的按钮 ID 返回值。</summary>
    public const int IDOK = 1;
    public const int IDCANCEL = 2;
    public const int IDYES = 6;
    public const int IDNO = 7;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}

/// <summary>
/// <c>SetWindowThemeAttribute</c> 的 <c>WTA_NONCLIENT</c> 属性载荷（B15 的 <c>Window.setTheme</c>）。
/// </summary>
/// <remarks>
/// <c>Mask</c> 说"这次要改哪几位"，<c>Flags</c> 是"改成什么"。只改一位时两者相等即可。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct WTA_NONCLIENT_PARAMS
{
    public uint Flags;
    public uint Mask;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y) { X = x; Y = y; }
}

/// <summary>OLE 拖放的坐标：**屏幕**坐标，左上原点。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

/// <summary>
/// <c>FORMATETC</c>：向 <c>IDataObject::GetData</c> 描述"我要哪种格式"。
/// </summary>
/// <remarks>
/// 字段顺序不能改：<c>ptd</c> 在 x64 上按 8 字节对齐，所以 <c>cfFormat</c> 后面有 6 字节填充，
/// 整体 32 字节。写成别的顺序会让 OLE 读到错位的值并返回 <c>DV_E_FORMATETC</c>。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    public ushort cfFormat;
    public nint ptd;
    public uint dwAspect;
    public int lindex;
    public uint tymed;
}

/// <summary>
/// <c>STGMEDIUM</c>：<c>GetData</c> 的返回值，装载实际载荷。
/// </summary>
/// <remarks>
/// 中间那个字段在 C 里是 union（hBitmap / hMetaFilePict / hEnhMetaFile / hGlobal / lpszFileName /
/// pstm / pstg）。这里只声明 <c>hGlobal</c>：本库只取 CF_HDROP，它一定走这条分支。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct STGMEDIUM
{
    public uint tymed;
    public nint hGlobal;
    public nint pUnkForRelease;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary><c>TrackMouseEvent</c> 的参数：<c>dwFlags = TME_LEAVE</c> 时，指针离开 <c>hwndTrack</c> 会收到一次 WM_MOUSELEAVE。</summary>
/// <remarks>
/// 这里曾经挂着 WM_NCCALCSIZE 的 lParam 说明（复制粘贴残留，评审 P3）——那句属于
/// <see cref="NCCALCSIZE_PARAMS"/>。托管侧只用 <c>cbSize</c> / <c>dwFlags</c> / <c>hwndTrack</c>，
/// <c>dwHoverTime</c> 只有 <c>TME_HOVER</c> 才用得到。
/// </remarks>
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

/// <summary>
/// <c>MONITORINFOEXW</c>：比 <see cref="MONITORINFO"/> 多一个设备名。
/// </summary>
/// <remarks>
/// 设备名是 <c>CCHDEVICENAME</c> = 32 个 UTF-16 字符（形如 <c>\\.\DISPLAY1</c>）。
/// 用 <c>fixed</c> 而不是 <c>[MarshalAs(ByValTStr)]</c>：后者在源生成的
/// <c>LibraryImport</c> 下不被支持，而这里的调用路径要求零封送。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public fixed char szDevice[32];
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

/// <summary>
/// <c>ICONINFO</c>：一个图标由"颜色位图 + 掩码位图"组成。
/// </summary>
/// <remarks>
/// <para>
/// 32bpp 图标把 alpha 放在颜色位图的第 4 字节里，掩码位图在 Windows XP 之后基本被忽略——
/// 但 <c>CreateIconIndirect</c> **要求**它非空，所以仍必须建一个全 0 的掩码。
/// </para>
/// <para>
/// <c>fIcon</c> 用 <see cref="int"/> 而不是 <see cref="bool"/>：<c>[LibraryImport]</c> 生成的
/// 封送代码不支持在 <c>ref</c> 结构体参数里做 <c>bool</c> 转换（SYSLIB1051），
/// 而 Win32 的 <c>BOOL</c> 本来就是 4 字节整数。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    /// <summary>非 0 表示这是图标（<c>TRUE</c>）；0 表示光标。</summary>
    public int fIcon;

    public uint xHotspot;
    public uint yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}

/// <summary>
/// <c>BITMAPINFOHEADER</c>：<c>CreateDIBSection</c> 需要的位图描述。
/// </summary>
/// <remarks>
/// <b>高度必须写成负值</b>：负高度表示"自顶向下"的 DIB，与托管 RGBA 的行序一致。
/// 写成正值会得到自底向上的位图，于是图标上下颠倒——而且在小图标上未必一眼看得出。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;

    /// <summary>造一个 32bpp 自顶向下的描述。</summary>
    internal static BitmapInfoHeader Create32BppTopDown(int width, int height) => new()
    {
        biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
        biWidth = width,
        biHeight = -height, // 负 = 自顶向下
        biPlanes = 1,
        biBitCount = 32,
        biCompression = 0,  // BI_RGB
        biSizeImage = (uint)(width * height * 4),
    };
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
    // ---- 主题（dwmapi / uxtheme）----
    //
    // B15 用：`Window.setTheme` / `App.setTheme` 在 Windows 上的落点。
    // 常量（DWMWA_USE_IMMERSIVE_DARK_MODE / WTA_NONCLIENT 等）在 Win32Constants 里。

    /// <summary>
    /// 让 DWM 按深色模式画这个窗口的非客户区（标题栏 / 边框）。
    /// </summary>
    /// <remarks>
    /// Win10 1809/1903 用旧属性名（<c>Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1</c>），
    /// 2004+ 才用 <c>Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE</c>——调用方两个都试，
    /// 旧系统对 20 返回 E_INVALIDARG，正好当作"这个属性不认识"跳过。
    /// 返回值是 HRESULT，**非 0 不抛**：那是"这个 Windows 版本不支持"，不是"调用错了"。
    /// </remarks>
    [LibraryImport("dwmapi")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>把窗口绑定到一个主题类（<c>null</c> 表示恢复默认），让标题栏跟着主题画。</summary>
    [LibraryImport("uxtheme", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    /// <summary>
    /// 设置窗口的非客户区主题属性（<see cref="Win32Constants.WTA_NONCLIENT"/> 等）。
    /// </summary>
    /// <remarks>
    /// <c>value</c> 收 <see cref="WTA_NONCLIENT_PARAMS"/> 的引用（按 <c>size</c> 传大小）。
    /// <c>SetWindowThemeAttribute</c> 的真实签名是 <c>void* pvAttribute</c>，这里按结构体
    /// 封送更省事（源生成器对 <c>void*</c> 只能给 <c>nint</c>，调用方还得手工取地址）。
    /// </remarks>
    [LibraryImport("uxtheme")]
    internal static partial int SetWindowThemeAttribute(
        nint hwnd, int attribute, ref WTA_NONCLIENT_PARAMS value, int size);

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

    /// <summary>同 <c>GetMonitorInfoW</c>，但取带设备名的扩展结构（判据是 <c>cbSize</c>）。</summary>
    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoEx(nint monitor, ref MONITORINFOEXW info);

    /// <summary>包含该点的显示器；<paramref name="flags"/> 决定"不在任何屏上"时的返回值。</summary>
    [LibraryImport("user32")]
    internal static partial nint MonitorFromPoint(POINT point, uint flags);

    /// <summary>
    /// 枚举显示器。回调返回 FALSE 会**终止**枚举，所以它恒返回 TRUE（见调用方）。
    /// </summary>
    /// <remarks>
    /// 回调用函数指针而不是托管委托：源生成器对委托参数要么要求
    /// <c>[MarshalAs(UnmanagedType.FunctionPtr)]</c> 的旧式封送、要么拒绝，而函数指针在
    /// AOT 下没有额外的桩代码。与窗口过程（<c>Win32WindowHost</c> 里的
    /// <c>[UnmanagedCallersOnly]</c> 静态方法 + 函数指针）同一套做法。
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint data);

    /// <summary>
    /// 取显示器的有效 DPI（96 = 100%）。失败时返回非 0 的 HRESULT。
    /// </summary>
    /// <remarks>
    /// 在 <c>shcore.dll</c>（Win8.1+）里。调它要求进程已声明 DPI 感知，否则返回
    /// <c>E_ACCESSDENIED</c>——本库在启动时已经设了 <c>PER_MONITOR_AWARE_V2</c>
    /// （见 <c>WindowsPlatformBackend.EnsureDpiAwareness</c>），所以这里能拿到每屏真值。
    /// </remarks>
    [LibraryImport("shcore")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

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

    /// <summary>
    /// 结束调用线程上正在进行的**经典**菜单循环（<c>TrackPopupMenu</c> 之前那套
    /// <c>StartMenu</c>/<c>EndMenu</c> 协议）。
    /// </summary>
    /// <remarks>
    /// <b>本库不用它</b>：所有菜单都走 <c>TrackPopupMenuEx</c>，那个函数用的是自己创建的
    /// 模态循环，只认 <see cref="Win32Constants.WM_CANCELMODE"/>。这里保留声明只是为了
    /// 让下一个人不必重新查一遍"为什么 EndMenu 关不掉菜单"——答案就是这条注释。
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndMenu();

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

    /// <summary>窗口是否最小化（图标化）。</summary>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    /// <summary>
    /// 当前前台窗口。
    /// </summary>
    /// <remarks>
    /// 判"本窗口有没有焦点"要用它比对，而不是 <c>GetFocus</c>：
    /// 后者只对**调用线程的窗口队列**有意义，而这里的调用线程不一定是前台的。
    /// 比对前台窗口才是"用户正在操作它"的判据。
    /// </remarks>
    [LibraryImport("user32")]
    internal static partial nint GetForegroundWindow();

    /// <summary>窗口是否接收输入（被 <c>EnableWindow(false)</c> 禁用后为 false）。</summary>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowEnabled(nint hwnd);

    /// <summary>启用/禁用窗口的输入。</summary>
    /// <remarks>
    /// 禁用后窗口仍会绘制，但**收不到鼠标与键盘**，而且系统会把焦点交给别的窗口。
    /// 返回值是"调用**之前**是否处于启用状态"（不是操作是否成功）——很容易看错。
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    // ---- 请求用户注意（任务栏闪烁）----

    /// <summary>停止闪烁。</summary>
    public const uint FLASHW_STOP = 0;

    /// <summary>闪烁标题栏。</summary>
    public const uint FLASHW_CAPTION = 0x1;

    /// <summary>闪烁任务栏按钮。</summary>
    public const uint FLASHW_TRAY = 0x2;

    /// <summary>两者都闪（<c>FLASHW_CAPTION | FLASHW_TRAY</c>）。</summary>
    public const uint FLASHW_ALL = 0x3;

    /// <summary>一直闪到窗口被激活为止。</summary>
    public const uint FLASHW_TIMERNOFG = 0xC;

    /// <summary><c>FlashWindowEx</c> 的参数。</summary>
    /// <remarks>
    /// <c>cbSize</c> 必须等于本结构的大小，否则调用直接失败——所以构造时用
    /// <c>Marshal.SizeOf</c> 而不是手写常量。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    /// <summary>闪烁窗口以引起用户注意。</summary>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>
    /// 窗口是否"可见"（自身及所有祖先都带 <c>WS_VISIBLE</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <c>IsWindowVisible</c> 的语义一致：**最小化的窗口仍算可见**（它只是移到了任务栏，
    /// 样式位没清）。因此它回答的是"被 <see cref="ShowWindow"/> 的 <c>SW_HIDE</c> 藏住了没有"，
    /// 而不是"在屏幕上占着地儿没有"——这正是 <c>WebviewWindow.IsVisible</c> 要的语义。
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// 系统双击间隔（毫秒，控制面板里可改；默认 500）。
    /// </summary>
    /// <remarks>
    /// 无边框窗口要在页面的 <c>mousedown</c> 里**同步**判断"这次按下是不是双击的第二下"，
    /// 而浏览器只给 <c>event.detail</c>、不给间隔本身——页面得知道系统设了多长才算得出来。
    /// 值为 0 表示取不到（本机没登录会话等），由 <see cref="OrielSystemSnapshot.Normalize"/> 兜底。
    /// </remarks>
    [LibraryImport("user32")]
    internal static partial uint GetDoubleClickTime();

    /// <summary>按指定 DPI 取系统度量（per-monitor DPI 感知下 <see cref="GetSystemMetrics"/> 不可靠）。</summary>
    [LibraryImport("user32")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// 按"客户区尺寸"反推"整窗尺寸"（按指定 DPI）。<c>lpRect</c> 传入客户区矩形，返回时被改写为整窗矩形。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它是"把 <c>WithSize</c> 的语义统一成客户区"的那件工具</b>：Linux 的
    /// <c>gtk_window_resize</c> 与 macOS 的 <c>setContentSize:</c> 都直接设客户区，而 Win32 的
    /// <c>SetWindowPos</c> / <c>CreateWindowExW</c> 设的是整窗（含边框与标题栏）。
    /// 不换算的后果是同一份 <c>WithSize(800, 600)</c> 在 Windows 上得到 784x561 的客户区
    /// （左右边框各 8、标题栏 + 下边框 39），而另两个平台得到 800x600。
    /// </para>
    /// <para>
    /// <b>只能用于有系统装饰的窗口</b>：无边框窗口的 <c>WM_NCCALCSIZE</c> 把客户区铺满整窗
    /// （见 <c>Win32WindowHost.HandleMessage</c>），那时客户区 = 整窗，再换算一次反而会大一圈。
    /// </para>
    /// <para>
    /// 用 <c>ForDpi</c> 变体而不是 <c>AdjustWindowRectEx</c>：本进程是 Per-Monitor-V2 感知的，
    /// 非 DPI 变体按**系统** DPI 算，在副屏（不同缩放）上会明显偏小。
    /// </para>
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRectExForDpi(
        ref RECT lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    /// <summary>从 ICONINFO（颜色位图 + 掩码位图）造一个 HICON。</summary>
    /// <remarks>
    /// 这是把托管 RGBA 变成 Win32 图标的最后一环（对标 Tauri 2 的 <c>Icon::from_rgba</c> →
    /// <c>tray_icon::Icon</c>）。返回的 HICON 由调用方持有，必须 <see cref="DestroyIcon"/>。
    /// </remarks>
    [LibraryImport("user32", EntryPoint = "CreateIconIndirect")]
    internal static partial nint CreateIconIndirect(ref IconInfo iconInfo);

    /// <summary>取（必要时创建）指定窗口的 DC；用完必须 <see cref="ReleaseDC"/>。</summary>
    [LibraryImport("user32")]
    internal static partial nint GetDC(nint hwnd);

    [LibraryImport("user32")]
    internal static partial int ReleaseDC(nint hwnd, nint hdc);

    // ---- gdi32 ----

    /// <summary>建一个内存 DC（把 DIB 选进去才能画）；用完必须 <see cref="DeleteDC"/>。</summary>
    [LibraryImport("gdi32")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    /// <summary>建一个 32bpp 自顶向下的 DIB 段，返回位图句柄；像素指针从 out 参数取回。</summary>
    [LibraryImport("gdi32", EntryPoint = "CreateDIBSection")]
    internal static partial nint CreateDIBSection(
        nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    /// <summary>建一个单色位图（用作 ICONINFO 的 AND 掩码）。</summary>
    [LibraryImport("gdi32", EntryPoint = "CreateBitmap")]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [LibraryImport("gdi32")]
    internal static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    // ---- shell32 ----

    /// <summary>提取可执行文件中的图标，按当前显示设置给出大/小两个尺寸；返回提取到的图标个数。</summary>
    [LibraryImport("shell32", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconExW(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    // ---- kernel32 ----

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32")]
    internal static partial uint GetCurrentThreadId();

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

    // ---- 文件夹选择（shell32）----
    // 用老的 SHBrowseForFolder 而不是 IFileOpenDialog + FOS_PICKFOLDERS：
    // 后者是 COM 接口，本库没有 COM 互操作基础（NativeAOT 下要自己搭 vtable 或引入 ComWrappers），
    // 为一个文件夹对话框引入那套机制不划算。代价是**设不了初始目录**——SHBrowseForFolder 要设初值
    // 得挂 BFFM_INITIALIZED 回调（记在 ROADMAP 里）。

    /// <summary>弹出文件夹选择器；返回需要 <see cref="CoTaskMemFree"/> 的 PIDL，取消返回 0。</summary>
    [LibraryImport("shell32", EntryPoint = "SHBrowseForFolderW")]
    internal static partial nint SHBrowseForFolderW(ref BROWSEINFOW browseInfo);

    /// <summary>把 PIDL 转成文件系统路径；缓冲区需至少 MAX_PATH 个字符。</summary>
    [LibraryImport("shell32", EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SHGetPathFromIDListW(nint pidl, nint pathBuffer);

    [LibraryImport("ole32")]
    internal static partial void CoTaskMemFree(nint ptr);

    // ---- 文件拖放（shell32）----
    // 走 WM_DROPFILES 而不是自建 OLE IDropTarget：本窗口用 Composition 宿主
    //（WS_EX_NOREDIRECTIONBITMAP，WebView2 **不是子窗口**），所以没有子窗口抢走拖放，
    // 父窗口自己收 WM_DROPFILES 即可——不必引入 OLE 初始化与手写 COM 接口。

    /// <summary>显式开关窗口接收文件拖放（等价写法是给窗口加 <c>WS_EX_ACCEPTFILES</c>）。</summary>
    [LibraryImport("shell32")]
    internal static partial void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    /// <summary>
    /// 查 HDROP 里的文件：<paramref name="fileIndex"/> 传 <c>0xFFFFFFFF</c> 时返回**文件个数**；
    /// 否则返回第 i 项的路径长度（<paramref name="fileName"/> 传 0 且 <paramref name="cch"/> 传 0），
    /// 或把路径写进缓冲区（返回实际写入的字符数，不含结尾 null）。
    /// </summary>
    [LibraryImport("shell32", EntryPoint = "DragQueryFileW")]
    internal static partial uint DragQueryFileW(nint hDrop, uint fileIndex, nint fileName, uint cch);

    /// <summary>释放 HDROP（由系统在消息处理结束后仍然有效，但必须由我们释放）。</summary>
    [LibraryImport("shell32")]
    internal static partial void DragFinish(nint hDrop);

    // ---- OLE 拖放（ole32）----
    // 这一节是为 B10 的 enter/over/leave 加的。上面的 WM_DROPFILES 只在**松手那一刻**回调一次，
    // 拿不到"拖动过程中"的任何信息；要边拖边汇报，只能做 OLE 的 IDropTarget。
    // 当时不走 OLE 的理由是"需求只到 drop"（见上），不是 OLE 不可行。

    /// <summary>
    /// 初始化当前线程的 OLE（拖放需要）。**每线程一次**，成对调用 <see cref="OleUninitialize"/>。
    /// </summary>
    /// <remarks>
    /// 返回 <c>S_FALSE(1)</c> 表示该线程已经初始化过，也算成功；<c>RPC_E_CHANGED_MODE</c> 才是问题
    /// （线程已被初始化成另一种套间模型）。调用方按"非负即成功"判断。
    /// </remarks>
    [LibraryImport("ole32")]
    internal static partial int OleInitialize(nint pvReserved);

    [LibraryImport("ole32")]
    internal static partial void OleUninitialize();

    /// <summary>把窗口注册成 OLE 拖放目标。失败会返回 HRESULT（<c>DRAGDROP_E_ALREADYREGISTERED</c> 表示已被占用）。</summary>
    [LibraryImport("ole32")]
    internal static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);

    [LibraryImport("ole32")]
    internal static partial int RevokeDragDrop(nint hwnd);

    /// <summary>释放 <see cref="STGMEDIUM"/>（取到 CF_HDROP 之后必须调，否则泄漏全局内存）。</summary>
    [LibraryImport("ole32")]
    internal static partial void ReleaseStgMedium(ref STGMEDIUM pmedium);

    /// <summary><c>DROPEFFECT_COPY</c>：只受理复制语义。</summary>
    internal const uint DROPEFFECT_COPY = 1;

    /// <summary><c>CF_HDROP</c>：拖放载荷里"文件列表"的剪贴板格式。</summary>
    internal const ushort CF_HDROP = 15;

    /// <summary><c>DVASPECT_CONTENT</c>：取内容（而不是缩略图/图标）。</summary>
    internal const uint DVASPECT_CONTENT = 1;

    /// <summary><c>TYMED_HGLOBAL</c>：载荷在全局内存里（HDROP 就是这个）。</summary>
    internal const uint TYMED_HGLOBAL = 1;

    // ---- 弹出菜单（user32）----

    [LibraryImport("user32")]
    internal static partial nint CreatePopupMenu();

    /// <summary>
    /// 创建**菜单栏**（与 <see cref="CreatePopupMenu"/> 的区别只在根：菜单栏是横排的顶层条，
    /// 弹出菜单是竖排的浮动列表）。子菜单两者都是 <c>CreatePopupMenu</c>。
    /// </summary>
    [LibraryImport("user32")]
    internal static partial nint CreateMenu();

    /// <summary>
    /// 把菜单栏挂到窗口上；<paramref name="menu"/> 传 0 表示摘掉。
    /// </summary>
    /// <remarks>
    /// 挂上之后客户区会**矮一个菜单栏**（<c>SM_CYMENU</c>）。装饰窗口由 <c>DefWindowProc</c>
    /// 在 <c>WM_NCCALCSIZE</c> 里自动让出来；无边框窗口要本库自己让（见 Win32WindowHost）。
    /// </remarks>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMenu(nint hwnd, nint menu);

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

    /// <summary>
    /// 取窗口的系统菜单句柄。
    /// </summary>
    /// <param name="hwnd">目标窗口。</param>
    /// <param name="revert">
    /// 传 <c>false</c>——取**当前**系统菜单。传 <c>true</c> 会把系统菜单重置回默认项
    /// （那会顺手清掉我们做过的灰化），所以本库只用 <c>false</c>。
    /// </param>
    /// <returns>
    /// 系统菜单句柄；窗口没有系统菜单（未带 <c>WS_SYSMENU</c>）时为 0。
    /// <b>注意"无边框"不等于"没有系统菜单"</b>：本库的 frameless 靠 <c>WM_NCCALCSIZE</c>
    /// 隐藏边框，样式里仍带 <c>WS_SYSMENU</c>（实测），所以那种窗口这里会返回非 0。
    /// </returns>
    [LibraryImport("user32")]
    internal static partial nint GetSystemMenu(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    /// <summary>
    /// 启用/灰化菜单项，返回**之前**的状态（-1 表示失败）。
    /// </summary>
    /// <remarks>
    /// 对系统菜单项，改完必须调 <see cref="DrawMenuBar"/>，否则菜单栏不会重画
    /// ——表现为"状态改了但看着没变"。
    /// </remarks>
    [LibraryImport("user32", EntryPoint = "EnableMenuItem")]
    internal static partial int EnableMenuItem(nint menu, uint itemId, uint enable);

    /// <summary>
    /// 读菜单项状态，返回 <c>MF_*</c> 标志位组合；<c>0xFFFFFFFF</c>（-1）表示该项不存在。
    /// </summary>
    [LibraryImport("user32", EntryPoint = "GetMenuState")]
    internal static partial uint GetMenuState(nint menu, uint itemId, uint flags);

    /// <summary>让窗口重画菜单栏（改完系统菜单项状态后必须调）。</summary>
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawMenuBar(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    // ---- 注册表写（advapi32）----

    /// <summary>
    /// 在<b>已打开的键</b>下写一个值。HKCU 下的写入不需要管理员权限；
    /// <paramref name="dataSize"/> 是字节数（字符串含结尾 NUL）。
    /// </summary>
    /// <remarks>
    /// 注意第二个参数是**值名**，不是子键路径——<c>RegSetValueEx</c> 没有子键参数，
    /// 子键要靠 <see cref="RegCreateKeyExW"/> 先打开。这里原先把它命名成 <c>subKey</c>，
    /// 调用方于是把 <c>"Software\...\Run"</c> 当值名传了进来：写入照样成功，
    /// 但值落在了 HKCU 根下，查询（走带子键的 <c>RegGetValueW</c>）永远找不到。
    /// </remarks>
    [LibraryImport("advapi32", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegSetValueExW(
        nint hkey,
        string valueName,
        uint reserved,
        uint type,
        nint data,
        uint dataSize);

    /// <summary>删注册表值（两个参数，**没有**子键参数）；值本来就不存在时返回 ERROR_FILE_NOT_FOUND(2)。</summary>
    [LibraryImport("advapi32", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegDeleteValueW(nint hkey, string valueName);

    /// <summary>
    /// 打开（不存在则创建）一个子键，返回需 <see cref="RegCloseKey"/> 释放的句柄。
    /// </summary>
    [LibraryImport("advapi32", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegCreateKeyExW(
        nint hkey,
        string subKey,
        uint reserved,
        nint className,
        uint options,
        uint desiredAccess,
        nint securityAttributes,
        out nint resultKey,
        out uint disposition);

    [LibraryImport("advapi32", EntryPoint = "RegCloseKey")]
    internal static partial int RegCloseKey(nint hkey);
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

/// <summary>
/// 文件夹选择器的参数（<c>BROWSEINFOW</c>）。
/// </summary>
/// <remarks>
/// <c>pszDisplayName</c> 指向一块调用方提供的缓冲：老 API 会把"用户在树里选中的显示名"写进去，
/// 但**它不是最终路径**（那要走 <c>SHGetPathFromIDListW</c>）——传 null 也合法，本库不读它。
/// </remarks>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct BROWSEINFOW
{
    public nint hwndOwner;
    public nint pidlRoot;
    public nint pszDisplayName;
    public nint lpszTitle;
    public uint ulFlags;
    public nint lpfn;
    public nint lParam;
    public int iImage;
}

/// <summary>
/// Win32 剪贴板 API（<c>user32</c> / <c>kernel32</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 用传统的剪贴板 API 而不是 WinRT 的 <c>Windows.ApplicationModel.DataTransfer.Clipboard</c>：
/// 后者要初始化 WinRT 公寓并引用 WinRT 投影，在 Native AOT 下是一整套额外依赖，
/// 而这几个 API 从 Windows 2000 起就没变过。
/// </para>
/// <para>
/// <b>有一处必须遵守的时序</b>：<c>OpenClipboard</c> → <c>EmptyClipboard</c> →
/// <c>SetClipboardData</c> → <c>CloseClipboard</c>；而 <c>OpenClipboard</c> 在别的进程
/// 正开着剪贴板时会**直接失败**（不是等待）。所以写操作要重试，
/// 见 <c>Win32WindowHost.WriteClipboardText</c>。
/// </para>
/// </remarks>
internal static partial class Win32Clipboard
{
    internal const uint CF_UNICODETEXT = 13;

    /// <summary>全局内存标志 <c>GMEM_MOVEABLE</c>：剪贴板要求用它分配。</summary>
    internal const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("user32", EntryPoint = "OpenClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32", EntryPoint = "CloseClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32", EntryPoint = "EmptyClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32", EntryPoint = "GetClipboardData", SetLastError = true)]
    internal static partial nint GetClipboardData(uint uFormat);

    [LibraryImport("user32", EntryPoint = "SetClipboardData", SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32", EntryPoint = "IsClipboardFormatAvailable", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("kernel32", EntryPoint = "GlobalAlloc", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32", EntryPoint = "GlobalLock", SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32", EntryPoint = "GlobalFree", SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);
}


