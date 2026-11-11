using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

namespace OrielWeb.Platform.Windows.WebView2.Handlers;

// 每个回调一个 [GeneratedComClass]：源生成器会为它们产出 CCW 与 vtbl。
// 之所以不能合并成一个泛型类：GeneratedComClass 不支持泛型类型参数。
//
// 所有 Invoke 都经 ComCallbackGuard 执行：异常被挡在边界之内（报诊断 + 返回 E_FAIL），
// 不会穿过 COM 边界。异常穿过边界会直接终止进程，而 WebView2 的回调发生在它自己的
// 消息泵里，那里没有调用方可以接住——应用代码一个空引用就能带走整个宿主。
// （2026-11-11 评审 H3：此前这里的注释是这么写的，但每个 Invoke 都是直传回调，实现与注释相反。）

[GeneratedComClass]
internal sealed partial class EnvironmentCompletedHandler(Func<int, nint, int> callback)
    : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    public int Invoke(int errorCode, nint createdEnvironment)
        => ComCallbackGuard.Run(() => callback(errorCode, createdEnvironment), "EnvironmentCompleted");
}

[GeneratedComClass]
internal sealed partial class CompositionControllerCompletedHandler(Func<int, nint, int> callback)
    : ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler
{
    public int Invoke(int errorCode, nint createdController)
        => ComCallbackGuard.Run(() => callback(errorCode, createdController), "CompositionControllerCompleted");
}

[GeneratedComClass]
internal sealed partial class ExecuteScriptCompletedHandler(Func<int, string, int> callback)
    : ICoreWebView2ExecuteScriptCompletedHandler
{
    public int Invoke(int errorCode, string resultObjectAsJson)
        => ComCallbackGuard.Run(() => callback(errorCode, resultObjectAsJson), "ExecuteScriptCompleted");
}

[GeneratedComClass]
internal sealed partial class AddScriptCompletedHandler(Func<int, string, int> callback)
    : ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    public int Invoke(int errorCode, string id)
        => ComCallbackGuard.Run(() => callback(errorCode, id), "AddScriptCompleted");
}

[GeneratedComClass]
internal sealed partial class ClearBrowsingDataCompletedHandler(Func<int, int> callback)
    : ICoreWebView2ClearBrowsingDataCompletedHandler
{
    public int Invoke(int errorCode)
        => ComCallbackGuard.Run(() => callback(errorCode), "ClearBrowsingDataCompleted");
}

[GeneratedComClass]
internal sealed partial class NavigationStartingHandler(Func<nint, nint, int> callback)
    : ICoreWebView2NavigationStartingEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "NavigationStarting");
}

[GeneratedComClass]
internal sealed partial class NavigationCompletedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2NavigationCompletedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "NavigationCompleted");
}

[GeneratedComClass]
internal sealed partial class DocumentTitleChangedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2DocumentTitleChangedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "DocumentTitleChanged");
}

[GeneratedComClass]
internal sealed partial class WebMessageReceivedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2WebMessageReceivedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "WebMessageReceived");
}

[GeneratedComClass]
internal sealed partial class ContextMenuRequestedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2ContextMenuRequestedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "ContextMenuRequested");
}

[GeneratedComClass]
internal sealed partial class CursorChangedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2CursorChangedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "CursorChanged");
}

[GeneratedComClass]
internal sealed partial class WebResourceRequestedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2WebResourceRequestedEventHandler
{
    public int Invoke(nint sender, nint args)
        => ComCallbackGuard.Run(() => callback(sender, args), "WebResourceRequested");
}
