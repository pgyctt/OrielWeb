using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>
/// WebView2 主接口（vtable 58 项）。
/// </summary>
/// <remarks>
/// 方法顺序**必须**与原生 IDL 完全一致——COM 的早期绑定靠 slot 序号寻址，
/// 少一个方法或调换顺序都会调用到别的函数上。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("76eceacb-0462-4d94-ac83-423a6793775e")]
internal partial interface ICoreWebView2
{
    [PreserveSig] int get_Settings(out nint settings);
    [PreserveSig] int get_Source(out nint uri);
    [PreserveSig] int Navigate(string uri);
    [PreserveSig] int NavigateToString(string htmlContent);
    [PreserveSig] int add_NavigationStarting(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_NavigationStarting(EventRegistrationToken token);
    [PreserveSig] int add_ContentLoading(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ContentLoading(EventRegistrationToken token);
    [PreserveSig] int add_SourceChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_SourceChanged(EventRegistrationToken token);
    [PreserveSig] int add_HistoryChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_HistoryChanged(EventRegistrationToken token);
    [PreserveSig] int add_NavigationCompleted(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_NavigationCompleted(EventRegistrationToken token);
    [PreserveSig] int add_FrameNavigationStarting(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_FrameNavigationStarting(EventRegistrationToken token);
    [PreserveSig] int add_FrameNavigationCompleted(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_FrameNavigationCompleted(EventRegistrationToken token);
    [PreserveSig] int add_ScriptDialogOpening(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ScriptDialogOpening(EventRegistrationToken token);
    [PreserveSig] int add_PermissionRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_PermissionRequested(EventRegistrationToken token);
    [PreserveSig] int add_ProcessFailed(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ProcessFailed(EventRegistrationToken token);
    [PreserveSig] int AddScriptToExecuteOnDocumentCreated(string javaScript, nint handler);
    [PreserveSig] int RemoveScriptToExecuteOnDocumentCreated(string id);
    [PreserveSig] int ExecuteScript(string javaScript, nint handler);
    [PreserveSig] int CapturePreview(int imageFormat, nint imageStream, nint handler);
    [PreserveSig] int Reload();
    [PreserveSig] int PostWebMessageAsJson(string webMessageAsJson);
    [PreserveSig] int PostWebMessageAsString(string webMessageAsString);
    [PreserveSig] int add_WebMessageReceived(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_WebMessageReceived(EventRegistrationToken token);
    [PreserveSig] int CallDevToolsProtocolMethod(string methodName, string parametersAsJson, nint handler);
    [PreserveSig] int get_BrowserProcessId(out uint value);
    [PreserveSig] int get_CanGoBack(out int canGoBack);
    [PreserveSig] int get_CanGoForward(out int canGoForward);
    [PreserveSig] int GoBack();
    [PreserveSig] int GoForward();
    [PreserveSig] int GetDevToolsProtocolEventReceiver(string eventName, out nint receiver);
    [PreserveSig] int Stop();
    [PreserveSig] int add_NewWindowRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_NewWindowRequested(EventRegistrationToken token);
    [PreserveSig] int add_DocumentTitleChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_DocumentTitleChanged(EventRegistrationToken token);
    [PreserveSig] int get_DocumentTitle(out nint title);
    [PreserveSig] int AddHostObjectToScript(string name, nint @object);
    [PreserveSig] int RemoveHostObjectFromScript(string name);
    [PreserveSig] int OpenDevToolsWindow();
    [PreserveSig] int add_ContainsFullScreenElementChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ContainsFullScreenElementChanged(EventRegistrationToken token);
    [PreserveSig] int get_ContainsFullScreenElement(out int containsFullScreenElement);
    [PreserveSig] int add_WebResourceRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_WebResourceRequested(EventRegistrationToken token);
    [PreserveSig] int AddWebResourceRequestedFilter(string uri, int resourceContext);
    [PreserveSig] int RemoveWebResourceRequestedFilter(string uri, int resourceContext);
    [PreserveSig] int add_WindowCloseRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_WindowCloseRequested(EventRegistrationToken token);
}

/// <summary>版本 2：Cookie 管理、环境回读、DOMContentLoaded（58 项之后接 7 项）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9e8f0cf8-e670-4b5e-b2bc-73e061e3184c")]
internal partial interface ICoreWebView2_2 : ICoreWebView2
{
    [PreserveSig] int add_WebResourceResponseReceived(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_WebResourceResponseReceived(EventRegistrationToken token);
    [PreserveSig] int NavigateWithWebResourceRequest(nint request);
    [PreserveSig] int add_DOMContentLoaded(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_DOMContentLoaded(EventRegistrationToken token);
    [PreserveSig] int get_CookieManager(out nint cookieManager);
    [PreserveSig] int get_Environment(out nint environment);
}

/// <summary>版本 3：挂起/恢复与虚拟主机映射。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("a0d6df20-3b92-416d-aa0c-437a9c727857")]
internal partial interface ICoreWebView2_3 : ICoreWebView2_2
{
    [PreserveSig] int TrySuspend(nint handler);
    [PreserveSig] int Resume();
    [PreserveSig] int get_IsSuspended(out int isSuspended);
    [PreserveSig] int SetVirtualHostNameToFolderMapping(string hostName, string folderPath, int accessKind);
    [PreserveSig] int ClearVirtualHostNameToFolderMapping(string hostName);
}

/// <summary>版本 4：Frame 创建与下载事件。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("20d02d59-6df2-42dc-bd06-f98a694b1302")]
internal partial interface ICoreWebView2_4 : ICoreWebView2_3
{
    [PreserveSig] int add_FrameCreated(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_FrameCreated(EventRegistrationToken token);
    [PreserveSig] int add_DownloadStarting(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_DownloadStarting(EventRegistrationToken token);
}

/// <summary>版本 5：客户端证书请求事件。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("bedb11b8-d63c-11eb-b8bc-0242ac130003")]
internal partial interface ICoreWebView2_5 : ICoreWebView2_4
{
    [PreserveSig] int add_ClientCertificateRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ClientCertificateRequested(EventRegistrationToken token);
}

/// <summary>版本 6：任务管理器窗口。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("499aadac-d92c-4589-8a75-111bfc167795")]
internal partial interface ICoreWebView2_6 : ICoreWebView2_5
{
    [PreserveSig] int OpenTaskManagerWindow();
}

/// <summary>版本 7：打印为 PDF。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("79c24d83-09a3-45ae-9418-487f32a58740")]
internal partial interface ICoreWebView2_7 : ICoreWebView2_6
{
    [PreserveSig] int PrintToPdf(string resultFilePath, nint printSettings, nint handler);
}

/// <summary>版本 8：静音与音频播放状态。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("e9632730-6e1e-43ab-b7b8-7b2c9e62e094")]
internal partial interface ICoreWebView2_8 : ICoreWebView2_7
{
    [PreserveSig] int add_IsMutedChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_IsMutedChanged(EventRegistrationToken token);
    [PreserveSig] int get_IsMuted(out int value);
    [PreserveSig] int put_IsMuted(int value);
    [PreserveSig] int add_IsDocumentPlayingAudioChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_IsDocumentPlayingAudioChanged(EventRegistrationToken token);
    [PreserveSig] int get_IsDocumentPlayingAudio(out int value);
}

/// <summary>版本 9：默认下载对话框。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("4d7b2eab-9fdc-468d-b998-a9260b5ed651")]
internal partial interface ICoreWebView2_9 : ICoreWebView2_8
{
    [PreserveSig] int add_IsDefaultDownloadDialogOpenChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_IsDefaultDownloadDialogOpenChanged(EventRegistrationToken token);
    [PreserveSig] int get_IsDefaultDownloadDialogOpen(out int value);
    [PreserveSig] int OpenDefaultDownloadDialog();
    [PreserveSig] int CloseDefaultDownloadDialog();
    [PreserveSig] int get_DefaultDownloadDialogCornerAlignment(out int value);
    [PreserveSig] int put_DefaultDownloadDialogCornerAlignment(int value);
    [PreserveSig] int get_DefaultDownloadDialogMargin(out WV2POINT value);
    [PreserveSig] int put_DefaultDownloadDialogMargin(WV2POINT value);
}

/// <summary>版本 10：基础认证请求事件。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b1690564-6f5a-4983-8e48-31d1143fecdb")]
internal partial interface ICoreWebView2_10 : ICoreWebView2_9
{
    [PreserveSig] int add_BasicAuthenticationRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_BasicAuthenticationRequested(EventRegistrationToken token);
}

/// <summary>版本 11：会话级 CDP 调用与右键菜单事件。本库的内建右键菜单依赖它。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0be78e56-c193-4051-b943-23b460c08bdb")]
internal partial interface ICoreWebView2_11 : ICoreWebView2_10
{
    [PreserveSig] int CallDevToolsProtocolMethodForSession(string sessionId, string methodName, string parametersAsJson, nint handler);
    [PreserveSig] int add_ContextMenuRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ContextMenuRequested(EventRegistrationToken token);
}

/// <summary>版本 12：状态栏文本。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("35d69927-bcfa-4566-9349-6b3e0d154cac")]
internal partial interface ICoreWebView2_12 : ICoreWebView2_11
{
    [PreserveSig] int add_StatusBarTextChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_StatusBarTextChanged(EventRegistrationToken token);
    [PreserveSig] int get_StatusBarText(out nint value);
}

/// <summary>版本 13：Profile。清除浏览数据走这条路。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f75f09a8-667e-4983-88d6-c8773f315e84")]
internal partial interface ICoreWebView2_13 : ICoreWebView2_12
{
    [PreserveSig] int get_Profile(out nint value);
}
