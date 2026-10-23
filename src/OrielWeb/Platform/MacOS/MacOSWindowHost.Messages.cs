using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>原生消息/回调分发与销毁流程。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    // ------------------------------------------------------------------
    // 生命周期回调（由 trampoline 调用，UI 线程）
    // ------------------------------------------------------------------

    /// <summary>
    /// Cocoa <c>windowShouldClose:</c>：返回 true 表示**允许**关闭。
    /// </summary>
    /// <remarks>
    /// 与 Linux 的 <c>delete-event</c> 相反（那边 true = 阻止），是本平台内的翻译点。
    /// </remarks>
    internal bool OnWindowShouldClose() => DecideClose() == OrielCloseDecision.Proceed;

    /// <summary>
    /// 判定一次关闭请求（宿主否决 → 页面接管 → 放行）。
    /// </summary>
    /// <remarks>
    /// "页面可决定"要求页面已开启接管**且已加载完成**：那条 <c>close-requested</c>
    /// 送不到未加载的页面，挂起只会让窗口变成点 X 没反应。
    /// </remarks>
    internal OrielCloseDecision DecideClose()
    {
        // 强制销毁途中不再问任何人：Destroy() 之后的任何关闭请求都直接放行，
        // 否则"页面接管"会把强制销毁也拦下来（那与 Destroy 的语义直接冲突）。
        if (_forceDestroy)
        {
            return OrielCloseDecision.Proceed;
        }

        return OrielClosePipeline.Decide(
            Closing,
            pageCanDecide: CloseInterceptEnabled && _pageReady,
            emitCloseRequested: () => PushEventOnUi(OrielClosePipeline.CloseRequestedEventName, "null"));
    }

    internal void OnWindowWillClose()
    {
        // 光标状态必须在这里恢复：grab 是**进程级**的（不清会让整个应用的光标不再跟随鼠标，
        // 而应用可能还在跑），隐藏与穿透也会残留到别的窗口上。
        if (_cursorGrabbed)
        {
            _cursorGrabbed = false;
            _ = CoreGraphicsNative.CGAssociateMouseAndMouseCursorPosition(1);
        }

        if (!_cursorVisible)
        {
            _cursorVisible = true;
            ObjCRuntime.SendVoid(ObjCRuntime.GetClassOrThrow("NSCursor"), ObjCRuntime.Sel("unhide"));
        }

        if (_ignoreCursorEvents && _nsWindow != 0)
        {
            _ignoreCursorEvents = false;
            ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setIgnoresMouseEvents:"), false);
        }

        // 先清理状态注册表与委托指针，再触发 Closed——避免用户在回调里发起 IPC 时
        // 走到已销毁的宿主，也避免实例指针被复用时把事件路由到旧宿主（ABA）。
        // 注册表持有的是托管 host 的强引用，不清理则整棵对象树永不释放。
        MacOSObjCClasses.RemoveWindowDelegate(_nsWindowDelegate);
        MacOSObjCClasses.RemoveNavigationDelegate(_navigationDelegate);
        MacOSObjCClasses.RemoveScriptHandler(_scriptHandler);
        MacOSObjCClasses.RemoveDropView(_dropView);
        MacOSObjCClasses.RemoveUIDelegate(_uiDelegate);
        MacOSObjCClasses.RemoveAssetSchemeHandler(_assetSchemeHandler);
        _assetSchemeHandler = 0;
        _nsWindowDelegate = 0;
        _navigationDelegate = 0;
        _scriptHandler = 0;
        _dropView = 0;
        _uiDelegate = 0;

        // 保留的上下文菜单一并释放：MacOSMenu.s_handlers 的条目与 ObjC handler 实例
        // 不释放会滞留到进程结束（注册表同样被 GCHandle 根住）。
        _contextMenu?.Dispose();
        _contextMenu = null;

        // 页面回环随文档一起消失：挂起的 ExecuteScriptAsync 在这里立刻失败，
        // 而不是让调用方的 await 永不返回。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：窗口已销毁，页面回环不会再返回结果。");

        Closed?.Invoke();
        _backend.OnWindowDestroyed();
    }
}
