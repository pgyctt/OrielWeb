using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>资源请求事件参数。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("453e667f-12c7-49d4-be6d-ddbe7956f57a")]
internal partial interface ICoreWebView2WebResourceRequestedEventArgs
{
    [PreserveSig] int get_Request(out nint request);
    [PreserveSig] int get_Response(out nint response);
    [PreserveSig] int put_Response(nint response);
    [PreserveSig] int GetDeferral(out nint deferral);
    [PreserveSig] int get_ResourceContext(out int context);
}

/// <summary>资源请求。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("97055cd4-512c-4264-8b5f-e3f446cea6a5")]
internal partial interface ICoreWebView2WebResourceRequest
{
    [PreserveSig] int get_Uri(out nint uri);
    [PreserveSig] int put_Uri(string uri);
    [PreserveSig] int get_Method(out nint method);
    [PreserveSig] int put_Method(string method);
    [PreserveSig] int get_Content(out nint content);
    [PreserveSig] int put_Content(nint content);
    [PreserveSig] int get_Headers(out nint headers);
}

/// <summary>资源响应。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("aafcc94f-fa27-48fd-97df-830ef75aaec9")]
internal partial interface ICoreWebView2WebResourceResponse
{
    [PreserveSig] int get_Content(out nint content);
    [PreserveSig] int put_Content(nint content);
    [PreserveSig] int get_Headers(out nint headers);
    [PreserveSig] int get_StatusCode(out int statusCode);
    [PreserveSig] int put_StatusCode(int statusCode);
    [PreserveSig] int get_ReasonPhrase(out nint reasonPhrase);
    [PreserveSig] int put_ReasonPhrase(string reasonPhrase);
}

/// <summary>WebMessage 事件参数。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0f99a40c-e962-4207-9e92-e3d542eff849")]
internal partial interface ICoreWebView2WebMessageReceivedEventArgs
{
    [PreserveSig] int get_Source(out nint source);
    [PreserveSig] int get_WebMessageAsJson(out nint webMessageAsJson);
    [PreserveSig] int TryGetWebMessageAsString(out nint webMessageAsString);
}

/// <summary>导航开始事件参数。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("5b495469-e119-438a-9b18-7604f25f2e49")]
internal partial interface ICoreWebView2NavigationStartingEventArgs
{
    [PreserveSig] int get_Uri(out nint uri);
    [PreserveSig] int get_IsUserInitiated(out int isUserInitiated);
    [PreserveSig] int get_IsRedirected(out int isRedirected);
    [PreserveSig] int get_RequestHeaders(out nint requestHeaders);
    [PreserveSig] int get_Cancel(out int cancel);
    [PreserveSig] int put_Cancel(int cancel);
    [PreserveSig] int get_NavigationId(out ulong navigationId);
}

/// <summary>导航完成事件参数。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("30d68b7d-20d9-4752-a9ca-ec8448fbb5c1")]
internal partial interface ICoreWebView2NavigationCompletedEventArgs
{
    [PreserveSig] int get_IsSuccess(out int isSuccess);
    [PreserveSig] int get_WebErrorStatus(out int webErrorStatus);
    [PreserveSig] int get_NavigationId(out ulong navigationId);
}

/// <summary>右键菜单请求事件参数。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("a1d309ee-c03f-11eb-8529-0242ac130003")]
internal partial interface ICoreWebView2ContextMenuRequestedEventArgs
{
    [PreserveSig] int get_MenuItems(out nint menuItems);
    [PreserveSig] int get_ContextMenuTarget(out nint contextMenuTarget);
    [PreserveSig] int get_Location(out WV2POINT location);
    [PreserveSig] int put_SelectedCommandId(int commandId);
    [PreserveSig] int get_SelectedCommandId(out int commandId);
    [PreserveSig] int put_Handled(int handled);
    [PreserveSig] int get_Handled(out int handled);
    [PreserveSig] int GetDeferral(out nint deferral);
}

/// <summary>单个菜单项。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7aed49e3-a93f-497a-811c-749c6b6b6c65")]
internal partial interface ICoreWebView2ContextMenuItem
{
    [PreserveSig] int get_Name(out nint name);
    [PreserveSig] int get_Label(out nint label);
    [PreserveSig] int get_CommandId(out int commandId);
    [PreserveSig] int get_ShortcutKeyDescription(out nint shortcutKeyDescription);
    [PreserveSig] int get_Icon(out nint icon);
    [PreserveSig] int get_Kind(out int kind);
    [PreserveSig] int put_IsEnabled(int isEnabled);
    [PreserveSig] int get_IsEnabled(out int isEnabled);
    [PreserveSig] int put_IsChecked(int isChecked);
    [PreserveSig] int get_IsChecked(out int isChecked);
    [PreserveSig] int get_Children(out nint children);
    [PreserveSig] int add_CustomItemSelected(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_CustomItemSelected(EventRegistrationToken token);
}

/// <summary>菜单项集合。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f562a2f5-c415-45cf-b909-d4b7c1e276d3")]
internal partial interface ICoreWebView2ContextMenuItemCollection
{
    [PreserveSig] int get_Count(out uint count);
    [PreserveSig] int GetValueAtIndex(uint index, out nint value);
    [PreserveSig] int RemoveValueAtIndex(uint index);
    [PreserveSig] int InsertValueAtIndex(uint index, nint value);
}
