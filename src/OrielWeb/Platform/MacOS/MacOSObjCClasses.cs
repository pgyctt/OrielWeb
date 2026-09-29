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
// 穿越 ObjC 运行时边界会导致进程 fail-fast 且不可捕获（见 docs/DECISIONS.md）。
// ============================================================================

internal static unsafe class MacOSObjCClasses
{
    // ---- 类对（进程级单次注册）----

    private static nint s_appDelegateClass;
    private static nint s_windowDelegateClass;
    private static nint s_scriptHandlerClass;
    private static nint s_navigationDelegateClass;
    private static nint s_pumpHelperClass;

    // ---- 状态注册表（实例指针 → 托管状态；实例被 retain，指针稳定）----
    // 仅在 AppKit/WebKit 主线程访问，不跨线程；窗口关闭时经 Remove* 清理，
    // 否则条目只增不减 → 整棵对象树永不释放，且指针复用会造成陈旧映射（ABA）。

    private static readonly Dictionary<nint, MacOSWindowHost> WindowDelegateStates = [];
    private static readonly Dictionary<nint, MacOSWindowHost> NavigationDelegateStates = [];
    private static readonly Dictionary<nint, MacOSWebMessageHandler> ScriptHandlerStates = [];

    private static nint AppDelegateClass => Ensure(ref s_appDelegateClass, BuildAppDelegate);
    private static nint WindowDelegateClass => Ensure(ref s_windowDelegateClass, BuildWindowDelegate);
    private static nint ScriptHandlerClass => Ensure(ref s_scriptHandlerClass, BuildScriptHandler);
    private static nint NavigationDelegateClass => Ensure(ref s_navigationDelegateClass, BuildNavigationDelegate);
    private static nint PumpHelperClass => Ensure(ref s_pumpHelperClass, BuildPumpHelper);

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

    // ------------------------------------------------------------------
    // 状态清理（窗口关闭时调用；避免注册表只增不减导致泄漏与 ABA 指针复用风险）
    // ------------------------------------------------------------------

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
}
