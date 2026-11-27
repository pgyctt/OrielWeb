using System.Reflection;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 触摸转发依赖的两个"错了也不报错"的前提：结构布局与 COM vtable 顺序。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这两件事必须钉住</b>：<c>GetPointerTouchInfo</c> 按结构布局往托管内存里写字段，
/// 布局差一个字段，触摸坐标就整体错位（页面滚动位置乱跳，或者根本没有滚动）；
/// <c>SendPointerInput</c> 按 vtable 槽位调用 <c>ICoreWebView2PointerInfo</c> 的方法，
/// 顺序错了会把 <c>pixelLocation</c> 写进 <c>displayRect</c> 之类的字段——同样没有任何 HRESULT 错误。
/// 两者都只在**真机触摸**下才暴露，而无头 CI 永远不会碰，所以用断言固定住。
/// </para>
/// <para>
/// 期望值来自 Windows SDK 的 <c>winuser.h</c>（结构）与 WebView2.h（vtable）。
/// </para>
/// </remarks>
public sealed class Win32PointerLayoutTests
{
    [Fact]
    public void PointerInfo_HasWindowsSdkLayout()
    {
        // x64: 4×UINT32 + 2×指针 + 4×POINT + 4×UINT32 + UINT64 + INT32，对齐后 96 字节。
        // x86 的指针是 4 字节，整体 80 字节——CI 只在 x64 上跑，这里明确区分而不是写死。
        int expected = IntPtr.Size == 8 ? 96 : 80;
        Assert.Equal(expected, Marshal.SizeOf<POINTER_INFO>());
    }

    [Fact]
    public void PointerTouchInfo_HasWindowsSdkLayout()
    {
        int expected = IntPtr.Size == 8 ? 144 : 128;
        Assert.Equal(expected, Marshal.SizeOf<POINTER_TOUCH_INFO>());
    }

    [Fact]
    public void PointerTouchInfo_EmbedsPointerInfoAtOffsetZero()
    {
        // 第一个字段必须是 POINTER_INFO 本体，而不是它的副本或指针：
        // GetPointerTouchInfo 直接把整块内存当成 POINTER_TOUCH_INFO 填。
        Assert.Equal(0, (int)Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.pointerInfo)));
    }

    [Fact]
    public void PointerTouchInfo_FieldsFollowPointerInfoInOrder()
    {
        // 顺序决定 touchMask 之后那几个字段能不能被正确读取。
        long afterPointerInfo = Marshal.SizeOf<POINTER_INFO>();

        Assert.True(Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.touchFlags)) >= afterPointerInfo);
        Assert.True(
            Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.touchMask))
            > Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.touchFlags)));
        Assert.True(
            Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.rcContact))
            > Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.touchMask)));
        Assert.True(
            Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.pressure))
            > Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.orientation)));
    }

    [Fact]
    public void PointerInfoInterface_UsesTheHeaderIid()
    {
        // WebView2.h: MIDL_INTERFACE("e6995887-d10d-4f5d-9359-4ce46e4f96b9")
        // 用网上流传的 E6995887-D10D-4D4D-935E-4B4F9C0C4B9E 会让 QI 直接失败 → 触摸静默不工作。
        Assert.Equal(
            new Guid("e6995887-d10d-4f5d-9359-4ce46e4f96b9"),
            typeof(ICoreWebView2PointerInfo).GUID);
    }

    [Fact]
    public void PointerInfoInterface_VtableOrderMatchesWebView2Header()
    {
        // 必须是 WebView2.h 里 ICoreWebView2PointerInfo 的声明顺序（成对出现）。
        // 重排、删除或插入任何一条都会让后面的槽位整体错位。
        string[] expected =
        [
            "get_PointerKind", "put_PointerKind",
            "get_PointerId", "put_PointerId",
            "get_FrameId", "put_FrameId",
            "get_PointerFlags", "put_PointerFlags",
            "get_PointerDeviceRect", "put_PointerDeviceRect",
            "get_DisplayRect", "put_DisplayRect",
            "get_PixelLocation", "put_PixelLocation",
            "get_HimetricLocation", "put_HimetricLocation",
            "get_PixelLocationRaw", "put_PixelLocationRaw",
            "get_HimetricLocationRaw", "put_HimetricLocationRaw",
            "get_Time", "put_Time",
            "get_HistoryCount", "put_HistoryCount",
            "get_InputData", "put_InputData",
            "get_KeyStates", "put_KeyStates",
            "get_PerformanceCount", "put_PerformanceCount",
            "get_ButtonChangeKind", "put_ButtonChangeKind",
            "get_PenFlags", "put_PenFlags",
            "get_PenMask", "put_PenMask",
            "get_PenPressure", "put_PenPressure",
            "get_PenRotation", "put_PenRotation",
            "get_PenTiltX", "put_PenTiltX",
            "get_PenTiltY", "put_PenTiltY",
            "get_TouchFlags", "put_TouchFlags",
            "get_TouchMask", "put_TouchMask",
            "get_TouchContact", "put_TouchContact",
            "get_TouchContactRaw", "put_TouchContactRaw",
            "get_TouchOrientation", "put_TouchOrientation",
            "get_TouchPressure", "put_TouchPressure",
        ];

        // 接口成员的 MetadataToken 在同一模块内按声明顺序递增，用它还原源码顺序。
        string[] actual = [.. typeof(ICoreWebView2PointerInfo)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(m => m.MetadataToken)
            .Select(m => m.Name)];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PointerEventKindValuesMatchWindowMessages()
    {
        // COREWEBVIEW2_POINTER_EVENT_KIND 的取值就是 WM_POINTER* 的消息号，
        // Win32WindowHost 因此可以把 message 直接当 eventKind 传给 SendPointerInput。
        // 这条断言把"直接透传"这个前提也钉住。
        Assert.Equal(0x0245u, Win32Constants.WM_POINTERUPDATE);  // 581 = COREWEBVIEW2_POINTER_EVENT_KIND_UPDATE
        Assert.Equal(0x0246u, Win32Constants.WM_POINTERDOWN);    // 582 = COREWEBVIEW2_POINTER_EVENT_KIND_DOWN
        Assert.Equal(0x0247u, Win32Constants.WM_POINTERUP);      // 583 = COREWEBVIEW2_POINTER_EVENT_KIND_UP
        Assert.Equal(0x0249u, Win32Constants.WM_POINTERENTER);   // 585 = COREWEBVIEW2_POINTER_EVENT_KIND_ENTER
        Assert.Equal(0x024Au, Win32Constants.WM_POINTERLEAVE);   // 586 = COREWEBVIEW2_POINTER_EVENT_KIND_LEAVE
    }
}
