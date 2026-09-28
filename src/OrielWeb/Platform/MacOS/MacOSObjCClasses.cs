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
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint WindowShouldClose(nint self, nint sel, nint sender)
    {
        if (WindowDelegateStates.TryGetValue(self, out var host))
        {
            return host.OnWindowShouldClose() ? 1 : 0;
        }
        return 1;
    }

    [UnmanagedCallersOnly]
    private static nint WindowWillClose(nint self, nint sel, nint notification)
    {
        if (WindowDelegateStates.TryGetValue(self, out var host))
        {
            host.OnWindowWillClose();
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
        AddMethod(cls, "webView:didFinishNavigation:", &DidFinishNavigation, "v@:@@");
        AddMethod(cls, "webView:didFailNavigation:withError:", &DidFailNavigation, "v@:@@@");
        AddMethod(cls, "webView:didFailProvisionalNavigation:withError:", &DidFailProvisionalNavigation, "v@:@@@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint DidFinishNavigation(nint self, nint sel, nint webview, nint navigation)
    {
        if (NavigationDelegateStates.TryGetValue(self, out var host))
        {
            host.OnNavigationCompleted(success: true);
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint DidFailNavigation(nint self, nint sel, nint webview, nint navigation, nint error)
    {
        if (NavigationDelegateStates.TryGetValue(self, out var host))
        {
            host.OnNavigationCompleted(success: false);
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint DidFailProvisionalNavigation(nint self, nint sel, nint webview, nint navigation, nint error)
    {
        if (NavigationDelegateStates.TryGetValue(self, out var host))
        {
            host.OnNavigationCompleted(success: false);
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
            action();
        }
        return 0;
    }

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
