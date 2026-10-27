using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>控制器：bounds / 可见性 / 缩放 / 焦点。组合控制器从它派生而来（同一对象的不同接口）。</summary>
/// <remarks>
/// 只有本库真正订阅的事件才用强类型 handler；其余收成 <c>nint</c>——
/// <c>[GeneratedComInterface]</c> 会把继承链上每个方法都生成 stub，
/// 每个强类型参数都要求那个 handler 接口存在，声明面会无谓地膨胀。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("4d00c0d1-9434-4eb6-8078-8697a560334f")]
internal partial interface ICoreWebView2Controller
{
    [PreserveSig] int get_IsVisible(out int isVisible);
    [PreserveSig] int put_IsVisible(int isVisible);
    [PreserveSig] int get_Bounds(out WV2RECT bounds);
    [PreserveSig] int put_Bounds(WV2RECT bounds);
    [PreserveSig] int get_ZoomFactor(out double zoomFactor);
    [PreserveSig] int put_ZoomFactor(double zoomFactor);
    [PreserveSig] int add_ZoomFactorChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_ZoomFactorChanged(EventRegistrationToken token);
    [PreserveSig] int SetBoundsAndZoomFactor(WV2RECT bounds, double zoomFactor);
    [PreserveSig] int MoveFocus(int reason);
    [PreserveSig] int add_MoveFocusRequested(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_MoveFocusRequested(EventRegistrationToken token);
    [PreserveSig] int add_GotFocus(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_GotFocus(EventRegistrationToken token);
    [PreserveSig] int add_LostFocus(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_LostFocus(EventRegistrationToken token);
    [PreserveSig] int add_AcceleratorKeyPressed(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_AcceleratorKeyPressed(EventRegistrationToken token);
    [PreserveSig] int get_ParentWindow(out nint parentWindow);
    [PreserveSig] int put_ParentWindow(nint parentWindow);
    [PreserveSig] int NotifyParentWindowPositionChanged();
    [PreserveSig] int Close();
    [PreserveSig] int get_CoreWebView2(out nint coreWebView2);
}

/// <summary>控制器 2：默认背景色。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab")]
internal partial interface ICoreWebView2Controller2 : ICoreWebView2Controller
{
    [PreserveSig] int get_DefaultBackgroundColor(out COREWEBVIEW2_COLOR backgroundColor);
    [PreserveSig] int put_DefaultBackgroundColor(COREWEBVIEW2_COLOR backgroundColor);
}

/// <summary>控制器 3：光栅化缩放与 bounds 模式。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f9614724-5d2b-41dc-aef7-73d62b51543b")]
internal partial interface ICoreWebView2Controller3 : ICoreWebView2Controller2
{
    [PreserveSig] int get_RasterizationScale(out double scale);
    [PreserveSig] int put_RasterizationScale(double scale);
    [PreserveSig] int get_ShouldDetectMonitorScaleChanges(out int value);
    [PreserveSig] int put_ShouldDetectMonitorScaleChanges(int value);
    [PreserveSig] int add_RasterizationScaleChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_RasterizationScaleChanged(EventRegistrationToken token);
    [PreserveSig] int get_BoundsMode(out int boundsMode);
    [PreserveSig] int put_BoundsMode(int boundsMode);
}

/// <summary>控制器 4：外部拖放开关。关掉它才能让 OLE 拖放落到宿主窗口。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("97d418d5-a426-4e49-a151-e1a10f327d9e")]
internal partial interface ICoreWebView2Controller4 : ICoreWebView2Controller3
{
    [PreserveSig] int get_AllowExternalDrop(out int value);
    [PreserveSig] int put_AllowExternalDrop(int value);
}

/// <summary>
/// 组合控制器：WebView2 作为 DirectComposition 视觉存在时的宿主接口。
/// </summary>
/// <remarks>
/// 它**不是** <see cref="ICoreWebView2Controller"/> 的派生接口，而是同一 COM 对象上的另一个接口，
/// 需要一次 QueryInterface。所以本库同时持有两份视图。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("3df9b733-b9ae-4a15-86b4-eb9ee9826469")]
internal partial interface ICoreWebView2CompositionController
{
    [PreserveSig] int get_RootVisualTarget(out nint target);
    [PreserveSig] int put_RootVisualTarget(nint target);
    [PreserveSig] int SendMouseInput(int eventKind, int virtualKeys, uint mouseData, WV2POINT point);
    [PreserveSig] int SendPointerInput(int eventType, nint pointerInfo);
    [PreserveSig] int get_Cursor(out nint cursor);
    [PreserveSig] int get_SystemCursorId(out uint systemCursorId);
    [PreserveSig] int add_CursorChanged(nint handler, out EventRegistrationToken token);
    [PreserveSig] int remove_CursorChanged(EventRegistrationToken token);
}
