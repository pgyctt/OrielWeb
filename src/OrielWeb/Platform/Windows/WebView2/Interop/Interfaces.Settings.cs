using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>WebView2 设置（vtable 18 项）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("e562e4f0-d7fa-43ac-8d71-c05150499f00")]
internal partial interface ICoreWebView2Settings
{
    [PreserveSig] int get_IsScriptEnabled(out int isScriptEnabled);
    [PreserveSig] int put_IsScriptEnabled(int isScriptEnabled);
    [PreserveSig] int get_IsWebMessageEnabled(out int isWebMessageEnabled);
    [PreserveSig] int put_IsWebMessageEnabled(int isWebMessageEnabled);
    [PreserveSig] int get_AreDefaultScriptDialogsEnabled(out int areDefaultScriptDialogsEnabled);
    [PreserveSig] int put_AreDefaultScriptDialogsEnabled(int areDefaultScriptDialogsEnabled);
    [PreserveSig] int get_IsStatusBarEnabled(out int isStatusBarEnabled);
    [PreserveSig] int put_IsStatusBarEnabled(int isStatusBarEnabled);
    [PreserveSig] int get_AreDevToolsEnabled(out int areDevToolsEnabled);
    [PreserveSig] int put_AreDevToolsEnabled(int areDevToolsEnabled);
    [PreserveSig] int get_AreDefaultContextMenusEnabled(out int enabled);
    [PreserveSig] int put_AreDefaultContextMenusEnabled(int enabled);
    [PreserveSig] int get_AreHostObjectsAllowed(out int allowed);
    [PreserveSig] int put_AreHostObjectsAllowed(int allowed);
    [PreserveSig] int get_IsZoomControlEnabled(out int enabled);
    [PreserveSig] int put_IsZoomControlEnabled(int enabled);
    [PreserveSig] int get_IsBuiltInErrorPageEnabled(out int enabled);
    [PreserveSig] int put_IsBuiltInErrorPageEnabled(int enabled);
}

/// <summary>设置 2：UserAgent。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("ee9a0f68-f46c-4e32-ac23-ef8cac224d2a")]
internal partial interface ICoreWebView2Settings2 : ICoreWebView2Settings
{
    [PreserveSig] int get_UserAgent(out nint userAgent);
    [PreserveSig] int put_UserAgent(string userAgent);
}

/// <summary>设置 3：浏览器加速键。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("fdb5ab74-af33-4854-84f0-0a631deb5eba")]
internal partial interface ICoreWebView2Settings3 : ICoreWebView2Settings2
{
    [PreserveSig] int get_AreBrowserAcceleratorKeysEnabled(out int enabled);
    [PreserveSig] int put_AreBrowserAcceleratorKeysEnabled(int enabled);
}

/// <summary>设置 4：密码自动保存与常规自动填充。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("cb56846c-4168-4d53-b04f-03b6d6796ff2")]
internal partial interface ICoreWebView2Settings4 : ICoreWebView2Settings3
{
    [PreserveSig] int get_IsPasswordAutosaveEnabled(out int value);
    [PreserveSig] int put_IsPasswordAutosaveEnabled(int value);
    [PreserveSig] int get_IsGeneralAutofillEnabled(out int value);
    [PreserveSig] int put_IsGeneralAutofillEnabled(int value);
}

/// <summary>设置 5：捏合缩放。本库的 <c>Zoom</c> 选项同时关掉它与缩放控件。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("183e7052-1d03-43a0-ab99-98e043b66b39")]
internal partial interface ICoreWebView2Settings5 : ICoreWebView2Settings4
{
    [PreserveSig] int get_IsPinchZoomEnabled(out int enabled);
    [PreserveSig] int put_IsPinchZoomEnabled(int enabled);
}

/// <summary>用户配置（vtable 7 项）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("79110ad3-cd5d-4373-8bc3-c60658f17a5f")]
internal partial interface ICoreWebView2Profile
{
    [PreserveSig] int get_ProfileName(out nint value);
    [PreserveSig] int get_IsInPrivateModeEnabled(out int value);
    [PreserveSig] int get_ProfilePath(out nint value);
    [PreserveSig] int get_DefaultDownloadFolderPath(out nint value);
    [PreserveSig] int put_DefaultDownloadFolderPath(string value);
    [PreserveSig] int get_PreferredColorScheme(out int value);
    [PreserveSig] int put_PreferredColorScheme(int value);
}

/// <summary>配置 2：清除浏览数据。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("fa740d4b-5eae-4344-a8ad-74be31925397")]
internal partial interface ICoreWebView2Profile2 : ICoreWebView2Profile
{
    [PreserveSig] int ClearBrowsingData(int dataKinds, nint handler);
    [PreserveSig] int ClearBrowsingDataInTimeRange(int dataKinds, long startTime, long endTime, nint handler);
    [PreserveSig] int ClearBrowsingDataAll(nint handler);
}
