using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Interop;

namespace OrielWeb.Platform.Windows.WebView2.Handlers;

// 每个回调一个 [GeneratedComClass]：源生成器会为它们产出 CCW 与 vtbl。
// 之所以不能合并成一个泛型类：GeneratedComClass 不支持泛型类型参数。
//
// 所有 Invoke 都把异常吞掉并返回 HRESULT——异常穿过 COM 边界会直接终止进程，
// 而 WebView2 的回调发生在它自己的消息泵里，那里没有调用方可以接住。

[GeneratedComClass]
internal sealed partial class EnvironmentCompletedHandler(Func<int, nint, int> callback)
    : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    public int Invoke(int errorCode, nint createdEnvironment) => callback(errorCode, createdEnvironment);
}

[GeneratedComClass]
internal sealed partial class CompositionControllerCompletedHandler(Func<int, nint, int> callback)
    : ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler
{
    public int Invoke(int errorCode, nint createdController) => callback(errorCode, createdController);
}

[GeneratedComClass]
internal sealed partial class ExecuteScriptCompletedHandler(Func<int, string, int> callback)
    : ICoreWebView2ExecuteScriptCompletedHandler
{
    public int Invoke(int errorCode, string resultObjectAsJson) => callback(errorCode, resultObjectAsJson);
}

[GeneratedComClass]
internal sealed partial class AddScriptCompletedHandler(Func<int, string, int> callback)
    : ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    public int Invoke(int errorCode, string id) => callback(errorCode, id);
}

[GeneratedComClass]
internal sealed partial class ClearBrowsingDataCompletedHandler(Func<int, int> callback)
    : ICoreWebView2ClearBrowsingDataCompletedHandler
{
    public int Invoke(int errorCode) => callback(errorCode);
}

[GeneratedComClass]
internal sealed partial class NavigationStartingHandler(Func<nint, nint, int> callback)
    : ICoreWebView2NavigationStartingEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class NavigationCompletedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2NavigationCompletedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class DocumentTitleChangedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2DocumentTitleChangedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class WebMessageReceivedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2WebMessageReceivedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class ContextMenuRequestedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2ContextMenuRequestedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class CursorChangedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2CursorChangedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}

[GeneratedComClass]
internal sealed partial class WebResourceRequestedHandler(Func<nint, nint, int> callback)
    : ICoreWebView2WebResourceRequestedEventHandler
{
    public int Invoke(nint sender, nint args) => callback(sender, args);
}
