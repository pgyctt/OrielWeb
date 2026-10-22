using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 托盘状态项按钮的 ObjC target：只负责"按钮被点了"。
/// </summary>
/// <remarks>
/// 菜单项的点击由 <see cref="MacOSMenu"/> 自带的 target 处理（每个菜单一个，tag 映射在其内部）——
/// 状态项按钮与菜单是两个不同的接收者，混在一个类里只会让状态表多一层间接。
/// 约定与 <see cref="MacOSObjCClasses"/> 一致：trampoline 必须全身 try/catch
/// （托管异常穿越 ObjC 边界会 fail-fast）。
/// </remarks>
internal static unsafe class MacOSTrayHandler
{
    private static nint s_class;

    /// <summary>handler 实例指针 → 后端（实例在创建时 retain，指针稳定）。</summary>
    private static readonly Dictionary<nint, MacOSTrayBackend> s_states = [];

    /// <summary>
    /// 在状态项按钮上安装跟踪区域（<c>NSTrackingArea</c>），让 AppKit 派发 Enter/Move/Leave。
    /// </summary>
    /// <param name="button">状态项按钮。</param>
    /// <param name="handler">本托盘自己的 handler（owner）：多托盘下每个按钮各装一个区域，
    /// owner 必须是它自己那一个，否则事件会派发到别的托盘上。</param>
    /// <remarks>
    /// <para>
    /// 没有它，按钮只会收到 action（点击），拿不到任何悬停事件——而 Enter/Move/Leave 是对标
    /// Tauri 2 事件面所必需的。
    /// </para>
    /// <para>
    /// 选项含义（AppKit）：<c>NSTrackingMouseEnteredAndExited</c>(0x01) 进出、
    /// <c>NSTrackingMouseMoved</c>(0x02) 移动、<c>NSTrackingActiveAlways</c>(0x80) 应用不在前台也要跟踪
    /// （托盘在别的应用前台时也可能被划过，不给它就拿不到事件）、
    /// <c>NSTrackingInVisibleRect</c>(0x200) 自动跟随控件尺寸（不必自己维护 rect）。
    /// </para>
    /// </remarks>
    internal static void InstallTrackingArea(nint button, nint handler)
    {
        if (button == 0 || handler == 0)
        {
            return;
        }

        const int NSTrackingMouseEnteredAndExited = 0x01;
        const int NSTrackingMouseMoved = 0x02;
        const int NSTrackingActiveAlways = 0x80;
        const int NSTrackingInVisibleRect = 0x200;
        int options = NSTrackingMouseEnteredAndExited | NSTrackingMouseMoved
            | NSTrackingActiveAlways | NSTrackingInVisibleRect;

        nint cls = ObjCRuntime.GetClassOrThrow("NSTrackingArea");
        nint alloc = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("alloc"));

        // initWithRect:options:owner:userInfo: —— rect 用空矩形（InVisibleRect 会忽略它），
        // owner 是托盘 handler（它实现了 mouseEntered:/mouseExited:/mouseMoved:）。
        nint area = ObjCRuntime.SendIdRectNintObjObj(
            alloc,
            ObjCRuntime.Sel("initWithRect:options:owner:userInfo:"),
            new ObjCRuntime.NSRect(0, 0, 0, 0),
            (nuint)options,
            handler,
            0);

        if (area == 0)
        {
            OrielDiagnostics.Report("[OrielWeb] 安装托盘跟踪区域失败（NSTrackingArea 返回 nil）。");
            return;
        }

        ObjCRuntime.SendVoidObj(button, ObjCRuntime.Sel("addTrackingArea:"), area);
    }

    /// <summary>创建 handler 并登记状态；返回值由调用方持有（释放时调 <see cref="Remove"/>）。</summary>
    internal static nint Create(MacOSTrayBackend backend)
    {
        nint instance = AllocInit(EnsureClass());
        s_states[instance] = backend;
        return instance;
    }

    internal static void Remove(nint handler) => s_states.Remove(handler);

    private static nint EnsureClass()
    {
        if (s_class != 0)
        {
            return s_class;
        }

        nint cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielTrayHandler", 0);
        // v@:@（无返回值、self、_cmd、sender）
        ObjCRuntime.class_addMethod(
            cls, ObjCRuntime.Sel("orielStatusItemClicked:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&StatusItemClicked, "v@:@");

        // 悬停事件（对标 Tauri 2 的 Enter/Move/Leave）：按钮上装了 NSTrackingArea 之后，
        // AppKit 会回调这两个 selector。v@:@（参数是 NSEvent*）。
        ObjCRuntime.class_addMethod(
            cls, ObjCRuntime.Sel("mouseEntered:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&MouseEntered, "v@:@");
        ObjCRuntime.class_addMethod(
            cls, ObjCRuntime.Sel("mouseExited:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&MouseExited, "v@:@");
        ObjCRuntime.class_addMethod(
            cls, ObjCRuntime.Sel("mouseMoved:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&MouseMoved, "v@:@");

        ObjCRuntime.objc_registerClassPair(cls);
        s_class = cls;
        return cls;
    }

    private static nint AllocInit(nint cls)
    {
        nint instance = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("alloc"));
        instance = ObjCRuntime.SendId(instance, ObjCRuntime.Sel("init"));
        ObjCRuntime.objc_retain(instance);
        return instance;
    }

    [UnmanagedCallersOnly]
    private static nint StatusItemClicked(nint self, nint sel, nint sender)
    {
        try
        {
            if (s_states.TryGetValue(self, out MacOSTrayBackend? backend))
            {
                backend.RaiseRawEvent("orielStatusItemClicked:（状态项按钮 action）");
                backend.RaiseEventReceived();
                backend.RaiseClicked();
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘点击回调抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint MouseEntered(nint self, nint sel, nint eventPtr)
    {
        try
        {
            if (s_states.TryGetValue(self, out MacOSTrayBackend? backend))
            {
                backend.RaiseRawEvent("mouseEntered:（指针进入状态项按钮）");
                backend.RaiseHoverEvent(OrielTrayEventType.Enter, eventPtr);
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘悬停回调抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint MouseExited(nint self, nint sel, nint eventPtr)
    {
        try
        {
            if (s_states.TryGetValue(self, out MacOSTrayBackend? backend))
            {
                backend.RaiseRawEvent("mouseExited:（指针离开状态项按钮）");
                backend.RaiseHoverEvent(OrielTrayEventType.Leave, eventPtr);
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘悬停回调抛出异常：{ex}");
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint MouseMoved(nint self, nint sel, nint eventPtr)
    {
        try
        {
            if (s_states.TryGetValue(self, out MacOSTrayBackend? backend))
            {
                backend.RaiseHoverEvent(OrielTrayEventType.Move, eventPtr);
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 托盘悬停回调抛出异常：{ex}");
        }
        return 0;
    }
}
