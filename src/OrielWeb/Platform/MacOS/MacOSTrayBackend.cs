using System.Diagnostics;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 托盘后端：<c>NSStatusBar</c> 状态项 + <c>NSMenu</c>。
/// </summary>
/// <remarks>
/// <para>
/// 菜单直接设给状态项（<c>statusItem.setMenu:</c>）——这是 macOS 的惯例：左键点击即弹菜单，
/// 系统因此不再派发按钮 action。于是"设了菜单"时 <see cref="OrielTray.Clicked"/> 不触发，
/// 这是平台行为而不是遗漏（<see cref="OrielTrayOptions.MenuOnClick"/> 在 macOS 上没有意义）。
/// </para>
/// <para>
/// 菜单项点击统一走一个 selector，用 <c>tag</c> 带回项索引：为每个项动态生成 selector
/// 既没必要（还得回收）也不便调试，而 tag 正是 AppKit 为这种场景准备的字段。
/// </para>
/// </remarks>
internal sealed class MacOSTrayBackend : ITrayBackend
{
    /// <summary>NSVariableStatusItemLength：宽度随内容自适应。</summary>
    private const double VariableStatusItemLength = -1;

    private readonly OrielApp _app;
    private nint _statusItem;
    private nint _button;
    private nint _handler;
    private nint _menu;
    private readonly List<OrielMenuItem> _targets = [];
    private bool _disposed;

    public event Action? Clicked;
    public event Action<string>? MenuItemClicked;

    internal MacOSTrayBackend(OrielApp app, OrielTrayOptions options)
    {
        _app = app;

        nint statusBar = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSStatusBar"), ObjCRuntime.Sel("systemStatusBar"));
        _statusItem = ObjCRuntime.SendIdDouble(
            statusBar, ObjCRuntime.Sel("statusItemWithLength:"), VariableStatusItemLength);
        if (_statusItem == 0)
        {
            throw new InvalidOperationException("创建状态项失败（NSStatusBar.statusItemWithLength: 返回 nil）。");
        }

        _handler = MacOSTrayHandler.Create(this);
        _button = ObjCRuntime.SendId(_statusItem, ObjCRuntime.Sel("button"));
        if (_button != 0)
        {
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTarget:"), _handler);
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setAction:"), ObjCRuntime.Sel("orielStatusItemClicked:"));
        }

        ApplyIcon(options.IconPath);
        SetTooltip(options.Tooltip);
    }

    // ---- ITrayBackend ----

    public void SetTooltip(string tooltip)
    {
        if (_button != 0)
        {
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setToolTip:"), ObjCRuntime.MakeNSString(tooltip));
        }
    }

    public void SetIcon(string? iconPath) => ApplyIcon(iconPath);

    public void SetMenu(IReadOnlyList<OrielMenuItem> items)
    {
        _targets.Clear();

        // setMenu:nil 会解除菜单关联，按钮 action 随之恢复（于是 Clicked 又能触发了）
        _menu = items.Count == 0 ? 0 : BuildMenu(items);
        ObjCRuntime.SendVoidObj(_statusItem, ObjCRuntime.Sel("setMenu:"), _menu);
    }

    public void Show()
    {
        if (_statusItem != 0)
        {
            ObjCRuntime.SendVoidBool(_statusItem, ObjCRuntime.Sel("setVisible:"), true);
        }
    }

    /// <summary>状态项创建成功且未被隐藏（macOS 的状态项一旦创建就会出现在菜单栏）。</summary>
    public bool IsVisible
        => _statusItem != 0 && ObjCRuntime.SendBoolRet(_statusItem, ObjCRuntime.Sel("isVisible"));

    public void Hide()
    {
        if (_statusItem != 0)
        {
            ObjCRuntime.SendVoidBool(_statusItem, ObjCRuntime.Sel("setVisible:"), false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_statusItem != 0)
        {
            nint statusBar = ObjCRuntime.SendId(
                ObjCRuntime.GetClassOrThrow("NSStatusBar"), ObjCRuntime.Sel("systemStatusBar"));
            ObjCRuntime.SendVoidObj(statusBar, ObjCRuntime.Sel("removeStatusItem:"), _statusItem);
            _statusItem = 0;
        }

        _button = 0;
        _menu = 0;
        _targets.Clear();

        if (_handler != 0)
        {
            MacOSTrayHandler.Remove(_handler);
            ObjCRuntime.objc_release(_handler);
            _handler = 0;
        }
    }

    // ---- 供 handler 回调 ----

    internal void RaiseClicked() => Clicked?.Invoke();

    internal void OnMenuItemActivated(nint tag)
    {
        int index = (int)tag;
        if (index < 0 || index >= _targets.Count)
        {
            return;
        }

        Activate(_targets[index]);
    }

    // ---- 菜单构建 ----

    private nint BuildMenu(IReadOnlyList<OrielMenuItem> items)
    {
        nint menu = ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSMenu"), ObjCRuntime.Sel("alloc"));
        menu = ObjCRuntime.SendId(menu, ObjCRuntime.Sel("init"));

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
            ObjCRuntime.MakeNSString(BuildTitle(item)),
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
            ObjCRuntime.SendVoidObj(menuItem, ObjCRuntime.Sel("setSubmenu:"), BuildMenu(item.Items!));
        }
        else if (modifierMask != 0)
        {
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setKeyEquivalentModifierMask:"), modifierMask);
        }

        return menuItem;
    }

    private static string BuildTitle(OrielMenuItem item)
        => item.Label ?? DefaultLabel(item.Role) ?? string.Empty;

    private static string? DefaultLabel(string? role) => role switch
    {
        OrielMenuRole.Quit => "Quit",
        OrielMenuRole.Close => "Close",
        OrielMenuRole.Minimize => "Minimize",
        OrielMenuRole.Zoom => "Zoom",
        OrielMenuRole.ToggleFullScreen => "Enter Full Screen",
        OrielMenuRole.About => "About",
        _ => null,
    };

    /// <summary>
    /// 加速键 → AppKit 的 key equivalent。macOS 上这是**真快捷键**（系统会拦下按键），
    /// 与 Windows/Linux 上只作显示不同。
    /// </summary>
    /// <remarks>
    /// 只映射单字符键：AppKit 把方向键/F 键放在 0xF700 段私有码位里，映射它们要再维护一张码位表，
    /// 收益不抵复杂度——这类加速键在 macOS 上就是没有快捷键（菜单仍可点）。
    /// </remarks>
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
        // NSEventModifierFlag*（其中 Command = 1<<20，与 Windows 的 Win 键复用同一含义）
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

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            ActivateRole(role);
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
        }
    }

    /// <summary>托盘里可用的 role 只有应用级动作（与 Windows/Linux 侧同一取舍）。</summary>
    private void ActivateRole(string role)
    {
        if (role == OrielMenuRole.Quit)
        {
            _app.Quit();
        }
    }

    private void ApplyIcon(string? iconPath)
    {
        nint image = 0;
        if (!string.IsNullOrEmpty(iconPath))
        {
            if (!File.Exists(iconPath))
            {
                Debug.WriteLine($"[OrielWeb] 托盘图标文件不存在：{iconPath}");
            }
            else
            {
                image = ObjCRuntime.SendIdObj(
                    ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSImage"), ObjCRuntime.Sel("alloc")),
                    ObjCRuntime.Sel("initWithContentsOfFile:"),
                    ObjCRuntime.MakeNSString(iconPath));

                if (image != 0)
                {
                    // 模板图会自动跟随菜单栏深浅色反色——托盘图标基本都该这样，
                    // 否则深色菜单栏上是一块看不清的黑图
                    ObjCRuntime.SendVoidBool(image, ObjCRuntime.Sel("setTemplate:"), true);
                }
            }
        }

        if (_button == 0)
        {
            return;
        }

        if (image != 0)
        {
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setImage:"), image);
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString(string.Empty));
        }
        else
        {
            // 无图标时给一个可见占位：否则状态项是空白的，用户会以为程序没起来
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString("●"));
        }
    }
}
