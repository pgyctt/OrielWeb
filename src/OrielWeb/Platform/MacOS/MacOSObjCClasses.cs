using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OrielWeb.Ipc;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

// ============================================================================
// 运行时构建的 ObjC 类对 + [UnmanagedCallersOnly] trampoline。
// 与 Windows 侧的手工 CCW 同一思想：不依赖 ComWrappers/官方互操作生成器，
// IMP 直接指向托管静态方法；状态经静态注册表（实例指针 → 托管对象）查找。
// 实例在创建时 objc_retain，随进程生命周期存活。
//
// 约定：所有 [UnmanagedCallersOnly] trampoline 必须全身 try/catch——托管异常
// 穿越 ObjC 运行时边界会导致进程 fail-fast 且不可捕获（见 API.md）。
// ============================================================================

internal static unsafe class MacOSObjCClasses
{
    // ---- 类对（进程级单次注册）----

    private static nint s_appDelegateClass;
    private static nint s_windowDelegateClass;
    private static nint s_scriptHandlerClass;
    private static nint s_navigationDelegateClass;
    private static nint s_pumpHelperClass;
    private static nint s_dropViewClass;
    private static nint s_uiDelegateClass;

    // ---- 状态注册表（实例指针 → 托管状态；实例被 retain，指针稳定）----
    // 仅在 AppKit/WebKit 主线程访问，不跨线程；窗口关闭时经 Remove* 清理，
    // 否则条目只增不减 → 整棵对象树永不释放，且指针复用会造成陈旧映射（ABA）。

    private static readonly Dictionary<nint, MacOSWindowHost> WindowDelegateStates = [];
    private static readonly Dictionary<nint, MacOSWindowHost> NavigationDelegateStates = [];
    private static readonly Dictionary<nint, MacOSWebMessageHandler> ScriptHandlerStates = [];
    private static readonly Dictionary<nint, MacOSWindowHost> DropViewStates = [];
    private static readonly Dictionary<nint, MacOSWindowHost> UIDelegateStates = [];

    private static nint AppDelegateClass => Ensure(ref s_appDelegateClass, BuildAppDelegate);
    private static nint WindowDelegateClass => Ensure(ref s_windowDelegateClass, BuildWindowDelegate);
    private static nint ScriptHandlerClass => Ensure(ref s_scriptHandlerClass, BuildScriptHandler);
    private static nint NavigationDelegateClass => Ensure(ref s_navigationDelegateClass, BuildNavigationDelegate);
    private static nint PumpHelperClass => Ensure(ref s_pumpHelperClass, BuildPumpHelper);
    private static nint DropViewClass => Ensure(ref s_dropViewClass, BuildDropView);
    private static nint UIDelegateClass => Ensure(ref s_uiDelegateClass, BuildUIDelegate);

    private static nint Ensure(ref nint cached, Func<nint> build)
    {
        if (cached != 0)
        {
            return cached;
        }
        cached = build();
        return cached;
    }

    private static nint AllocInit(nint cls)
    {
        var instance = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("alloc"));
        instance = ObjCRuntime.SendId(instance, ObjCRuntime.Sel("init"));
        ObjCRuntime.objc_retain(instance);
        return instance;
    }

    private static void AddMethod(nint cls, string selector, delegate* unmanaged<nint, nint, nint> imp, string encoding)
        => ObjCRuntime.class_addMethod(cls, ObjCRuntime.Sel(selector), imp, encoding);

    private static void AddMethod(nint cls, string selector, delegate* unmanaged<nint, nint, nint, nint> imp, string encoding)
        => ObjCRuntime.class_addMethod(cls, ObjCRuntime.Sel(selector), imp, encoding);

    private static void AddMethod(nint cls, string selector, delegate* unmanaged<nint, nint, nint, nint, nint> imp, string encoding)
        => ObjCRuntime.class_addMethod(cls, ObjCRuntime.Sel(selector), imp, encoding);

    private static void AddMethod(nint cls, string selector, delegate* unmanaged<nint, nint, nint, nint, nint, nint> imp, string encoding)
        => ObjCRuntime.class_addMethod(cls, ObjCRuntime.Sel(selector), imp, encoding);

    // ---- App 委托 ----

    private static nint BuildAppDelegate()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielAppDelegate", 0);
        AddMethod(cls, "applicationDidFinishLaunching:", &AppDidFinishLaunching, "v@:@");
        AddMethod(cls, "applicationShouldTerminateAfterLastWindowClosed:", &TerminateAfterLastWindow, "c@:@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint AppDidFinishLaunching(nint self, nint sel, nint notification)
    {
        // 无需额外动作：窗口在 run 之前创建并显示
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint TerminateAfterLastWindow(nint self, nint sel, nint app) => 1;

    // ---- 窗口委托 ----

    private static nint BuildWindowDelegate()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielWindowDelegate", 0);
        AddMethod(cls, "windowShouldClose:", &WindowShouldClose, "c@:@");
        AddMethod(cls, "windowWillClose:", &WindowWillClose, "v@:@");
        // 原生路径的最大化/还原（系统菜单 Zoom、脚本等）不经过 win.toggleMaximize，
        // 只能靠 resize 回调发现——isZoomed 的真实变化由宿主比对后上报（见 SyncMaximizedState）。
        AddMethod(cls, "windowDidResize:", &WindowDidResize, "v@:@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint WindowShouldClose(nint self, nint sel, nint sender)
    {
        try
        {
            if (WindowDelegateStates.TryGetValue(self, out var host))
            {
                return host.OnWindowShouldClose() ? 1 : 0;
            }
        }
        catch (Exception ex)
        {
            // 用户 Closing 处理器异常不得穿越 ObjC 边界。
            // 降级为"允许关闭"——与状态缺失时的默认语义一致。
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] windowShouldClose: 抛出异常：{ex}");
        }
        return 1;
    }

    [UnmanagedCallersOnly]
    private static nint WindowWillClose(nint self, nint sel, nint notification)
    {
        try
        {
            if (WindowDelegateStates.TryGetValue(self, out var host))
            {
                host.OnWindowWillClose();
            }
        }
        catch (Exception ex)
        {
            // 用户 Closed 处理器异常不得穿越 ObjC 边界
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] windowWillClose: 抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WindowDidResize(nint self, nint sel, nint notification)
    {
        try
        {
            if (WindowDelegateStates.TryGetValue(self, out var host))
            {
                host.SyncMaximizedState();
            }
        }
        catch (Exception ex)
        {
            // 异常不得穿越 ObjC 边界；状态同步失败不影响窗口本身
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] windowDidResize: 抛出异常：{ex}");
        }
        return 0;
    }

    // ---- WKScriptMessageHandler ----

    private static nint BuildScriptHandler()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielScriptMessageHandler", 0);
        AddMethod(cls, "userContentController:didReceiveScriptMessage:", &DidReceiveScriptMessage, "v@:@@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint DidReceiveScriptMessage(nint self, nint sel, nint contentController, nint message)
    {
        try
        {
            if (!ScriptHandlerStates.TryGetValue(self, out var handler))
            {
                return 0;
            }
            // JS 侧 postMessage 的是 JSON 字符串
            var body = ObjCRuntime.SendId(message, ObjCRuntime.Sel("body"));
            var json = ObjCRuntime.ToManagedString(body);
            handler.OnScriptMessage(json);
        }
        catch
        {
            // 消息处理异常不外泄（外泄 = 进程 fail-fast）
        }
        return 0;
    }

    // ---- WKNavigationDelegate ----

    private static nint BuildNavigationDelegate()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielNavigationDelegate", 0);
        AddMethod(cls, "webView:didStartProvisionalNavigation:", &DidStartProvisionalNavigation, "v@:@@");
        AddMethod(cls, "webView:didFinishNavigation:", &DidFinishNavigation, "v@:@@");
        AddMethod(cls, "webView:didFailNavigation:withError:", &DidFailNavigation, "v@:@@@");
        AddMethod(cls, "webView:didFailProvisionalNavigation:withError:", &DidFailProvisionalNavigation, "v@:@@@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint DidFinishNavigation(nint self, nint sel, nint webview, nint navigation)
    {
        try
        {
            if (NavigationDelegateStates.TryGetValue(self, out var host))
            {
                host.OnNavigationCompleted(success: true);
            }
        }
        catch (Exception ex)
        {
            // 导航完成会触发用户 Loaded 处理器，异常不得穿越 ObjC 边界
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] didFinishNavigation: 抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint DidStartProvisionalNavigation(nint self, nint sel, nint webview, nint navigation)
    {
        try
        {
            if (NavigationDelegateStates.TryGetValue(self, out var host))
            {
                host.OnNavigationStarted();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] didStartProvisionalNavigation: 抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint DidFailNavigation(nint self, nint sel, nint webview, nint navigation, nint error)
    {
        try
        {
            if (NavigationDelegateStates.TryGetValue(self, out var host))
            {
                host.OnNavigationCompleted(success: false, error: DescribeNSError(error));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] didFailNavigation: 抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint DidFailProvisionalNavigation(nint self, nint sel, nint webview, nint navigation, nint error)
    {
        try
        {
            if (NavigationDelegateStates.TryGetValue(self, out var host))
            {
                host.OnNavigationCompleted(success: false, error: DescribeNSError(error));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] didFailProvisionalNavigation: 抛出异常：{ex}");
        }
        return 0;
    }

    /// <summary>
    /// 把 NSError 压成一行文本（"domain: localizedDescription"）。取的是 ObjC 对象的属性而非
    /// 结构体字段，因此不依赖 NSError 的内存布局。error 为 0（无错误对象）时返回 null。
    /// </summary>
    private static string? DescribeNSError(nint error)
    {
        if (error == 0)
        {
            return null;
        }

        string? domain = ObjCRuntime.ToManagedString(ObjCRuntime.SendId(error, ObjCRuntime.Sel("domain")));
        string? description = ObjCRuntime.ToManagedString(
            ObjCRuntime.SendId(error, ObjCRuntime.Sel("localizedDescription")));

        if (string.IsNullOrEmpty(description))
        {
            return string.IsNullOrEmpty(domain) ? null : domain;
        }

        return string.IsNullOrEmpty(domain) ? description : $"{domain}: {description}";
    }

    // ---- 主题观察者（NSDistributedNotificationCenter 的接收端）----

    /// <summary>观察者实例 → 后端 的映射（通知回调需要找回后端实例）。</summary>
    private static readonly ConcurrentDictionary<nint, MacOSPlatformBackend> ThemeObserverStates = [];

    private static nint s_themeObserverClass;

    /// <summary>
    /// 建一个主题观察者实例。类只建一次（与 PumpHelper 同样的手工类对模式）。
    /// </summary>
    internal static nint CreateThemeObserver(MacOSPlatformBackend backend)
    {
        nint cls = s_themeObserverClass;
        if (cls == 0)
        {
            cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielThemeObserver", 0);
            AddMethod(cls, "orielThemeChanged:", &OnThemeChanged, "v@:@");
            ObjCRuntime.objc_registerClassPair(cls);
            s_themeObserverClass = cls;
        }

        nint observer = ObjCRuntime.SendId(
            ObjCRuntime.SendId(cls, ObjCRuntime.Sel("alloc")), ObjCRuntime.Sel("init"));
        ThemeObserverStates[observer] = backend;
        return observer;
    }

    [UnmanagedCallersOnly]
    private static nint OnThemeChanged(nint self, nint sel, nint notification)
    {
        try
        {
            if (ThemeObserverStates.TryGetValue(self, out var backend))
            {
                backend.RaiseThemeIfChanged();
            }
        }
        catch (Exception ex)
        {
            // 异常不得穿越 ObjC 边界；主题同步失败不影响应用本身
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] orielThemeChanged: 抛出异常：{ex}");
        }
        return 0;
    }

    // ---- 主线程泵（performSelectorOnMainThread 的接收端）----

    internal static readonly ConcurrentQueue<Action> MainThreadQueue = [];

    private static nint BuildPumpHelper()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielPumpHelper", 0);
        AddMethod(cls, "orielPump:", &Pump, "v@:@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint Pump(nint self, nint sel, nint arg)
    {
        while (MainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 动作来自 public 的 OrielApp.PostToMainThread，异常不得穿越 ObjC 边界；
                // 单条失败不影响后续排空
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 主线程队列动作抛出异常：{ex}");
            }
        }
        return 0;
    }

    // ---- WKUIDelegate ----

    /// <summary>
    /// 构建 UI 委托类，目前只为了一件事：<c>webView:willOpenMenu:withEvent:</c>。
    /// </summary>
    /// <remarks>
    /// 这是 macOS 上唯一能在**内建菜单弹出前**动它的钩子：拿到的是现成的 <c>NSMenu</c>，
    /// 我们删掉不要的项，剩下的仍由 WebKit 自己弹——保留项的行为（真的剪切/复制/粘贴到选区）
    /// 因此不受影响。
    /// <para>
    /// 与拖放同理，<b>不碰 <c>WKWebView</c> 的方法表</b>：那是 WebKit 自己的实现，替换它是全局改动。
    /// 委托才是标准扩展点。
    /// </para>
    /// <para>
    /// <c>willOpenMenu</c> 是 macOS 11+ 的可选方法，旧系统根本不回调它——那时菜单是原样的，
    /// 属于"过滤没生效但不出错"的降级。可以接受：它不会让任何东西坏掉。
    /// </para>
    /// </remarks>
    private static nint BuildUIDelegate()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielUIDelegate", 0);
        // (WKWebView *, NSMenu *, NSEvent *) → void：三个对象参数，所以是 v@:@@@
        AddMethod(cls, "webView:willOpenMenu:withEvent:", &WillOpenMenu, "v@:@@@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    /// <remarks>
    /// 返回类型写成 <c>nint</c> 而 ObjC 声明是 <c>void</c>：本文件的 trampoline 一律用 <c>nint</c>
    /// （调用方不读返回值，因此无害），这样只需维护有限几个 <c>AddMethod</c> 重载。
    /// </remarks>
    [UnmanagedCallersOnly]
    private static nint WillOpenMenu(nint self, nint sel, nint webview, nint menu, nint nsEvent)
    {
        try
        {
            if (UIDelegateStates.TryGetValue(self, out var host))
            {
                host.FilterContextMenu(menu);
            }
        }
        catch (Exception ex)
        {
            // 过滤失败就让菜单原样弹出；异常不得穿越 ObjC 边界
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] willOpenMenu: 过滤失败：{ex}");
        }

        return 0;
    }

    // ---- 拖放视图 ----

    /// <summary>
    /// 构建承载 <c>NSDraggingDestination</c> 的视图类。
    /// </summary>
    /// <remarks>
    /// 为什么是一个**容器视图**而不是给 WKWebView 加方法：
    /// 拖放协议方法必须由"注册了 dragged types 的那个 view"实现，而给 WKWebView 加/替换方法会
    /// 动到 WebKit 自己的拖放实现（它内建处理拖放，且不会把文件路径交给页面）。
    /// AppKit 查找拖放目标时会**沿父视图链向上**，因此把 webview 放进一个注册过类型的容器里，
    /// 容器就能收到事件——webview 的类完全不必被碰。
    /// <para>
    /// 继承 <c>NSView</c>（而不是 NSObject）：它要作为真正的视图插进视图层级。
    /// </para>
    /// </remarks>
    private static nint BuildDropView()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSView"), "OrielDropView", 0);
        // NSDragOperation 是 NSUInteger；返回 NSDragOperationCopy 才是"受理"，
        // 返回 0 会让系统显示禁止光标（拖不进来）。
        AddMethod(cls, "draggingEntered:", &DraggingEntered, "L@:@");
        AddMethod(cls, "performDragOperation:", &PerformDragOperation, "c@:@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    /// <summary>NSDragOperationCopy：只受理复制语义。</summary>
    private const int NSDragOperationCopy = 1;

    [UnmanagedCallersOnly]
    private static nint DraggingEntered(nint self, nint sel, nint sender)
    {
        try
        {
            return DropViewStates.ContainsKey(self) ? NSDragOperationCopy : 0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint PerformDragOperation(nint self, nint sel, nint sender)
    {
        try
        {
            if (!DropViewStates.TryGetValue(self, out var host))
            {
                return 0;
            }

            IReadOnlyList<string> paths = ReadDraggedPaths(sender);
            if (paths.Count == 0)
            {
                return 0;
            }

            host.OnFilesDropped(paths);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 从拖动会话的粘贴板取文件路径。
    /// </summary>
    /// <remarks>
    /// 走 <c>readObjectsForClasses:options:</c> 而不是老的
    /// <c>propertyListForType:@"NSFilenamesPboardType"</c>：后者已废弃，且新系统里
    /// 提供方（Finder）不保证再写那个类型。取到的是 NSURL，用 <c>path</c> 拿本地路径
    /// （不是 <c>absoluteString</c>——那会带 <c>file://</c> 前缀与百分号编码）。
    /// </remarks>
    private static IReadOnlyList<string> ReadDraggedPaths(nint sender)
    {
        nint pasteboard = ObjCRuntime.SendId(sender, ObjCRuntime.Sel("draggingPasteboard"));
        if (pasteboard == 0)
        {
            return [];
        }

        nint classes = ObjCRuntime.SendIdObj(
            ObjCRuntime.GetClass("NSArray"),
            ObjCRuntime.Sel("arrayWithObject:"),
            ObjCRuntime.GetClass("NSURL"));
        nint options = ObjCRuntime.SendId(ObjCRuntime.GetClass("NSDictionary"), ObjCRuntime.Sel("dictionary"));

        nint urls = ObjCRuntime.SendIdObjObj(
            pasteboard, ObjCRuntime.Sel("readObjectsForClasses:options:"), classes, options);
        if (urls == 0)
        {
            return [];
        }

        int count = (int)ObjCRuntime.SendId(urls, ObjCRuntime.Sel("count"));
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            nint url = ObjCRuntime.SendIdNint(urls, ObjCRuntime.Sel("objectAtIndex:"), i);
            if (url == 0)
            {
                continue;
            }

            nint nsPath = ObjCRuntime.SendId(url, ObjCRuntime.Sel("path"));
            if (nsPath != 0 && ObjCRuntime.ToManagedString(nsPath) is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    // ------------------------------------------------------------------
    // 状态清理（窗口关闭时调用；避免注册表只增不减导致泄漏与 ABA 指针复用风险）
    // ------------------------------------------------------------------

    internal static void RemoveDropView(nint instance) => DropViewStates.Remove(instance);
    internal static void RemoveUIDelegate(nint instance) => UIDelegateStates.Remove(instance);
    internal static void RemoveWindowDelegate(nint instance) => WindowDelegateStates.Remove(instance);
    internal static void RemoveNavigationDelegate(nint instance) => NavigationDelegateStates.Remove(instance);
    internal static void RemoveScriptHandler(nint instance) => ScriptHandlerStates.Remove(instance);

    // ------------------------------------------------------------------
    // 工厂
    // ------------------------------------------------------------------

    internal static nint CreateAppDelegate()
    {
        var instance = AllocInit(AppDelegateClass);
        return instance;
    }

    internal static nint CreateWindowDelegate(MacOSWindowHost host)
    {
        var instance = AllocInit(WindowDelegateClass);
        WindowDelegateStates[instance] = host;
        return instance;
    }

    internal static nint CreateNavigationDelegate(MacOSWindowHost host)
    {
        var instance = AllocInit(NavigationDelegateClass);
        NavigationDelegateStates[instance] = host;
        return instance;
    }

    internal static nint CreateScriptHandler(MacOSWebMessageHandler handler)
    {
        var instance = AllocInit(ScriptHandlerClass);
        ScriptHandlerStates[instance] = handler;
        return instance;
    }

    internal static nint CreatePumpHelper() => AllocInit(PumpHelperClass);

    internal static nint CreateDropView(MacOSWindowHost host)
    {
        var instance = AllocInit(DropViewClass);
        DropViewStates[instance] = host;
        return instance;
    }

    internal static nint CreateUIDelegate(MacOSWindowHost host)
    {
        var instance = AllocInit(UIDelegateClass);
        UIDelegateStates[instance] = host;
        return instance;
    }

    // ---- oriel:// 的 scheme 处理器 ----
    //
    // 与 Windows 的 WebResourceRequested、Linux 的 WebKitURISchemeRequest 对应：把内嵌资源
    // 直接交给引擎，不经过磁盘。挂在 WKWebViewConfiguration 上，所以必须在
    // initWithFrame:configuration: **之前**注册（见 MacOSWindowHost.Create）。

    private static nint s_assetSchemeHandlerClass;

    private static readonly Dictionary<nint, (EmbeddedAssetStore Store, string Host)> AssetSchemeStates = [];

    private static nint AssetSchemeHandlerClass => Ensure(ref s_assetSchemeHandlerClass, BuildAssetSchemeHandler);

    private static nint BuildAssetSchemeHandler()
    {
        var cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielAssetSchemeHandler", 0);

        // 协议必须显式声明：WebKit 会 conformsToProtocol: 检查（见 ObjCRuntime.class_addProtocol 的说明）。
        ObjCRuntime.class_addProtocol(cls, ObjCRuntime.objc_getProtocol("WKURLSchemeHandler"));

        AddMethod(cls, "webView:startURLSchemeTask:", &AssetSchemeStart, "v@:@@");
        AddMethod(cls, "webView:stopURLSchemeTask:", &AssetSchemeStop, "v@:@@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    internal static nint CreateAssetSchemeHandler(EmbeddedAssetStore store, string host)
    {
        var instance = AllocInit(AssetSchemeHandlerClass);
        AssetSchemeStates[instance] = (store, host);
        return instance;
    }

    internal static void RemoveAssetSchemeHandler(nint instance) => AssetSchemeStates.Remove(instance);

    /// <summary>WKURLSchemeTask 的失败码：<c>NSURLErrorFileDoesNotExist</c>。</summary>
    private const int NsUrlErrorFileDoesNotExist = -1100;

    private const int NsUrlErrorUnsupportedUrl = -1002;

    [UnmanagedCallersOnly]
    private static nint AssetSchemeStart(nint self, nint sel, nint webView, nint task)
    {
        // 约定：trampoline 必须全身 try/catch——托管异常穿越 ObjC 边界会 fail-fast 且不可捕获。
        try
        {
            if (!AssetSchemeStates.TryGetValue(self, out var state))
            {
                return 0;
            }

            nint request = ObjCRuntime.SendId(task, ObjCRuntime.Sel("request"));
            nint url = request == 0 ? 0 : ObjCRuntime.SendId(request, ObjCRuntime.Sel("URL"));
            if (url == 0)
            {
                return 0;
            }

            string uri = ObjCRuntime.ToManagedString(ObjCRuntime.SendId(url, ObjCRuntime.Sel("absoluteString")));

            if (!AssetUrl.TryResolve(uri, state.Host, out string relative))
            {
                AssetSchemeFail(task, NsUrlErrorUnsupportedUrl);
                return 0;
            }

            if (!state.Store.TryGet(relative, out EmbeddedAsset asset))
            {
                // 404 必须是失败：自检里"导航到不存在的页面应当失败"那一步靠它。
                AssetSchemeFail(task, NsUrlErrorFileDoesNotExist);
                return 0;
            }

            byte[] payload;
            using (Stream source = state.Store.Open(asset))
            {
                payload = new byte[source.Length];
                source.ReadExactly(payload);
            }

            // dataWithBytes: 会复制，所以可以用 fixed 的托管数组，不必自己分配非托管内存。
            nint data;
            fixed (byte* buffer = payload)
            {
                data = ObjCRuntime.SendIdNintNint(
                    ObjCRuntime.GetClass("NSData"),
                    ObjCRuntime.Sel("dataWithBytes:length:"),
                    (nint)buffer,
                    payload.Length);
            }

            nint headers = ObjCRuntime.SendIdObjObj(
                ObjCRuntime.GetClass("NSDictionary"),
                ObjCRuntime.Sel("dictionaryWithObject:forKey:"),
                ObjCRuntime.MakeNSString(asset.ContentType),
                ObjCRuntime.MakeNSString("Content-Type"));

            // HTTPVersion 给 "HTTP/1.1"：自定义 scheme 没有真实协议版本，但初始化器要这个参数。
            nint response = ObjCRuntime.SendIdObjNintObjObj(
                ObjCRuntime.SendId(ObjCRuntime.GetClass("NSHTTPURLResponse"), ObjCRuntime.Sel("alloc")),
                ObjCRuntime.Sel("initWithURL:statusCode:HTTPVersion:headerFields:"),
                url,
                200,
                ObjCRuntime.MakeNSString("HTTP/1.1"),
                headers);

            ObjCRuntime.SendVoidObj(task, ObjCRuntime.Sel("didReceiveResponse:"), response);
            // alloc/init 产生的 +1 归我们：didReceiveResponse: 之后 WebKit 自己 retain 了它，
            // 不 release 就是每个子资源请求漏一个 NSHTTPURLResponse——图片多的页面一次加载
            // 漏几十个。同函数里便捷构造（dictionaryWithObject:forKey: 等）是 autoreleased，不在此列。
            ObjCRuntime.objc_release(response);
            ObjCRuntime.SendVoidObj(task, ObjCRuntime.Sel("didReceiveData:"), data);
            ObjCRuntime.SendVoid(task, ObjCRuntime.Sel("didFinish"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] 应答内嵌资源失败：{ex}");
            try
            {
                AssetSchemeFail(task, NsUrlErrorFileDoesNotExist);
            }
            catch (Exception inner)
            {
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 上报失败也失败了：{inner.Message}");
            }
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint AssetSchemeStop(nint self, nint sel, nint webView, nint task) => 0;

    private static void AssetSchemeFail(nint task, int code)
    {
        nint error = ObjCRuntime.SendIdObjNintObj(
            ObjCRuntime.GetClass("NSError"),
            ObjCRuntime.Sel("errorWithDomain:code:userInfo:"),
            ObjCRuntime.MakeNSString("NSURLErrorDomain"),
            code,
            0);

        ObjCRuntime.SendVoidObj(task, ObjCRuntime.Sel("didFailWithError:"), error);
    }
}
