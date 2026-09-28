using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 平台后端：GTK3 主循环 + 主线程调度（g_idle_add_full）+ 窗口工厂。
/// 依赖：libgtk-3 / libwebkit2gtk-4.1（apt install libwebkit2gtk-4.1-dev）。
/// </summary>
internal sealed class LinuxPlatformBackend : IPlatformBackend
{
    private int _aliveWindows;
    private int _ran;
    private readonly int _mainManagedThreadId = Environment.CurrentManagedThreadId;

    public LinuxPlatformBackend()
    {
        GtkNative.GtkInit(0, 0);
    }

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = new LinuxWindowHost(window, options, app, assetDirectory, this);
        _aliveWindows++;
        return host;
    }

    public void RunMessageLoop()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }
        GtkNative.GtkMain();
    }

    public void Quit() => GtkNative.GtkMainQuit();

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public void PostToMainThread(Action action) => LinuxSignalHandlers.PostToMainThread(action);

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            GtkNative.GtkMainQuit();
        }
    }

    public void Dispose()
    {
    }
}
