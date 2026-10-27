using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

// 回调接口一律把 sender / args 收成 nint：原生侧传进来的就是接口指针，
// 由使用者按需用 ComObject.FromPointer<T> 包装。这样声明面最小，也不会因为
// 某个参数接口没声明而阻塞整条链。

/// <summary>环境创建完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d")]
internal partial interface ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, nint createdEnvironment);
}

/// <summary>控制器创建完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c")]
internal partial interface ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, nint createdController);
}

/// <summary>组合控制器创建完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("02fab84b-1428-4fb7-ad45-1b2e64736184")]
internal partial interface ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, nint createdController);
}

/// <summary>导航开始。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3")]
internal partial interface ICoreWebView2NavigationStartingEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>导航完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c")]
internal partial interface ICoreWebView2NavigationCompletedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>文档标题变化。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4")]
internal partial interface ICoreWebView2DocumentTitleChangedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>收到页面 WebMessage。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("57213f19-00e6-49fa-8e07-898ea01ecbd2")]
internal partial interface ICoreWebView2WebMessageReceivedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>右键菜单请求。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04d3fe1d-ab87-42fb-a898-da241d35b63c")]
internal partial interface ICoreWebView2ContextMenuRequestedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>光标变化（组合控制器专有）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9da43ccc-26e1-4dad-b56c-d8961c94c571")]
internal partial interface ICoreWebView2CursorChangedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>资源请求拦截。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("ab00b74c-15f1-4646-80e8-e76341d25d71")]
internal partial interface ICoreWebView2WebResourceRequestedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>ExecuteScript 完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("49511172-cc67-4bca-9923-137112f4c4cc")]
internal partial interface ICoreWebView2ExecuteScriptCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, string resultObjectAsJson);
}

/// <summary>注入脚本完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17")]
internal partial interface ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, string id);
}

/// <summary>清除浏览数据完成。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("e9710a06-1d1d-49b2-8234-226f35846ae5")]
internal partial interface ICoreWebView2ClearBrowsingDataCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode);
}
