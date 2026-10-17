using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 平台后端：NSApplication 生命周期 + 主线程调度 + 窗口工厂。
/// </summary>
internal sealed unsafe class MacOSPlatformBackend : IPlatformBackend
{
    private nint _nsApp;
    private nint _appDelegate;
    private nint _pumpHelper;
    private int _aliveWindows;
    private int _ran;

    private static readonly ConcurrentQueue<Action> MainThreadQueue = MacOSObjCClasses.MainThreadQueue;
    // 主线程身份：托管线程 ID 在同一原生线程上保持稳定，足够用于回执快速路径判断
    private readonly int _mainManagedThreadId = Environment.CurrentManagedThreadId;

    public MacOSPlatformBackend()
    {
        // 必须最先做：纯 P/Invoke 的可执行文件不链接任何框架，不显式加载的话
        // objc_getClass 全部返回 nil，后续 objc_msgSend 退化为静默 no-op——
        // 进程会"正常"退出但从未建出窗口（见 ObjCRuntime.LoadFrameworks 的说明）。
        ObjCRuntime.LoadFrameworks();

        _nsApp = ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        if (_nsApp == 0)
        {
            throw new InvalidOperationException("NSApplication.sharedApplication 返回 nil：AppKit 未能正常初始化。");
        }
        // NSApplicationActivationPolicyRegular = 0（普通应用，出现在 Dock）
        ObjCRuntime.SendVoidNint(_nsApp, ObjCRuntime.Sel("setActivationPolicy:"), 0);

        _appDelegate = MacOSObjCClasses.CreateAppDelegate();
        ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("setDelegate:"), _appDelegate);

        _pumpHelper = MacOSObjCClasses.CreatePumpHelper();

        // NSApplication 主循环没有 SynchronizationContext：安装后 async 命令 await 的续体
        // 回主线程，与 Windows 语义一致（命令里 await 之后可以直接碰 UI/平台对象）。见 API.md。
        // 必须在 pump helper 建好之后：PostToMainThread 依赖它排空队列。
        MainThreadSynchronizationContext.Install(action => PostToMainThread(action));

        // 主题：先记初值（否则"启动时已是深色"会漏报一次），再订系统通知。
        // NSDistributedNotificationCenter 的 addObserver:selector:name:object: **不**持有观察者，
        // 所以必须自己留着引用（_themeObserver），否则通知到达时对象已释放。
        _lastTheme = CurrentTheme;
        _themeObserver = MacOSObjCClasses.CreateThemeObserver(this);
        nint center = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSDistributedNotificationCenter"),
            ObjCRuntime.Sel("defaultCenter"));
        ObjCRuntime.SendVoidObjSelObjObj(
            center,
            ObjCRuntime.Sel("addObserver:selector:name:object:"),
            _themeObserver,
            ObjCRuntime.Sel("orielThemeChanged:"),
            ObjCRuntime.MakeNSString("AppleInterfaceThemeChangedNotification"),
            0);
    }

    // ---- 系统主题 ----

    private OrielTheme _lastTheme = OrielTheme.Light;
    private nint _themeObserver;

    public event Action<OrielTheme>? ThemeChanged;

    public OrielTheme CurrentTheme => IsDarkTheme() ? OrielTheme.Dark : OrielTheme.Light;

    /// <summary>重新读主题，变化了才上报。</summary>
    internal void RaiseThemeIfChanged()
    {
        OrielTheme theme = CurrentTheme;
        if (theme == _lastTheme)
        {
            return;
        }

        _lastTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// 判定当前是否为深色：<c>NSUserDefaults</c> 的 <c>AppleInterfaceStyle</c> **仅在深色时存在**
    /// （值为 "Dark"），浅色时该键不存在——所以"取不到"就是浅色，不是失败。
    /// </summary>
    private static bool IsDarkTheme()
    {
        nint defaults = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSUserDefaults"), ObjCRuntime.Sel("standardUserDefaults"));
        if (defaults == 0)
        {
            return false;
        }

        nint value = ObjCRuntime.SendIdObj(
            defaults, ObjCRuntime.Sel("stringForKey:"), ObjCRuntime.MakeNSString("AppleInterfaceStyle"));
        if (value == 0)
        {
            return false;
        }

        string? style = ObjCRuntime.ToManagedString(value);
        return style is not null && style.Contains("Dark", StringComparison.OrdinalIgnoreCase);
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets)
    {
        var host = new MacOSWindowHost(window, options, app, assets, this);

        // 构造函数只装配宿主，真正的 NSWindow 创建（initWithContentRect → 挂 delegate/webview → orderFront）在这里完成。
        // Windows 后端的建窗在静态工厂内完成，Linux/macOS 后端是实例方法，必须显式调用。
        // 未真机验证：本修复与 Linux 侧同源（见 API.md「Linux 首次真机运行验证」），待有 Mac 环境时复验。
        host.Create();

        // 计数在建窗**成功之后**才自增（与 Linux 侧同款，评审 P3）：Create 抛异常时窗口并不存在。
        _aliveWindows++;
        return host;
    }

    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app, string id)
        => new MacOSTrayBackend(app, options, id);

    // ---- 通知 ----

    /// <summary>macOS 自带 osascript，因此通知总是可用（未打包运行时的唯一可行路径）。</summary>
    public bool NotificationsSupported => MacOSNotificationSender.IsAvailable;

    /// <summary>
    /// 投递通知。<paramref name="appId"/> 被**刻意忽略**：<c>osascript</c> 投递的通知归属于
    /// Script Editor（未打包运行时），应用名由系统决定，AppleScript 没有可传应用标识的入口。
    /// 这与 Windows/Linux 的差异是平台事实，不是漏实现——要按应用分组得进 .app bundle 后用
    /// <c>UNUserNotificationCenter</c>（已记入 ROADMAP）。
    /// </summary>
    public bool ShowNotification(OrielNotificationOptions notification, string appId)
    {
        _ = appId;
        return MacOSNotificationSender.Send(notification);
    }

    // ---- 系统剪贴板（NSPasteboard）----

    /// <summary>UTF-8 文本的粘贴板类型（<c>NSPasteboardTypeString</c>）。</summary>
    private const string NSPasteboardTypeString = "public.utf8-plain-text";

    /// <summary>
    /// 取 <c>[NSPasteboard generalPasteboard]</c>。
    /// </summary>
    /// <remarks>
    /// <c>generalPasteboard</c> 是**类方法**（<c>+</c>），所以 self 传的是类对象而不是实例：
    /// <c>objc_getClass("NSPasteboard")</c>。传 0 会得到 nil 并让后面所有调用静默失败
    /// （症状是"剪贴板永远是空的"）。
    /// </remarks>
    private static nint GeneralPasteboard()
    {
        nint pasteboardClass = ObjCRuntime.GetClass("NSPasteboard");
        return pasteboardClass == 0
            ? 0
            : ObjCRuntime.SendId(pasteboardClass, ObjCRuntime.Sel("generalPasteboard"));
    }

    /// <summary>
    /// 读剪贴板文本；没有文本内容时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// AppKit 上的"读剪贴板"就是这两步：问一遍有没有这个类型，再取。
    /// 返回的 NSString 由 AppKit 持有（autorelease），这里只读它的 UTF-8 字节，不接管所有权。
    /// </remarks>
    public string? ReadClipboardText()
    {
        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return null;
        }

        nint type = ObjCRuntime.MakeNSString(NSPasteboardTypeString);

        // stringForType: 返回 NSString* 或 nil（剪贴板里没有该类型时）
        nint text = ObjCRuntime.SendIdObj(pasteboard, ObjCRuntime.Sel("stringForType:"), type);
        return text == 0 ? null : ObjCRuntime.ToManagedString(text);
    }

    /// <summary>写剪贴板文本。</summary>
    /// <remarks>
    /// <c>clearContents</c> 是必须的：它告诉系统"这块内容归我了"，
    /// 同时**给这块内容打一个变更计数**（changeCount）——不调用的话
    /// 别的应用不会收到"剪贴板变了"的通知，表现为"复制了但粘贴菜单还是灰的"。
    /// </remarks>
    public bool WriteClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return false;
        }

        _ = ObjCRuntime.SendId(pasteboard, ObjCRuntime.Sel("clearContents"));

        nint type = ObjCRuntime.MakeNSString(NSPasteboardTypeString);
        nint value = ObjCRuntime.MakeNSString(text);

        // setString:forType: 返回 BOOL
        return ObjCRuntime.SendIntObjObj(pasteboard, ObjCRuntime.Sel("setString:forType:"), value, type) != 0;
    }

    /// <summary>清空剪贴板。</summary>
    public bool ClearClipboard()
    {
        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return false;
        }

        _ = ObjCRuntime.SendId(pasteboard, ObjCRuntime.Sel("clearContents"));
        return true;
    }

    public void RunMessageLoop()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }
        // run 前把窗口带到前台
        ObjCRuntime.SendVoidBool(_nsApp, ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
        ObjCRuntime.SendVoid(_nsApp, ObjCRuntime.Sel("run"));
    }

    /// <summary>
    /// 请求退出：让 <c>NSApplication.run</c> 返回，而不是终止进程。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>用 <c>stop:</c> 而不是 <c>terminate:</c></b>（CI 上实测出来的缺陷）：
    /// <c>terminate:</c> 走的是 AppKit 的"应用终止"流程，它**不会让 <c>run</c> 返回**——
    /// 于是 <see cref="OrielApp.Run"/> 之后的清理（托盘撤销、后端释放）全部被跳过，
    /// <c>ExitCompleted</c> 事件也永不触发。这与库对使用者的承诺直接冲突：
    /// "Quit 只请求退出，Run() 返回后该做的清理仍会执行"。
    /// </para>
    /// <para>
    /// <c>stop:</c> 的语义正是我们要的（Apple 文档）："Stops the main event loop...
    /// sets a flag that the app checks only after it finishes dispatching an event"——
    /// 即循环会退出、<c>run</c> 会返回，与 Windows 的 <c>PostQuitMessage</c>、Linux 的
    /// <c>gtk_main_quit</c> 同义。
    /// </para>
    /// <para>
    /// <b>必须补一个空事件</b>：<c>stop:</c> 是"处理完**当前**事件后"才检查标志。若此刻
    /// 队列里没有事件，循环会一直等到下一个真实事件——表现为"调了 Quit 却退不掉"。
    /// 投一个 <c>CGEventCreate(NULL)</c> 造的空事件即可立刻唤醒它。
    /// </para>
    /// </remarks>
    public void Quit()
    {
        ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("stop:"), 0);

        // 唤醒 run loop 去检查上面那个标志（理由见 remarks）。
        // 用 +[NSEvent otherEventWithType:...] 造一个应用自定义事件——**不是** CGEventCreate：
        // postEvent:atStart: 要的是 NSEvent*，传 CGEventRef 是未定义行为。
        // 这是 AppKit 里"让 stop: 立刻生效"的标准做法。
        const nint NSEventTypeApplicationDefined = 15;
        nint evt = ObjCRuntime.SendIdNintDouble2NuintDoubleNintNintNintNintNint(
            ObjCRuntime.GetClassOrThrow("NSEvent"),
            ObjCRuntime.Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            NSEventTypeApplicationDefined,
            0, 0,      // location（NSZeroPoint）
            0,         // modifierFlags
            0,         // timestamp
            0,         // windowNumber
            0,         // context
            0,         // subtype
            0, 0);     // data1 / data2

        if (evt != 0)
        {
            ObjCRuntime.SendVoidObjObjBool(
                _nsApp, ObjCRuntime.Sel("postEvent:atStart:"), evt, 0, true);
        }
        else
        {
            // 造不出事件不致命（stop: 的标志已置位，下一个真实事件到来时循环照样会退出），
            // 但"此刻没有事件就退不掉"的可能存在，记一笔便于排障。
            OrielDiagnostics.Report(
                "[OrielWeb] 退出时未能构造唤醒事件：run loop 要等到下一个事件才会检查退出标志。");
        }
    }

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public bool PostToMainThread(Action action)
    {
        MainThreadQueue.Enqueue(action);
        // pump 帮助实例常驻，selector 在主线程排空队列。
        // objc_msgSend 到 nil（pump helper 没建出来）是静默 no-op——队列里的动作永远不会被排空，
        // 所以这里显式判空并留痕，而不是让"投了但没生效"悄悄发生。
        if (_pumpHelper == 0)
        {
            OrielDiagnostics.Report("[OrielWeb] PostToMainThread 失败：pump helper 不可用，回调已被丢弃。");
            return false;
        }

        ObjCRuntime.SendVoidSelObjBool(
            _pumpHelper,
            ObjCRuntime.Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
            ObjCRuntime.Sel("orielPump:"),
            0,
            false);
        return true;
    }

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            // 最后一个窗口关闭 → 结束应用（NSApplication.run 返回）
            Quit();
        }
    }

    // ---- 开机自启（LaunchAgents 目录）----

    public bool EnableAutoStart(string id, IReadOnlyList<string>? arguments) => MacOSAutoStart.Enable(id, arguments);

    public bool DisableAutoStart(string id) => MacOSAutoStart.Disable(id);

    public bool IsAutoStartEnabled(string id) => MacOSAutoStart.IsEnabled(id);

    public void Dispose()
    {
        // macOS 后端当前不持有需要显式释放的原生资源
    }
}
