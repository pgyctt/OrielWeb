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
                backend.RaiseClicked();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 托盘点击回调抛出异常：{ex}");
        }
        return 0;
    }
}
