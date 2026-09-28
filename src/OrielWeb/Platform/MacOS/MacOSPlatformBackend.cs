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
        _nsApp = ObjCRuntime.SendId(ObjCRuntime.GetClass("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        // NSApplicationActivationPolicyRegular = 0（普通应用，出现在 Dock）
        ObjCRuntime.SendVoidNint(_nsApp, ObjCRuntime.Sel("setActivationPolicy:"), 0);

        _appDelegate = MacOSObjCClasses.CreateAppDelegate();
        ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("setDelegate:"), _appDelegate);

        _pumpHelper = MacOSObjCClasses.CreatePumpHelper();
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = new MacOSWindowHost(window, options, app, assetDirectory, this);
        _aliveWindows++;
        return host;
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

    public void Quit() => ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("terminate:"), 0);

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public void PostToMainThread(Action action)
    {
        MainThreadQueue.Enqueue(action);
        // pump 帮助实例常驻，selector 在主线程排空队列
        ObjCRuntime.SendVoidSelObjBool(
            _pumpHelper,
            ObjCRuntime.Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
            ObjCRuntime.Sel("orielPump:"),
            0,
            false);
    }

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            // 最后一个窗口关闭 → 结束应用（NSApplication.run 返回）
            Quit();
        }
    }

    public void Dispose()
    {
    }
}
