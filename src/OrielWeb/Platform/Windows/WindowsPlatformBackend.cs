using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 平台后端：Win32 消息循环 + 主线程调度（消息专用窗口）+ 窗口工厂。
/// </summary>
internal sealed unsafe class WindowsPlatformBackend : IPlatformBackend
{
    private const string MessageWindowClassName = "OrielWeb_MsgWindow";

    private static int s_messageClassRegistered;
    private static readonly object s_messageClassGate = new();

    private readonly uint _uiThreadId;
    private readonly nint _messageHwnd;
    private int _aliveWindows;
    private int _ran;

    // 托盘与通知**不再**借用调度窗口：那个窗口的消息空间与 WebView2 共享，
    // 自选的 WM_APP + n 会与它的私有消息撞车（见 Win32MessageWindow 的说明）。
    // 两者各自持有 Win32MessageWindow。

    /// <param name="userDataFolder">
    /// 覆盖 WebView2 的 user data folder（<see cref="OrielAppBuilder.UseUserDataFolder"/>）；
    /// 为空则用 <see cref="DefaultUserDataFolderFor"/>。它必须是**全进程一个**：环境按它创建，见
    /// <see cref="GetEnvironmentAsync"/>。
    /// </param>
    /// <param name="appIdentifier">
    /// 应用标识符，只在 <paramref name="userDataFolder"/> 为空时用来拼默认目录。
    /// 省略时退化成一个固定名——直接 new 本类的场景（测试、工具）不会配它。
    /// </param>
    public WindowsPlatformBackend(string? userDataFolder, string? appIdentifier = null)
    {
        _userDataFolder = string.IsNullOrWhiteSpace(userDataFolder)
            ? DefaultUserDataFolderFor(appIdentifier ?? DefaultAppIdentifier)
            : userDataFolder;
        EnsureDpiAwareness();
        EnsureMessageWindowClass();

        // OLE 拖放（IDropTarget）要求本线程初始化过 OLE。重复初始化返回 S_FALSE(1)，按"非负即成功"处理；
        // 真失败（如 RPC_E_CHANGED_MODE，线程已被别的套间模型占住）时拖放退回 WM_DROPFILES，
        // 其余功能不受影响——所以这里只报告，不抛。
        int oleResult = Win32.OleInitialize(0);
        if (oleResult < 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] OleInitialize 失败：0x{oleResult:X8}——拖放退回 WM_DROPFILES（只有 drop）。");
        }

        _uiThreadId = Win32.GetCurrentThreadId();
        // lpParam 传 0：MessageWindowProc 只处理 WM_APP_DISPATCH（要被执行的 Action 随消息的
        // lParam 传递），从不读取创建参数或 GWLP_USERDATA，因此无需分配 GCHandle
        _messageHwnd = Win32.CreateWindowExW(
            0, RegisterClassNamePtr(MessageWindowClassName), 0, 0,
            0, 0, 0, 0,
            Win32Constants.HWND_MESSAGE, 0, Win32.GetModuleHandleW(null),
            null);
        if (_messageHwnd == 0)
        {
            throw new InvalidOperationException($"创建 OrielWeb 调度窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }

        // Win32 消息循环本身没有 SynchronizationContext：安装后，await 的续体会被 Post 回
        // UI 线程队列，这是 WebView2 生成绑定异步装配（Task 化）能正确工作的前提。
        // （与 Linux/macOS 共用 MainThreadSynchronizationContext，三平台续体语义一致。）
        MainThreadSynchronizationContext.Install(action => PostToMainThread(action));

        // 主题：记下初值（否则"启动时已是深色"会漏报一次），并让静态 WndProc 能回调到本实例
        // （消息窗口的 WndProc 必须是静态的，见 MessageWindowProc）。
        _lastTheme = CurrentTheme;
        s_current = this;
    }

    // ---- WebView2 环境（全进程共享）----

    /// <summary>
    /// 全进程共享的 WebView2 环境（惰性创建，只建一次）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一个进程里每个窗口各建一个环境是不行的</b>：环境的 user data folder 是**全进程同一个目录**，
    /// 而 WebView2 不允许同一目录上并存两个环境——第二个会以"资源状态不对"失败，
    /// 于是"运行时再开一个窗口"在 Windows 上直接起不来。正确形态是**一份环境 + 多个控制器**。
    /// </para>
    /// <para>
    /// 缓存的是 <see cref="Task{TResult}"/> 而不是结果：多个窗口同时首次装配时，只会真正创建一个环境，
    /// 后来者 await 同一个任务。创建与读取都只在 UI 线程发生。
    /// </para>
    /// </remarks>
    private Task<IComObject<ICoreWebView2Environment>?>? _environment;

    /// <summary>标识符缺失时的退化名（与 <c>path.*</c> 内建命令的退化名一致）。</summary>
    private const string DefaultAppIdentifier = "oriel-app";

    /// <summary>默认的 user data folder：<c>%LOCALAPPDATA%\%3C标识符%3E\WebView2</c>。</summary>
    /// <remarks>
    /// <para>
    /// <b>按应用隔离，不按库共用。</b>此前这里是写死的 <c>%LOCALAPPDATA%\OrielWeb\WebView2</c>——
    /// 于是"两个基于本库的应用同时打开"必然撞车：目录只有一个，而 WebView2 不允许两个进程
    /// 同时打开同一个用户数据目录，后启动的那个直接以 <c>0x800700AA</c>（ERROR_BUSY）装配失败。
    /// 而 <see cref="OrielAppInfo.Identifier"/> 的文档从 0.3 起就写着"它将成为数据目录的组成部分"，
    /// 唯独 WebView2 这份目录没跟上——两处说法不一致，错的是这里。
    /// </para>
    /// <para>
    /// 标识符在 <c>Build()</c> 时已按"能安全当目录名"校验过（<see cref="OrielAppInfo.Validate"/>），
    /// 所以这里可以直接拼——不存在 <c>..</c> 或分隔符跳目录的可能。
    /// </para>
    /// <para>
    /// 代价是老目录里已有的 profile（localStorage / cookie）不再被读到。这是有意的：
    /// 一份全库共用的 profile 本身就不该存在，迁移它等于把错误的东西搬到正确的位置。
    /// </para>
    /// </remarks>
    internal static string DefaultUserDataFolderFor(string appIdentifier) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        appIdentifier,
        Win32WindowHost.WebView2UserDataFolderName);

    /// <summary>本次应用的 user data folder（<c>UseUserDataFolder</c> 覆盖过就用它）。</summary>
    /// <remarks>失败提示要用它——只说"被占用"而不给路径，等于让人自己去猜是哪个目录。</remarks>
    internal string UserDataFolder => _userDataFolder;

    private readonly string _userDataFolder;

    /// <summary>取（必要时创建）共享环境。必须在 UI 线程调用。</summary>
    /// <remarks>
    /// 这里刻意不写成 <c>async</c>/<c>await</c>：本类是 <c>unsafe</c> 上下文，在其中 await 会报 CS4004。
    /// 判空与继续 await 由窗口那侧完成（那边不是 unsafe）。
    /// </remarks>
    internal Task<IComObject<ICoreWebView2Environment>?> GetEnvironmentAsync(string? browserFolder)
        => _environment ??= Functions.CreateCoreWebView2EnvironmentWithOptionsAsync(
            browserFolder, _userDataFolder, Win32AssetScheme.EnvironmentOptions);

    // ---- 系统主题 ----
    // 应用级（而非窗口级）：主题是系统状态，与具体窗口无关。

    private static WindowsPlatformBackend? s_current;
    private OrielTheme _lastTheme = OrielTheme.Light;

    public event Action<OrielTheme>? ThemeChanged;

    public OrielTheme CurrentTheme => ReadAppsUseLightTheme() == 0 ? OrielTheme.Dark : OrielTheme.Light;

    /// <summary>
    /// 读 <c>HKCU\…\Themes\Personalize\AppsUseLightTheme</c>：1 = 浅色、0 = 深色。
    /// 键不存在（旧系统或未设置）时返回 null，调用方按浅色处理。
    /// </summary>
    private static unsafe int? ReadAppsUseLightTheme()
    {
        uint size = sizeof(int);
        int value = 0;
        int status = Win32.RegGetValueW(
            Win32Constants.HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            Win32Constants.RRF_RT_REG_DWORD,
            0,
            (nint)(&value),
            ref size);
        return status == 0 ? value : null;
    }

    /// <summary>重新读主题，变化了才上报（去重：WM_SETTINGCHANGE 会因很多原因发来）。</summary>
    private void RaiseThemeIfChanged()
    {
        OrielTheme theme = CurrentTheme;
        if (theme == _lastTheme)
        {
            return;
        }

        _lastTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets)
    {
        var host = Win32WindowHost.Create(window, options, app, assets, this);

        Interlocked.Increment(ref _aliveWindows);

        return host;
    }

    // ---- 开机自启（HKCU 的 Run 键）----

    public bool EnableAutoStart(string id, IReadOnlyList<string>? arguments) => Win32AutoStart.Enable(id, arguments);

    public bool DisableAutoStart(string id) => Win32AutoStart.Disable(id);

    public bool IsAutoStartEnabled(string id) => Win32AutoStart.IsEnabled(id);

    public void RunMessageLoop()
    {
        // 与 Linux/macOS 后端一致：重复调用直接返回，不嵌套第二个消息循环
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }

        while (true)
        {
            int result = Win32.GetMessageW(out var message, 0, 0, 0);
            if (result == 0)
            {
                return; // WM_QUIT：正常退出
            }

            if (result == -1)
            {
                // GetMessageW 的 -1（出错）与 0（WM_QUIT）是两回事：以前一并用 `> 0` 落出循环，
                // 应用"毫无理由地退出"且不留痕迹。这里记一笔，并补上退出消息——同线程上还有
                // 别的消息循环在跑时（多窗口、嵌套会话）它才等到 WM_QUIT 而不是静默落空（评审 P3）。
                OrielDiagnostics.Report("[OrielWeb] GetMessageW 失败，消息循环退出。");
                Win32.PostQuitMessage(1);
                return;
            }

            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// 创建托盘。这里**不持有**托盘对象：托盘的归属与释放统一由 <see cref="OrielApp"/> 负责
    /// （见 <see cref="OrielApp.Dispose"/>），三个后端在这一点上写法保持一致。
    /// </summary>
    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app, string id)
        => new Win32TrayBackend(app, options, id);

    /// <summary>
    /// 显示器查询（见 <see cref="IMonitorBackend"/>）。
    /// </summary>
    /// <remarks>
    /// 无状态，因此可以直接复用同一个实例——枚举发生在每次调用里，
    /// 所以热插拔/改分辨率之后立刻就能看到新值，不需要失效逻辑。
    /// </remarks>
    public IMonitorBackend Monitors { get; } = new WindowsMonitorBackend();

    // ---- 通知 ----

    /// <summary>
    /// Windows 的通知始终可用：走 WinRT toast（经 PowerShell 调用，见 <see cref="Win32ToastNotification"/>）。
    /// </summary>
    public bool NotificationsSupported => true;

    public bool ShowNotification(OrielNotificationOptions notification, string appId)
        => Win32ToastNotification.Send(notification, appId);

    // ---- 系统剪贴板 ----

    /// <summary>
    /// 读剪贴板文本；没有文本内容时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先问 <c>IsClipboardFormatAvailable</c> 再取：剪贴板里可能是图片或文件列表，
    /// 那时 <c>GetClipboardData(CF_UNICODETEXT)</c> 返回 0，而 0 与"取失败"无法区分。
    /// 先问一句能把"没有文本"与"读失败"分开。
    /// </para>
    /// <para>
    /// <c>OpenClipboard</c> 失败时**重试几次**：别的进程正开着剪贴板是正常状态
    /// （它是个全局独占资源），而这个状态通常在几毫秒内结束。
    /// 不重试的话表现是"偶发读不到剪贴板"，那种偶发最难查。
    /// </para>
    /// </remarks>
    public string? ReadClipboardText()
    {
        if (!Win32Clipboard.IsClipboardFormatAvailable(Win32Clipboard.CF_UNICODETEXT))
        {
            return null;
        }

        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            nint handle = Win32Clipboard.GetClipboardData(Win32Clipboard.CF_UNICODETEXT);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = Win32Clipboard.GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                // CF_UNICODETEXT 是 NUL 结尾的 UTF-16，所以按字符串读（不需要知道长度）
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                _ = Win32Clipboard.GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>写剪贴板文本。</summary>
    /// <remarks>
    /// <para>
    /// <c>SetClipboardData</c> 成功之后那块 <c>GMEM_MOVEABLE</c> 内存的**所有权归系统**，
    /// 不能再 <c>GlobalFree</c>——释放了会让系统在粘贴时读到已释放的内存。
    /// 所以失败路径才释放，成功路径不释放（下面那两处注释就是这件事）。
    /// </para>
    /// </remarks>
    public bool WriteClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            if (!Win32Clipboard.EmptyClipboard())
            {
                return false;
            }

            // +1 是 NUL 结尾
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            nint handle = Win32Clipboard.GlobalAlloc(Win32Clipboard.GMEM_MOVEABLE, bytes);
            if (handle == 0)
            {
                return false;
            }

            bool transferred = false;

            try
            {
                nint pointer = Win32Clipboard.GlobalLock(handle);
                if (pointer == 0)
                {
                    return false;
                }

                try
                {
                    unsafe
                    {
                        fixed (char* source = text)
                        {
                            Buffer.MemoryCopy(source, (void*)pointer, bytes, (nuint)(text.Length * sizeof(char)));
                        }
                    }

                    // 自己补 NUL：MemoryCopy 只搬了 text.Length 个字符
                    Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
                }
                finally
                {
                    _ = Win32Clipboard.GlobalUnlock(handle);
                }

                if (Win32Clipboard.SetClipboardData(Win32Clipboard.CF_UNICODETEXT, handle) == 0)
                {
                    return false;
                }

                // 交出去了：从此系统拥有这块内存
                transferred = true;
                return true;
            }
            finally
            {
                if (!transferred)
                {
                    _ = Win32Clipboard.GlobalFree(handle);
                }
            }
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>清空剪贴板。</summary>
    public bool ClearClipboard()
    {
        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            return Win32Clipboard.EmptyClipboard();
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    // ---- 剪贴板：图片 / HTML / 敏感文本（B19）----

    /// <summary>
    /// 读剪贴板里的位图（<c>CF_DIB</c>）并编码成 PNG。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只认 <b>BI_RGB 的 24/32 位</b>：那是截图工具、画图、浏览器复制图片时实际会放的形态。
    /// 其它（调色板位图、RLE、JPEG/PNG 私有格式）如实返回 <c>null</c>——
    /// 硬猜格式的后果是给出一张颜色错乱的图，而调用方无从判断。
    /// </para>
    /// <para>
    /// <b>32 位 DIB 的 alpha 常常全是 0</b>（<c>CF_DIB</c> 不带 alpha 语义）：直接按原值转换会得到
    /// 一张全透明的图。所以这里检查一遍——全 0 就当作不透明。
    /// </para>
    /// </remarks>
    public byte[]? ReadClipboardImage()
    {
        if (!Win32Clipboard.IsClipboardFormatAvailable(Win32Clipboard.CF_DIB))
        {
            return null;
        }

        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            nint handle = Win32Clipboard.GetClipboardData(Win32Clipboard.CF_DIB);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = Win32Clipboard.GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                int size = (int)Win32Clipboard.GlobalSize(handle);
                if (size <= 0)
                {
                    return null;
                }

                return DecodeDibToPng(new ReadOnlySpan<byte>((void*)pointer, size));
            }
            finally
            {
                _ = Win32Clipboard.GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>
    /// 把 PNG 写进剪贴板：<c>CF_DIB</c>（兼容性最好）+ 注册格式 <c>PNG</c>（保留 alpha）。
    /// </summary>
    /// <remarks>
    /// 两种格式都给：<c>CF_DIB</c> 是老程序的共同语言（画图、Word、资源管理器），
    /// 而 <c>PNG</c> 是浏览器与新编辑器优先取的（带 alpha，且不用我们来选色深）。
    /// 只给其中一种会让另一半粘贴目标拿到一张透明/失真或根本没有的图。
    /// </remarks>
    public bool WriteClipboardImage(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        OrielImage image;
        try
        {
            image = OrielImage.FromBytes(png);
        }
        catch (OrielImageFormatException)
        {
            // 命令层应当已经校验过；走到这里说明调用方绕过了它
            return false;
        }

        byte[] dib = BuildDib(image.Rgba.Span, image.Width, image.Height);

        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            if (!Win32Clipboard.EmptyClipboard())
            {
                return false;
            }

            bool ok = TrySetClipboardBytes(Win32Clipboard.CF_DIB, dib);

            uint pngFormat = Win32Clipboard.RegisterClipboardFormat("PNG");
            if (pngFormat != 0)
            {
                ok |= TrySetClipboardBytes(pngFormat, png);
            }

            return ok;
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>写 CF_HTML + 纯文本回退。</summary>
    public bool WriteClipboardHtml(string html, string? altText)
    {
        ArgumentNullException.ThrowIfNull(html);

        uint htmlFormat = Win32Clipboard.RegisterClipboardFormat("HTML Format");
        if (htmlFormat == 0)
        {
            return false;
        }

        byte[] payload = OrielClipboardHtml.Build(html);

        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            if (!Win32Clipboard.EmptyClipboard())
            {
                return false;
            }

            bool ok = TrySetClipboardBytes(htmlFormat, payload);

            // 纯文本回退：记事本、终端、不支持 HTML 的输入框拿到的是这个
            ok |= TrySetClipboardText(altText ?? html);
            return ok;
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>
    /// 写敏感文本：先放三个"别记录"的标记格式，再放文本。
    /// </summary>
    /// <remarks>
    /// 标记格式来自 Windows 10 的剪贴板历史/云同步协议（<c>CanIncludeInClipboardHistory</c>、
    /// <c>CanUploadToCloudClipboard</c>、<c>ExcludeClipboardContentFromMonitorProcessing</c>）。
    /// 三个都要给：前两个管"历史与云同步"，第三个管"别被剪贴板查看器抓"。
    /// 它们都只是**请求**——系统或第三方剪贴板管理器可以不理（与 Tauri 的说明一致）。
    /// </remarks>
    public bool WriteClipboardSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            if (!Win32Clipboard.EmptyClipboard())
            {
                return false;
            }

            uint history = Win32Clipboard.RegisterClipboardFormat("CanIncludeInClipboardHistory");
            if (history != 0)
            {
                _ = TrySetClipboardDwordZero(history);
            }

            uint cloud = Win32Clipboard.RegisterClipboardFormat("CanUploadToCloudClipboard");
            if (cloud != 0)
            {
                _ = TrySetClipboardDwordZero(cloud);
            }

            uint exclude = Win32Clipboard.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            if (exclude != 0)
            {
                _ = TrySetClipboardDwordZero(exclude);
            }

            return TrySetClipboardText(text);
        }
        finally
        {
            _ = Win32Clipboard.CloseClipboard();
        }
    }

    // ------------------------------------------------------------------ 应用级（B23）

    /// <summary>
    /// Dock 图标显隐（见 <see cref="IPlatformBackend.SetDockVisibility"/>）。
    /// </summary>
    /// <remarks>
    /// Windows 上**没有**这个能力：任务栏按钮属于单个窗口（<c>window.setSkipTaskbar</c>），
    /// 而"任务栏"本身是 Explorer 的。抛 <see cref="NotSupportedException"/> 而不是静默无效
    /// ——静默无效会让调用方以为"隐藏成功了"，然后在截图里看到它。
    /// </remarks>
    public void SetDockVisibility(bool visible)
        => throw new NotSupportedException(
            "setDockVisibility 只有 macOS 有：Windows 的任务栏按钮属于单个窗口，"
            + "用 window.setSkipTaskbar 隐藏它；整个任务的图标由 Explorer 决定。");

    /// <summary>Windows 没有"按标识符隔离的浏览数据"（WebView2 用整个用户数据目录隔离）。</summary>
    public bool SupportsDataStoreIdentifiers => false;

    /// <inheritdoc />
    public void RemoveDataStoreData(Guid identifier)
        => throw new NotSupportedException(
            "数据存储标识符只有 macOS 的 WKWebsiteDataStore 有："
            + "Windows 的 WebView2 用整个用户数据目录隔离（见 UseUserDataFolder），没有按 UUID 分库的模型。");

    /// <summary>把一段字节放进剪贴板（调用方已 <c>OpenClipboard</c> 且 <c>EmptyClipboard</c>）。</summary>
    /// <remarks>
    /// <c>SetClipboardData</c> 成功后那块 <c>GMEM_MOVEABLE</c> 内存的**所有权归系统**，
    /// 不能再释放——释放了系统在粘贴时会读到已释放的内存。所以只有失败路径才 <c>GlobalFree</c>。
    /// </remarks>
    private static bool TrySetClipboardBytes(uint format, ReadOnlySpan<byte> data)
    {
        nint handle = Win32Clipboard.GlobalAlloc(Win32Clipboard.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == 0)
        {
            return false;
        }

        bool transferred = false;
        try
        {
            nint pointer = Win32Clipboard.GlobalLock(handle);
            if (pointer == 0)
            {
                return false;
            }

            try
            {
                data.CopyTo(new Span<byte>((void*)pointer, data.Length));
            }
            finally
            {
                _ = Win32Clipboard.GlobalUnlock(handle);
            }

            if (Win32Clipboard.SetClipboardData(format, handle) == 0)
            {
                return false;
            }

            transferred = true;
            return true;
        }
        finally
        {
            if (!transferred)
            {
                _ = Win32Clipboard.GlobalFree(handle);
            }
        }
    }

    /// <summary>把文本（UTF-16、NUL 结尾）放进剪贴板。</summary>
    private static bool TrySetClipboardText(string text)
    {
        // +1 是 NUL 结尾
        nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
        nint handle = Win32Clipboard.GlobalAlloc(Win32Clipboard.GMEM_MOVEABLE, bytes);
        if (handle == 0)
        {
            return false;
        }

        bool transferred = false;
        try
        {
            nint pointer = Win32Clipboard.GlobalLock(handle);
            if (pointer == 0)
            {
                return false;
            }

            try
            {
                fixed (char* source = text)
                {
                    Buffer.MemoryCopy(source, (void*)pointer, bytes, (nuint)(text.Length * sizeof(char)));
                }

                // 自己补 NUL：MemoryCopy 只搬了 text.Length 个字符
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                _ = Win32Clipboard.GlobalUnlock(handle);
            }

            if (Win32Clipboard.SetClipboardData(Win32Clipboard.CF_UNICODETEXT, handle) == 0)
            {
                return false;
            }

            transferred = true;
            return true;
        }
        finally
        {
            if (!transferred)
            {
                _ = Win32Clipboard.GlobalFree(handle);
            }
        }
    }

    /// <summary>往剪贴板里放一个值为 0 的 DWORD（剪贴板历史/云同步的"允许"开关用）。</summary>
    private static bool TrySetClipboardDwordZero(uint format)
    {
        Span<byte> zero = stackalloc byte[4];
        return TrySetClipboardBytes(format, zero);
    }

    /// <summary>
    /// <c>CF_DIB</c> → PNG。只认 BI_RGB 的 24/32 位（见 <see cref="ReadClipboardImage"/>）。
    /// </summary>
    private static byte[]? DecodeDibToPng(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        int headerSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dib[..4]);
        if (headerSize < 40 || dib.Length < headerSize)
        {
            return null;
        }

        int rawWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dib[4..8]);
        int rawHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dib[8..12]);
        ushort bitCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dib[14..16]);
        uint compression = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dib[16..20]);

        bool topDown = rawHeight < 0;
        int width = rawWidth;
        int height = Math.Abs(rawHeight);

        if (width <= 0 || height <= 0 || compression != 0 || (bitCount != 24 && bitCount != 32))
        {
            return null;
        }

        int bytesPerPixel = bitCount / 8;
        int sourceStride = ((width * bitCount + 31) / 32) * 4;
        if (dib.Length < (long)headerSize + ((long)sourceStride * height))
        {
            return null;
        }

        var rgba = new byte[width * height * 4];
        bool anyAlpha = false;

        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            int source = headerSize + (sourceRow * sourceStride);
            int target = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int s = source + (x * bytesPerPixel);
                byte b = dib[s];
                byte g = dib[s + 1];
                byte r = dib[s + 2];
                byte a = bytesPerPixel == 4 ? dib[s + 3] : (byte)255;

                if (a != 0)
                {
                    anyAlpha = true;
                }

                rgba[target + (x * 4)] = r;
                rgba[target + (x * 4) + 1] = g;
                rgba[target + (x * 4) + 2] = b;
                rgba[target + (x * 4) + 3] = a;
            }
        }

        if (bytesPerPixel == 4 && !anyAlpha)
        {
            // CF_DIB 不携带 alpha 语义：全 0 表示"没给"，不是"全透明"
            for (int i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }
        }

        return OrielPngEncoder.Encode(rgba, width, height);
    }

    /// <summary>RGBA → <c>CF_DIB</c>（40 字节头 + 自下而上的 BGRA/BGR 行）。</summary>
    /// <remarks>
    /// 一律写 32 位：24 位要按 4 字节对齐补位，除了省一点内存没有任何好处，
    /// 而 32 位是所有粘贴目标都认的形态。
    /// </remarks>
    private static byte[] BuildDib(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int stride = width * 4;
        var dib = new byte[40 + (stride * height)];

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), height); // 正数 = 自下而上
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1);    // planes
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 32);   // bpp
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16, 4), 0);    // BI_RGB
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20, 4), stride * height);

        for (int y = 0; y < height; y++)
        {
            int source = y * stride;
            int target = 40 + ((height - 1 - y) * stride);

            for (int x = 0; x < width; x++)
            {
                int s = source + (x * 4);
                int t = target + (x * 4);
                dib[t] = rgba[s + 2];      // B
                dib[t + 1] = rgba[s + 1];  // G
                dib[t + 2] = rgba[s];      // R
                dib[t + 3] = rgba[s + 3];  // A
            }
        }

        return dib;
    }

    /// <summary>
    /// 打开剪贴板，失败时重试几次。
    /// </summary>
    /// <remarks>
    /// 剪贴板是**全局独占**资源：别的进程开着时 <c>OpenClipboard</c> 立刻失败（不等待）。
    /// 那个状态通常在毫秒级结束，所以重试 5 次 × 10ms 足够覆盖绝大多数情况；
    /// 真占着不放（比如某个剪贴板管理器卡住了）时如实失败，让调用方决定怎么办。
    /// </remarks>
    private static bool TryOpenClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (Win32Clipboard.OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    public void Quit() => Win32.PostQuitMessage(0);

    public bool IsOnUiThread() => Win32.GetCurrentThreadId() == _uiThreadId;

    public bool PostToMainThread(Action action)
    {
        var handle = GCHandle.Alloc(action);
        if (!Win32.PostMessageW(_messageHwnd, Win32Constants.WM_APP_DISPATCH, 0, GCHandle.ToIntPtr(handle)))
        {
            handle.Free();
            // 调度窗口失效（消息循环已结束、窗口被销毁）：动作不会执行。返回 false 并留痕，
            // 让"投了但没生效"可被观测，而不是静默丢弃（下游反馈 2026-10-14 的 P2-5）。
            OrielDiagnostics.Report("[OrielWeb] PostToMainThread 失败：调度窗口已失效，回调已被丢弃。");
            return false;
        }

        return true;
    }

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            Win32.PostQuitMessage(0);
        }
    }

    /// <summary>
    /// 后端自身只持有共享的 WebView2 环境；托盘归 <see cref="OrielApp"/> 所有并由它释放
    /// （托盘会连带释放自己的消息窗口），调度窗口随消息循环结束销毁。
    /// </summary>
    public void Dispose()
    {
        // 共享环境比任何单个窗口活得久，所以窗口销毁时**不**释放它（见 Win32WindowHost 的清理），
        // 只在这里、应用退出时释放一次。
        if (_environment is { IsCompletedSuccessfully: true } environment)
        {
            environment.Result?.Dispose();
        }

        _environment = null;

        // 设置变化的回调走静态 s_current（消息窗口的 WndProc 必须静态）：不清空的话，
        // 同一进程里重建后端会把主题事件继续送到这个已释放的实例上（评审 P3）。
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    // ---- 消息窗口 ----

    [UnmanagedCallersOnly]
    private static nint MessageWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == Win32Constants.WM_APP_DISPATCH)
        {
            // 先取回并释放 GCHandle，避免 Action 抛异常时句柄泄漏
            var handle = GCHandle.FromIntPtr(lParam);
            Action? action;
            try
            {
                action = (Action)handle.Target!;
            }
            finally
            {
                handle.Free();
            }

            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                // PostToMainThread 是 public API，用户 Action 的异常绝不能穿越原生边界
                // （外泄 = 进程 fail-fast，不可捕获）
                OrielDiagnostics.Report($"[OrielWeb] PostToMainThread 回调抛出异常：{ex}");
            }
            return 0;
        }
        // 托盘的 WM_APP_TRAY 与通知的 WM_APP_NOTIFY 不在这里处理：
        // 那个消息号会与 WebView2 的私有消息撞车，所以它们各自用专属窗口
        //（见 Win32MessageWindow），不再共享本窗口的消息空间。
        if (message == Win32Constants.WM_SETTINGCHANGE)
        {
            // 主题切换会带 "ImmersiveColorSet" 参数；其它设置变化（环境变量、区域等）不理会
            if (lParam != 0
                && Marshal.PtrToStringUni(lParam) is { } changed
                && changed.Contains("ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
            {
                s_current?.RaiseThemeIfChanged();
            }
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnsureMessageWindowClass()
    {
        if (Volatile.Read(ref s_messageClassRegistered) == 1)
        {
            return;
        }

        lock (s_messageClassGate)
        {
            if (Volatile.Read(ref s_messageClassRegistered) == 1)
            {
                return;
            }

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&MessageWindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = RegisterClassNamePtr(MessageWindowClassName),
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"注册 OrielWeb 调度窗口类失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            // 注册成功后才置位（理由同 Win32WindowHost.EnsureWindowClass）
            Volatile.Write(ref s_messageClassRegistered, 1);
        }
    }

    private static void EnsureDpiAwareness()
    {
        try
        {
            _ = Win32.SetProcessDpiAwarenessContext(Win32Constants.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // 旧系统无此 API，忽略
        }
    }

    private static readonly Dictionary<string, nint> s_classNamePtrs = new(StringComparer.Ordinal);

    private static nint RegisterClassNamePtr(string className)
    {
        lock (s_classNamePtrs)
        {
            if (!s_classNamePtrs.TryGetValue(className, out var pointer))
            {
                pointer = Marshal.StringToHGlobalUni(className);
                s_classNamePtrs[className] = pointer;
            }
            return pointer;
        }
    }
}
