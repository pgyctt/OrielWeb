using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// NSMenu 的构建、弹出与释放，自带一个 ObjC target 实例（托盘菜单与窗口上下文菜单共用）。
/// </summary>
/// <remarks>
/// <para>
/// 每个菜单**自带** target 对象，而不是共用一个全局 handler：菜单项点击时系统只告诉我们
/// "哪个 target 的哪个 tag 被点了"，tag 的编号空间天然属于单个菜单，把映射放在菜单对象内部
/// 就不需要额外的全局表，也不会出现两个菜单的 tag 互相串味。
/// </para>
/// <para>
/// 加速键（key equivalent）由 AppKit 在菜单打开时匹配，与 Windows/Linux 把按键留给页面不同，
/// 见 README 平台矩阵。只映射单字符键：方向键与 F 键在 AppKit 里要用 0xF700 段私有码位，
/// 映射它们要再维护一张码位表，收益不抵复杂度。
/// </para>
/// </remarks>
internal sealed unsafe class MacOSMenu : IDisposable
{
    // ---- handler 类（进程级单次注册）----

    private static nint s_handlerClass;

    /// <summary>handler 实例指针 → 菜单对象。</summary>
    private static readonly Dictionary<nint, MacOSMenu> s_handlers = [];

    private nint _handle;
    private nint _handler;
    private readonly List<OrielMenuItem> _targets = [];
    private readonly Action<OrielMenuItem> _onActivate;
    private bool _disposed;

    private MacOSMenu(Action<OrielMenuItem> onActivate) => _onActivate = onActivate;

    /// <summary>构建菜单；<paramref name="items"/> 为空时返回 null。</summary>
    internal static MacOSMenu? Build(IReadOnlyList<OrielMenuItem> items, Action<OrielMenuItem> onActivate)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var menu = new MacOSMenu(onActivate);
        menu._handler = CreateHandler(menu);
        menu._handle = menu.BuildCore(items);
        return menu._handle == 0 ? null : menu;
    }

    /// <summary>原生 NSMenu 句柄（<c>setMenu:</c>/<c>setSubmenu:</c> 需要它）。</summary>
    internal nint Handle => _handle;

    /// <summary>
    /// 在给定视图里、当前鼠标位置弹出（异步：返回后用户才可能选择）。
    /// 因此调用方要让菜单对象活到选择发生——不能弹出后立刻释放。
    /// </summary>
    internal void Popup(nint view)
    {
        if (_handle == 0 || _disposed)
        {
            return;
        }

        // popUpContextMenu:withEvent:forView: 是类方法：self 位置传 NSMenu 类对象。
        // event 传 NSApplication.currentEvent —— 位置由事件携带，这正是"在鼠标位置弹出"的来源；
        // 没有当前事件时（例如从后台回调里调用）AppKit 无法定位，此时明确不弹而不是弹到随机位置。
        nint currentEvent = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        currentEvent = ObjCRuntime.SendId(currentEvent, ObjCRuntime.Sel("currentEvent"));
        if (currentEvent == 0)
        {
            Debug.WriteLine("[OrielWeb] 当前没有 NSEvent，上下文菜单无法定位，已跳过弹出。");
            return;
        }

        ObjCRuntime.SendVoidObjObjObj(
            ObjCRuntime.GetClassOrThrow("NSMenu"),
            ObjCRuntime.Sel("popUpContextMenu:withEvent:forView:"),
            _handle,
            currentEvent,
            view);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targets.Clear();

        if (_handler != 0)
        {
            s_handlers.Remove(_handler);
            ObjCRuntime.objc_release(_handler);
            _handler = 0;
        }

        if (_handle != 0)
        {
            // 菜单可能已被 setMenu: 持有，release 只减少我们这一份引用
            ObjCRuntime.objc_release(_handle);
            _handle = 0;
        }
    }

    /// <summary>handler 回调入口：按 tag 找回菜单项。</summary>
    private void OnItemClicked(nint tag)
    {
        int index = (int)tag;
        if (index < 0 || index >= _targets.Count)
        {
            return;
        }

        _onActivate(_targets[index]);
    }

    // ---- 构建 ----

    private nint BuildCore(IReadOnlyList<OrielMenuItem> items)
    {
        nint menu = ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSMenu"), ObjCRuntime.Sel("alloc"));
        menu = ObjCRuntime.SendId(menu, ObjCRuntime.Sel("init"));
        if (menu == 0)
        {
            return 0;
        }

        foreach (OrielMenuItem item in items)
        {
            nint menuItem = BuildItem(item);
            if (menuItem != 0)
            {
                ObjCRuntime.SendVoidObj(menu, ObjCRuntime.Sel("addItem:"), menuItem);
            }
        }

        return menu;
    }

    private nint BuildItem(OrielMenuItem item)
    {
        if (item.IsSeparator)
        {
            return ObjCRuntime.SendId(
                ObjCRuntime.GetClassOrThrow("NSMenuItem"), ObjCRuntime.Sel("separatorItem"));
        }

        bool hasChildren = item.Items is { Count: > 0 };
        int tag = -1;
        if (!hasChildren)
        {
            tag = _targets.Count;
            _targets.Add(item);
        }

        (string keyEquivalent, nint modifierMask) = AcceleratorFor(item);

        nint allocated = ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSMenuItem"), ObjCRuntime.Sel("alloc"));
        nint menuItem = ObjCRuntime.SendIdObjObjObj(
            allocated,
            ObjCRuntime.Sel("initWithTitle:action:keyEquivalent:"),
            ObjCRuntime.MakeNSString(item.Label ?? OrielMenuRoles.DefaultLabel(item.Role) ?? string.Empty),
            hasChildren ? 0 : ObjCRuntime.Sel("orielMenuItemClicked:"),
            ObjCRuntime.MakeNSString(keyEquivalent));

        if (!hasChildren)
        {
            ObjCRuntime.SendVoidObj(menuItem, ObjCRuntime.Sel("setTarget:"), _handler);
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setTag:"), tag);
        }

        if (!item.Enabled)
        {
            ObjCRuntime.SendVoidBool(menuItem, ObjCRuntime.Sel("setEnabled:"), false);
        }
        if (item.Checked)
        {
            // NSControlStateValueOn = 1（macOS 会画成带对号的项）
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setState:"), 1);
        }

        if (hasChildren)
        {
            ObjCRuntime.SendVoidObj(menuItem, ObjCRuntime.Sel("setSubmenu:"), BuildCore(item.Items!));
        }
        else if (modifierMask != 0)
        {
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setKeyEquivalentModifierMask:"), modifierMask);
        }

        return menuItem;
    }

    private static (string KeyEquivalent, nint ModifierMask) AcceleratorFor(OrielMenuItem item)
    {
        if (item.Accelerator is not { Length: > 0 } acceleratorText
            || !OrielAccelerator.TryParse(acceleratorText, out OrielAccelerator? accelerator))
        {
            return (string.Empty, 0);
        }

        string key = accelerator!.Key;
        if (key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0]))
        {
            return (string.Empty, 0);
        }

        nint mask = 0;
        // NSEventModifierFlag*：Shift=1<<17、Control=1<<18、Option=1<<19、Command=1<<20
        if (accelerator.Shift)
        {
            mask |= 1 << 17;
        }
        if (accelerator.Control)
        {
            mask |= 1 << 18;
        }
        if (accelerator.Alt)
        {
            mask |= 1 << 19;
        }
        if (accelerator.Command)
        {
            mask |= 1 << 20;
        }

        return (char.ToLowerInvariant(key[0]).ToString(), mask);
    }

    // ---- ObjC handler ----

    private static nint CreateHandler(MacOSMenu menu)
    {
        if (s_handlerClass == 0)
        {
            s_handlerClass = BuildHandlerClass();
        }

        nint instance = ObjCRuntime.SendId(s_handlerClass, ObjCRuntime.Sel("alloc"));
        instance = ObjCRuntime.SendId(instance, ObjCRuntime.Sel("init"));
        ObjCRuntime.objc_retain(instance);
        s_handlers[instance] = menu;
        return instance;
    }

    private static nint BuildHandlerClass()
    {
        nint cls = ObjCRuntime.objc_allocateClassPair(ObjCRuntime.GetClass("NSObject"), "OrielMenuHandler", 0);
        ObjCRuntime.class_addMethod(
            cls,
            ObjCRuntime.Sel("orielMenuItemClicked:"),
            (delegate* unmanaged<nint, nint, nint, nint>)&MenuItemClicked,
            "v@:@");
        ObjCRuntime.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static nint MenuItemClicked(nint self, nint sel, nint menuItem)
    {
        try
        {
            if (s_handlers.TryGetValue(self, out MacOSMenu? menu))
            {
                // tag 是 AppKit 为这种场景准备的整型字段，用来带回"点了哪一项"
                menu.OnItemClicked(ObjCRuntime.SendId(menuItem, ObjCRuntime.Sel("tag")));
            }
        }
        catch (Exception ex)
        {
            // 托管异常穿越 ObjC 边界会 fail-fast，绝不能外泄
            Debug.WriteLine($"[OrielWeb] 菜单项回调抛出异常：{ex}");
        }
        return 0;
    }
}
