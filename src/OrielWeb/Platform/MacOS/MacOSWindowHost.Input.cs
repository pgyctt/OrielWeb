using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>输入面：光标、鼠标、拖放、右键菜单、拖动窗口。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    /// <summary>渲染引擎内建右键菜单的策略；见 <see cref="OrielContextMenuPolicy"/>。</summary>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>
    /// 内建右键菜单即将弹出（由 <c>OrielUIDelegate</c> 的 <c>willOpenMenu:</c> 调进来）。
    /// </summary>
    /// <remarks>
    /// <c>willOpenMenu:</c> 没有"拒绝弹出"的返回值，所以接管的方式是**换掉菜单内容**：
    /// 清空 WebKit 的项，换上只含剪辑命令的项。不再就地增删 WebKit 的项——那类做法依赖渲染引擎
    /// 的内部结构（Linux 侧同源的写法在 WebKitGTK 4.1 上会破坏内存），而自建菜单这条路本来就有。
    /// </remarks>
    internal void FilterContextMenu(nint menu)
    {
        if (menu == 0)
        {
            return;
        }

        switch (ContextMenuPolicy)
        {
            case OrielContextMenuPolicy.Native:
                return; // 平台原样

            case OrielContextMenuPolicy.Disabled:
                ObjCRuntime.SendVoid(menu, ObjCRuntime.Sel("removeAllItems"));
                return;

            default:
                ObjCRuntime.SendVoid(menu, ObjCRuntime.Sel("removeAllItems"));
                AppendEditingItems(menu);
                return;
        }
    }

    /// <summary>
    /// 往菜单里追加剪切 / 复制 / 粘贴。
    /// </summary>
    /// <remarks>
    /// action 用 AppKit 的标准 selector、target 留空，选择时由响应链交给 <c>WKWebView</c> 执行，
    /// 因此真正作用于页面选区与系统剪贴板；顺带白拿 Cmd+X/C/V 的快捷键。
    /// </remarks>
    private static void AppendEditingItems(nint menu)
    {
        foreach (OrielMenuItem item in OrielMenuRoles.EditingMenuItems())
        {
            string? selectorName = MacOSMappings.EditingSelector(item.Role);
            if (selectorName is null)
            {
                continue;
            }

            nint menuItem = ObjCRuntime.SendIdObjObjObj(
                ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSMenuItem"), ObjCRuntime.Sel("alloc")),
                ObjCRuntime.Sel("initWithTitle:action:keyEquivalent:"),
                ObjCRuntime.MakeNSString(OrielMenuRoles.DefaultLabel(item.Role) ?? string.Empty),
                ObjCRuntime.Sel(selectorName),
                ObjCRuntime.MakeNSString(MacOSMappings.EditingKeyEquivalent(item.Role)));

            if (menuItem == 0)
            {
                continue;
            }

            // NSEventModifierFlagCommand = 1 << 20
            ObjCRuntime.SendVoidNint(menuItem, ObjCRuntime.Sel("setKeyEquivalentModifierMask:"), 1 << 20);
            ObjCRuntime.SendVoidObj(menu, ObjCRuntime.Sel("addItem:"), menuItem);
        }
    }

    /// <summary>
    /// 把编辑类 role 经 AppKit 的响应链交给 <c>WKWebView</c> 执行。
    /// </summary>
    /// <remarks>
    /// 自建菜单里的编辑项走这里（<c>document.execCommand('paste')</c> 会被安全策略拦下）。
    /// target 传 nil 是有意的：<c>sendAction:to:from:</c> 会从当前第一响应者沿响应链找实现，
    /// 编辑时第一响应者正是 webview 内部的编辑视图；找不到时返回 false，不会崩。
    /// </remarks>
    private bool TryExecuteNativeEditing(string role)
    {
        string? selectorName = MacOSMappings.EditingSelector(role);
        if (selectorName is null)
        {
            return false;
        }

        nint app = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));

        return ObjCRuntime.SendBoolRetObjObjObj(
            app, ObjCRuntime.Sel("sendAction:to:from:"), ObjCRuntime.Sel(selectorName), 0, 0);
    }

    /// <inheritdoc />
    public void SetCursorGrab(bool grab)
    {
        if (grab == _cursorGrabbed)
        {
            return;
        }

        _cursorGrabbed = grab;

        // connected: 0 = 断开（光标锁死），非 0 = 恢复。
        // 这是**进程级**状态，所以窗口销毁时必须在 OnWindowWillClose 里恢复。
        _ = CoreGraphicsNative.CGAssociateMouseAndMouseCursorPosition(grab ? 0 : 1);
    }

    /// <inheritdoc />
    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible)
        {
            return;
        }

        _cursorVisible = visible;

        // NSCursor 的 hide/unhide 也是**全局计数**：多调一次 hide 就要多调一次 unhide
        nint cursorClass = ObjCRuntime.GetClassOrThrow("NSCursor");
        ObjCRuntime.SendVoid(cursorClass, ObjCRuntime.Sel(visible ? "unhide" : "hide"));
    }

    /// <inheritdoc />
    public void SetCursorIcon(OrielCursorIcon icon)
    {
        _cursorIcon = icon;

        nint cursor = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSCursor"),
            ObjCRuntime.Sel(MacOSMappings.CursorSelector(icon)));

        if (cursor != 0)
        {
            ObjCRuntime.SendVoid(cursor, ObjCRuntime.Sel("set"));
        }
    }

    /// <inheritdoc />
    public void SetCursorPosition(double x, double y)
    {
        if (_nsWindow == 0)
        {
            return;
        }

        // CG 的全局坐标是**点**（逻辑像素），原点在主屏左上角——与 InnerPosition 同口径，
        // 所以直接相加，不需要乘缩放比
        OrielPosition inner = InnerPosition;
        _ = CoreGraphicsNative.CGWarpMouseCursorPosition(new CGPoint(inner.X + x, inner.Y + y));
    }

    /// <inheritdoc />
    public void SetIgnoreCursorEvents(bool ignore)
    {
        if (_nsWindow == 0 || ignore == _ignoreCursorEvents)
        {
            return;
        }

        _ignoreCursorEvents = ignore;
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), ignore);
    }

    /// <inheritdoc />
    public void StartResizeDragging(OrielResizeDirection direction)
        => throw new NotSupportedException(
            "macOS 不支持 startResizeDragging：AppKit 没有公开的" +
            "\"以编程方式开始 resize 拖动\"入口（performWindowDragWithEvent: 只能移动窗口）。" +
            "需要该能力时请用可拖动的自绘边框配合 window.dragStart 之类的做法。");

    /// <summary>
    /// 上下文菜单：构建 → 弹出。
    /// </summary>
    /// <remarks>
    /// 弹出是异步的，菜单对象因此留到下一次弹出前才释放（不能弹完即抛）。
    /// <paramref name="position"/> 为 null 时位置由当前 <c>NSEvent</c> 决定；
    /// 没有当前事件时 <see cref="MacOSMenu.Popup"/> 会明确跳过并记日志，而不是弹到随机位置。
    /// </remarks>
    public void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
    {
        _contextMenu?.Dispose();
        _contextMenu = MacOSMenu.Build(items, ActivateMenuItem);
        if (_contextMenu is not null && _webview != 0)
        {
            // 自动关闭交给 MacOSMenu：它要在**弹出之前**用
            // `performSelector:afterDelay:inModes:` 把定时器排进 NSEventTrackingRunLoopMode。
            // 在这里用 Task.Delay 不行——弹出会阻塞主线程直到菜单关掉，
            // 而投回主线程的动作注册在 default mode，等不到。
            _contextMenu.Popup(_webview, position, autoDismissAfter);
        }
    }

    /// <summary>
    /// 取消正在跟踪的菜单（<c>cancelTracking</c>）。
    /// </summary>
    /// <remarks>
    /// macOS 的弹出本来就不阻塞调用方，所以这条主要用于"页面要求关掉菜单"
    /// （例如快捷键 Esc 由页面处理时）。没有菜单在屏上是空操作。
    /// </remarks>
    public void CloseContextMenu() => _contextMenu?.CancelTracking();

    /// <summary>拖放视图解析出的本地路径（由 <c>MacOSObjCClasses</c> 的 trampoline 调用）。</summary>
    internal void OnFilesDropped(IReadOnlyList<string> paths, int x, int y)
    {
        // FileDropped 是 B8 已发布的公开事件，drop 时两者都发；DragDrop 是完整版本
        DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Drop, paths, x, y));
        FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
    }

    /// <summary>拖动进入窗口（<c>draggingEntered:</c>）。</summary>
    internal void OnDragEntered(IReadOnlyList<string> paths, int x, int y)
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Enter, paths, x, y));

    /// <summary>在窗口内移动（<c>draggingUpdated:</c>，频率可达每秒几十次）。</summary>
    internal void OnDragUpdated(int x, int y)
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Over, [], x, y));

    /// <summary>离开窗口（<c>draggingExited:</c>）。</summary>
    internal void OnDragExited()
        => DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Leave, [], 0, 0));

    public void BeginDrag()
    {
        // macOS 无边框拖动由 JS 流式坐标驱动（win.dragStart 提供起点，DragTo 应用增量）。
    }

    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
    {
        // 屏幕 CSS 点 == cocoa 点（1:1）。窗口左上角 (wx, wy) → cocoa 左下角：
        _dragPointerStart = (px, py);
        _cocoaX = wx;
        _cocoaY = sh - wy - wh;
        _dragWindowOrigin = (_cocoaX, _cocoaY);
    }

    public void DragTo(double pointerDx, double pointerDy)
    {
        if (_dragPointerStart is null || _dragWindowOrigin is null)
        {
            return;
        }
        _cocoaX = _dragWindowOrigin.Value.X + pointerDx;
        _cocoaY = _dragWindowOrigin.Value.Y - pointerDy; // 屏幕 y 向下 = cocoa y 向上
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setFrameOrigin:"), _cocoaX, _cocoaY);
    }

    public void EndDrag()
    {
        _dragPointerStart = null;
        _dragWindowOrigin = null;
    }
}
