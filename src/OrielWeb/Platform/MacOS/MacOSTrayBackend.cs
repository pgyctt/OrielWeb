using System.Diagnostics;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 托盘后端：<c>NSStatusBar</c> 状态项 + <see cref="MacOSMenu"/>。
/// </summary>
/// <remarks>
/// <para>
/// 菜单直接设给状态项（<c>statusItem.setMenu:</c>）——这是 macOS 的惯例：左键点击即弹菜单，
/// 系统因此不再派发按钮 action。于是"设了菜单"时 <see cref="OrielTray.Clicked"/> 不触发，
/// 这是平台行为而不是遗漏（<see cref="OrielTrayOptions.MenuOnClick"/> 在 macOS 上没有意义）。
/// </para>
/// <para>
/// 菜单的构建与点击映射都在 <see cref="MacOSMenu"/> 里（与窗口上下文菜单、应用菜单栏共用）。
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
    private MacOSMenu? _menu;
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
        _menu?.Dispose();

        // setMenu:nil 会解除菜单关联，按钮 action 随之恢复（于是 Clicked 又能触发了）
        _menu = MacOSMenu.Build(items, Activate);
        ObjCRuntime.SendVoidObj(_statusItem, ObjCRuntime.Sel("setMenu:"), _menu?.Handle ?? 0);
    }

    public void Show()
    {
        if (_statusItem != 0)
        {
            ObjCRuntime.SendVoidBool(_statusItem, ObjCRuntime.Sel("setVisible:"), true);
        }
    }

    public void Hide()
    {
        if (_statusItem != 0)
        {
            ObjCRuntime.SendVoidBool(_statusItem, ObjCRuntime.Sel("setVisible:"), false);
        }
    }

    /// <summary>状态项创建成功且未被隐藏（macOS 的状态项一旦创建就会出现在菜单栏）。</summary>
    public bool IsVisible
        => _statusItem != 0 && ObjCRuntime.SendBoolRet(_statusItem, ObjCRuntime.Sel("isVisible"));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _menu?.Dispose();
        _menu = null;

        if (_statusItem != 0)
        {
            nint statusBar = ObjCRuntime.SendId(
                ObjCRuntime.GetClassOrThrow("NSStatusBar"), ObjCRuntime.Sel("systemStatusBar"));
            ObjCRuntime.SendVoidObj(statusBar, ObjCRuntime.Sel("removeStatusItem:"), _statusItem);
            _statusItem = 0;
        }

        _button = 0;

        if (_handler != 0)
        {
            MacOSTrayHandler.Remove(_handler);
            ObjCRuntime.objc_release(_handler);
            _handler = 0;
        }
    }

    // ---- 供 handler 回调 ----

    internal void RaiseClicked() => Clicked?.Invoke();

    // ---- 菜单项激活 ----

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            // 托盘没有"当前窗口"：窗口级与编辑类 role 在这里没有明确目标，忽略而不是猜一个窗口
            if (!OrielMenuRoles.TryActivate(role, _app, window: null))
            {
                Debug.WriteLine($"[OrielWeb] 托盘菜单的 role「{role}」在当前上下文无法执行，已忽略。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
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
