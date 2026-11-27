using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>
/// 指针信息（触摸 / 笔）：组合宿主把系统指针输入转发给 WebView 时携带的载荷。
/// </summary>
/// <remarks>
/// <para>
/// <b>成员顺序必须与 <c>WebView2.h</c> 的 vtable 完全一致。</b> COM 按槽位调用，
/// 顺序错了不会报错，只会把值写进相邻字段——现象是触摸"毫无反应"或坐标乱跳，
/// 而且因为 HRESULT 一直是 S_OK，日志里什么都看不到。所以下面按 WebView2.h 里
/// <c>ICoreWebView2PointerInfo</c> 的声明顺序逐条抄录，<b>不要重排、不要删中间的成员</b>
/// （哪怕本项目用不到 Pen*），否则它后面的槽位全部错位。
/// </para>
/// <para>
/// 本库只用到其中一部分（PointerKind / PointerId / FrameId / PointerFlags /
/// PixelLocation / Time / Touch*），其余为对齐保留。
/// </para>
/// <para>
/// IID 取自 WebView2.h 的 <c>MIDL_INTERFACE("e6995887-d10d-4f5d-9359-4ce46e4f96b9")</c>。
/// 网上流传的 <c>E6995887-D10D-4D4D-935E-4B4F9C0C4B9E</c> 是错的，用错会让 QI 直接失败。
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("e6995887-d10d-4f5d-9359-4ce46e4f96b9")]
internal partial interface ICoreWebView2PointerInfo
{
    // 1. PointerKind（DWORD = POINTER_INPUT_TYPE）
    [PreserveSig] int get_PointerKind(out uint pointerKind);
    [PreserveSig] int put_PointerKind(uint pointerKind);

    // 2. PointerId
    [PreserveSig] int get_PointerId(out uint pointerId);
    [PreserveSig] int put_PointerId(uint pointerId);

    // 3. FrameId
    [PreserveSig] int get_FrameId(out uint frameId);
    [PreserveSig] int put_FrameId(uint frameId);

    // 4. PointerFlags（POINTER_FLAGS 位掩码）
    [PreserveSig] int get_PointerFlags(out uint pointerFlags);
    [PreserveSig] int put_PointerFlags(uint pointerFlags);

    // 5. PointerDeviceRect
    [PreserveSig] int get_PointerDeviceRect(out RECT pointerDeviceRect);
    [PreserveSig] int put_PointerDeviceRect(RECT pointerDeviceRect);

    // 6. DisplayRect
    [PreserveSig] int get_DisplayRect(out RECT displayRect);
    [PreserveSig] int put_DisplayRect(RECT displayRect);

    // 7. PixelLocation（**WebView 物理坐标**，不是屏幕坐标）
    [PreserveSig] int get_PixelLocation(out POINT pixelLocation);
    [PreserveSig] int put_PixelLocation(POINT pixelLocation);

    // 8. HimetricLocation
    [PreserveSig] int get_HimetricLocation(out POINT himetricLocation);
    [PreserveSig] int put_HimetricLocation(POINT himetricLocation);

    // 9. PixelLocationRaw
    [PreserveSig] int get_PixelLocationRaw(out POINT pixelLocationRaw);
    [PreserveSig] int put_PixelLocationRaw(POINT pixelLocationRaw);

    // 10. HimetricLocationRaw
    [PreserveSig] int get_HimetricLocationRaw(out POINT himetricLocationRaw);
    [PreserveSig] int put_HimetricLocationRaw(POINT himetricLocationRaw);

    // 11. Time
    [PreserveSig] int get_Time(out uint time);
    [PreserveSig] int put_Time(uint time);

    // 12. HistoryCount
    [PreserveSig] int get_HistoryCount(out uint historyCount);
    [PreserveSig] int put_HistoryCount(uint historyCount);

    // 13. InputData
    [PreserveSig] int get_InputData(out int inputData);
    [PreserveSig] int put_InputData(int inputData);

    // 14. KeyStates
    [PreserveSig] int get_KeyStates(out uint keyStates);
    [PreserveSig] int put_KeyStates(uint keyStates);

    // 15. PerformanceCount
    [PreserveSig] int get_PerformanceCount(out ulong performanceCount);
    [PreserveSig] int put_PerformanceCount(ulong performanceCount);

    // 16. ButtonChangeKind
    [PreserveSig] int get_ButtonChangeKind(out int buttonChangeKind);
    [PreserveSig] int put_ButtonChangeKind(int buttonChangeKind);

    // 17-22. 笔相关（本库不写，保留只为对齐 vtable）
    [PreserveSig] int get_PenFlags(out uint penFlags);
    [PreserveSig] int put_PenFlags(uint penFlags);

    [PreserveSig] int get_PenMask(out uint penMask);
    [PreserveSig] int put_PenMask(uint penMask);

    [PreserveSig] int get_PenPressure(out uint penPressure);
    [PreserveSig] int put_PenPressure(uint penPressure);

    [PreserveSig] int get_PenRotation(out uint penRotation);
    [PreserveSig] int put_PenRotation(uint penRotation);

    [PreserveSig] int get_PenTiltX(out int penTiltX);
    [PreserveSig] int put_PenTiltX(int penTiltX);

    [PreserveSig] int get_PenTiltY(out int penTiltY);
    [PreserveSig] int put_PenTiltY(int penTiltY);

    // 23-28. 触摸相关（本库会写 TouchFlags / TouchMask / TouchContact）
    [PreserveSig] int get_TouchFlags(out uint touchFlags);
    [PreserveSig] int put_TouchFlags(uint touchFlags);

    [PreserveSig] int get_TouchMask(out uint touchMask);
    [PreserveSig] int put_TouchMask(uint touchMask);

    [PreserveSig] int get_TouchContact(out RECT touchContact);
    [PreserveSig] int put_TouchContact(RECT touchContact);

    [PreserveSig] int get_TouchContactRaw(out RECT touchContactRaw);
    [PreserveSig] int put_TouchContactRaw(RECT touchContactRaw);

    [PreserveSig] int get_TouchOrientation(out uint touchOrientation);
    [PreserveSig] int put_TouchOrientation(uint touchOrientation);

    [PreserveSig] int get_TouchPressure(out uint touchPressure);
    [PreserveSig] int put_TouchPressure(uint touchPressure);
}
