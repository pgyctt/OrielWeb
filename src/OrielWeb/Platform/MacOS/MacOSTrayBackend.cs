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
/// 菜单的构建与点击映射都在 <see cref="MacOSMenu"/> 里（与窗口上下文菜单共用）。
/// </para>
/// <para>
/// <b>多托盘</b>：每个实例各持一个 <c>NSStatusItem</c>，菜单栏上并列显示——
/// macOS 的 <c>NSStatusBar</c> 本来就支持任意多个状态项，无需额外协调。
/// </para>
/// <para>
/// <b>模板图</b>由 <see cref="OrielTrayOptions.IconAsTemplate"/> 控制（此前是无条件 true）。
/// 彩色品牌图标必须置 false，否则会被系统按 alpha 重绘成单色剪影。
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
    private bool _iconAsTemplate;
    private bool _disposed;

    public string Id { get; }

    /// <inheritdoc/>
    /// <remarks>macOS 上左键弹菜单是平台惯例（Tauri 有此开关而本库后端暂未接通），setter 抛错。</remarks>
    public bool MenuOnClick
    {
        get => _menuOnClick;
        set => throw new NotSupportedException(
            "macOS 的托盘左键行为由平台惯例决定，本库后端暂未接通运行期切换。");
    }

    private readonly bool _menuOnClick;

    public event Action? Clicked;
    public event Action<OrielTrayEvent>? EventReceived;
    public event Action<string>? MenuItemClicked;
    public event Action<string>? RawEvent;

    internal MacOSTrayBackend(OrielApp app, OrielTrayOptions options, string id)
    {
        _app = app;
        Id = id;
        _iconAsTemplate = options.IconAsTemplate;
        _menuOnClick = options.MenuOnClick;

        // 没有图形会话时 NSStatusBar 会走到"让进程 abort"的那条路上（与 NSMenu 同类），
        // 所以先拒绝。典型场景：SSH 到一台停在登录窗口的机器上跑自检。
        MacOSSession.RequireScreen("托盘状态项无法创建");

        nint statusBar = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSStatusBar"), ObjCRuntime.Sel("systemStatusBar"));
        _statusItem = ObjCRuntime.SendIdDouble(
            statusBar, ObjCRuntime.Sel("statusItemWithLength:"), VariableStatusItemLength);
        if (_statusItem == 0)
        {
            throw new InvalidOperationException("创建状态项失败（NSStatusBar.statusItemWithLength: 返回 nil）。");
        }

        // **必须 retain。** `statusItemWithLength:` 不符合 alloc/new/copy 命名，返回的是
        // **autoreleased** 对象：不 retain 的话，本对象只是"借"了它一个 runloop 周期。
        //
        // 症状是**跨 IPC 才炸**：`--selftest tray` 在同一个调用栈里创建并使用，pool 还没排空，
        // 一切正常；而页面调 `tray.new` 之后，WebKit 处理下一条消息前会排空 autorelease pool，
        // `_statusItem`（以及它的 `button`）就被释放了。此时再调 `tray.setTooltip` 等任何命令，
        // 相当于给野指针发消息 → SIGSEGV（退出码 139），日志里只有"即将调用"没有结果。
        // 这正是 tray-js 自检在 macOS 上崩在 setTooltip 的原因。
        ObjCRuntime.objc_retain(_statusItem);

        _handler = MacOSTrayHandler.Create(this);
        _button = ObjCRuntime.SendId(_statusItem, ObjCRuntime.Sel("button"));
        if (_button != 0)
        {
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTarget:"), _handler);
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setAction:"), ObjCRuntime.Sel("orielStatusItemClicked:"));
        }

        ApplyIcon(options);
        SetTooltip(options.Tooltip);
        SetTitle(options.Title);

        // 悬停事件（Enter/Move/Leave）：装跟踪区域之后 AppKit 才会派发它们。
        // owner 传本托盘的 handler —— 多托盘下每个按钮各一个，事件才不会串到别的托盘上。
        MacOSTrayHandler.InstallTrackingArea(_button, _handler);
    }

    // ---- ITrayBackend ----

    public void SetTooltip(string tooltip)
    {
        if (_button != 0)
        {
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setToolTip:"), ObjCRuntime.MakeNSString(tooltip));
        }
    }

    /// <summary>设置图标旁显示的文字（<c>NSStatusItem</c> 的 title，显示在图标右侧）。</summary>
    public void SetTitle(string? title)
    {
        if (_button == 0)
        {
            return;
        }

        // 空标题要写成空串而不是 nil：setTitle:nil 在 AppKit 里会被当"未设置"，行为随版本而变
        ObjCRuntime.SendVoidObj(
            _button, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString(title ?? string.Empty));

        // 有文字时给按钮一个固定宽度，否则文字会被图标挤掉。
        //
        // **必须用 double 重载**：`NSStatusItem.setLength:` 的参数是 `CGFloat`（浮点），
        // 而 arm64 上整数参数走 x0-x7、浮点走 d0-d7——用 SendVoidNint 把 CGFloat 塞进整数
        // 寄存器，AppKit 读到的就是垃圾值。实测后果是 **SIGSEGV**（崩溃报告：
        // EXC_BAD_ACCESS + "possible pointer authentication failure"，崩在 objc_msgSend）：
        // 状态项拿到荒谬的长度后去分配对应缓冲区，指针认证随即失败。
        // 同一个道理，上面 statusItemWithLength: 用的也是 SendIdDouble。
        ObjCRuntime.SendVoidDouble(
            _statusItem,
            ObjCRuntime.Sel("setLength:"),
            string.IsNullOrEmpty(title) ? VariableStatusItemLength : 0);
    }

    /// <summary>
    /// 把当前图标标记为模板图（<c>NSImage.setTemplate:</c>）。
    /// </summary>
    /// <remarks>
    /// 模板图由系统按菜单栏深浅色自动反色。彩色品牌图必须 false——否则颜色全丢，
    /// 只剩一个按 alpha 抠出来的剪影。
    /// </remarks>
    public void SetIconAsTemplate(bool asTemplate)
    {
        _iconAsTemplate = asTemplate;

        // 重新套用到当前按钮上的图像（若已有）
        if (_button != 0)
        {
            nint image = ObjCRuntime.SendId(_button, ObjCRuntime.Sel("image"));
            if (image != 0)
            {
                ObjCRuntime.SendVoidBool(image, ObjCRuntime.Sel("setTemplate:"), asTemplate);
            }
        }
    }

    public void SetIcon(OrielImage? icon)
    {
        if (icon is null)
        {
            ClearIcon();
            return;
        }

        ApplyImage(MacOSImageFactory.CreateNSImage(icon));
    }

    /// <summary>
    /// 按创建选项套用图标。
    /// </summary>
    /// <remarks>
    /// 统一走 <see cref="OrielTrayOptions.ResolveIcon"/>：读文件与解码在托管侧完成
    /// （PNG/ICO 都吃），三平台共用同一个解码器与同一条失败路径。
    /// </remarks>
    private void ApplyIcon(OrielTrayOptions options)
    {
        ApplyImage(options.ResolveIcon() is { } image
            ? MacOSImageFactory.CreateNSImage(image)
            : 0);
    }

    /// <summary>把 NSImage 设到状态项按钮上；传 0 表示回落占位符。</summary>
    private void ApplyImage(nint image)
    {
        if (_button == 0)
        {
            if (image != 0)
            {
                ObjCRuntime.objc_release(image);
            }

            return;
        }

        if (image != 0)
        {
            // 模板标记**按选项**设置：默认 true（单色图标跟随菜单栏反色），彩色图显式关掉
            ObjCRuntime.SendVoidBool(image, ObjCRuntime.Sel("setTemplate:"), _iconAsTemplate);

            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setImage:"), image);
            ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString(string.Empty));
            ObjCRuntime.objc_release(image);
        }
        else
        {
            ClearIcon();
        }
    }

    /// <summary>无图标时给一个可见占位：否则状态项是空白的，用户会以为程序没起来。</summary>
    private void ClearIcon()
    {
        if (_button == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setImage:"), 0);
        ObjCRuntime.SendVoidObj(_button, ObjCRuntime.Sel("setTitle:"), ObjCRuntime.MakeNSString("●"));
    }

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

            // 与构造里的 objc_retain 配对。`removeStatusItem:` 只是把它从菜单栏摘掉，
            // 不会替我们释放那次 retain。
            ObjCRuntime.objc_release(_statusItem);
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

    /// <summary>
    /// 上报一次点击事件。
    /// </summary>
    /// <remarks>
    /// macOS 的 <c>NSStatusItem</c> 按钮 action **不带按键信息**（AppKit 只告诉你"按钮被按了"），
    /// 所以这里按左键、状态 Up 上报——这是平台能给的最精确值。
    /// 需要区分左右键的应用在 macOS 上做不到（对标 Tauri 2：其 macOS 后端同样只报 Click）。
    /// </remarks>
    internal void RaiseEventReceived()
    {
        EventReceived?.Invoke(new OrielTrayEvent(
            Id, OrielTrayEventType.Click, OrielTrayMouseButton.Left, OrielTrayButtonState.Up));
    }

    /// <summary>
    /// 上报一次悬停事件（Enter/Move/Leave），坐标取自 <c>NSEvent</c> 的屏幕位置。
    /// </summary>
    /// <remarks>
    /// <c>NSEvent.locationInWindow</c> 是**窗口坐标**（原点在左下），而托盘按钮的窗口就是菜单栏，
    /// 它的坐标系与屏幕不同。这里用 <c>NSEvent</c> 的 <c>locationInWindow</c> 经
    /// <c>NSEvent.window</c> 转换——但转换要走 <c>convertPointToScreen:</c>，
    /// 那需要一个视图对象。为避免引入这条链，这里**如实不给坐标**（null）：
    /// 平台能可靠给的是"事件发生了"，坐标在 macOS 的菜单栏场景下不是必需的
    /// （页面通常只关心"指针进来了/离开了"）。
    /// </remarks>
    internal void RaiseHoverEvent(OrielTrayEventType type, nint eventPtr)
    {
        _ = eventPtr; // 坐标见 remarks：菜单栏窗口坐标系的转换成本高于收益
        EventReceived?.Invoke(new OrielTrayEvent(Id, type));
    }

    /// <summary>上报一条原始事件（事件只能在声明它的类型里触发，故由这里代发）。</summary>
    internal void RaiseRawEvent(string text) => RawEvent?.Invoke(text);

    // ---- 菜单项激活 ----

    private void Activate(OrielMenuItem item)
    {
        if (item.Role is { Length: > 0 } role)
        {
            // 托盘没有"当前窗口"：窗口级与编辑类 role 在这里没有明确目标，忽略而不是猜一个窗口
            if (!OrielMenuRoles.TryActivate(role, _app, window: null))
            {
                OrielDiagnostics.Report($"[OrielWeb] 托盘菜单的 role「{role}」在当前上下文无法执行，已忽略。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            MenuItemClicked?.Invoke(id);
        }
    }
}
