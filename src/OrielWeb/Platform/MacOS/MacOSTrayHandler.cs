using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 托盘用运行时构建的 ObjC 类：承载状态项按钮与菜单项的 action。
/// </summary>
/// <remarks>
/// 单独一个类而不是并进 <see cref="MacOSObjCClasses"/>：托盘的生命周期与窗口无关，
/// 状态表也只服务托盘——放在一起只会让那个已经被窗口/脚本/导航三件事共享的文件更难读。
/// 约定与那里一致：trampoline 必须全身 try/catch（托管异常穿越 ObjC 边界会 fail-fast）。
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
        // 菜单项：v@:@（无返回值、self、_cmd、NSMenuItem*）
        ObjCRuntime.class_addMethod(
            cls, ObjCRuntime.Sel("orielMenuItemClicked:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&MenuItemClicked, "v@:@");
        // 状态项按钮：同一个签名
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
    private static nint MenuItemClicked(nint self, nint sel, nint menuItem)
    {
        try
        {
            if (s_states.TryGetValue(self, out MacOSTrayBackend? backend))
            {
                // tag 是 AppKit 为这种场景准备的整型字段，用来带回"点了哪一项"
                backend.OnMenuItemActivated(ObjCRuntime.SendId(menuItem, ObjCRuntime.Sel("tag")));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 托盘菜单回调抛出异常：{ex}");
        }
        return 0;
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
