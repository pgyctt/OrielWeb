using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>原生材质/特效：属性探测与回退。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 落点是 <c>NSVisualEffectView</c>：一个插在 contentView 里、**位于 webview 之下**的
    /// 材质视图。材质编号见 <see cref="MacOSMappings.ToVisualEffectMaterial"/>（<c>NSVisualEffectMaterial</c>）。
    /// </para>
    /// <para>
    /// <b>必须把窗口与 webview 的背景放开</b>，否则材质永远看不到：这里同时做三件事——
    /// 窗口 <c>opaque = NO</c> + 背景清空、webview 的 <c>drawsBackground</c> 置 NO
    /// （<c>WKWebView</c> 上那个属性是私有的，走 KVC；Tauri/tao 用的也是这条路）。
    /// 页面自己仍需要有透明背景——本库不改页面的 CSS。
    /// </para>
    /// <para>
    /// <c>liquidGlassRegular / liquidGlassClear</c> 要 macOS 26 的 NSGlassEffectView，
    /// 本库不实现：列表里只有它们时抛 <see cref="NotSupportedException"/>。
    /// </para>
    /// </remarks>
    public void SetEffects(OrielEffects effects)
    {
        if (_nsWindow == 0 || _webview == 0)
        {
            return;
        }

        int material = -1;
        foreach (OrielEffect effect in effects.Effects)
        {
            int candidate = MacOSMappings.ToVisualEffectMaterial(effect);
            if (candidate >= 0)
            {
                material = candidate;
                break;
            }
        }

        if (material < 0)
        {
            throw new NotSupportedException(
                "macOS 没有可用的材质：支持 sidebar / headerView / sheet / windowBackground / "
                + "hudWindow / fullScreenUI / tooltip / contentBackground / underWindowBackground / "
                + "underPageBackground / titlebar / selection / menu / popover / appearanceBased / "
                + "light / dark / mediumLight / ultraDark；"
                + "liquidGlassRegular / liquidGlassClear 需要 macOS 26 的 NSGlassEffectView，本库未实现"
                + "（mica / acrylic / blur / tabbed 是 Windows 材质）。");
        }

        RemoveEffectView();

        // 1) 窗口与 webview 放开背景：不透明的话材质被挡在下面，什么都看不到
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setOpaque:"), false);
        nint clear = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSColor"), ObjCRuntime.Sel("clearColor"));
        if (clear != 0)
        {
            ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setBackgroundColor:"), clear);
        }

        SetWebviewDrawsBackground(false);

        // 2) 造材质视图，插到 contentView 的最底层（webview 与 dropView 之下）
        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        (double _, double _, double width, double height) = ReadRect(contentView, "bounds");

        nint effectClass = ObjCRuntime.GetClassOrThrow("NSVisualEffectView");
        _effectView = ObjCRuntime.SendIdRect(
            ObjCRuntime.SendId(effectClass, ObjCRuntime.Sel("alloc")),
            ObjCRuntime.Sel("initWithFrame:"),
            new ObjCRuntime.NSRect(0, 0, width, height));
        ObjCRuntime.objc_retain(_effectView);

        ObjCRuntime.SendVoidNint(_effectView, ObjCRuntime.Sel("setMaterial:"), material);
        ObjCRuntime.SendVoidNint(
            _effectView,
            ObjCRuntime.Sel("setState:"),
            effects.State switch
            {
                OrielEffectState.Active => 1,   // NSVisualEffectStateActive
                OrielEffectState.Inactive => 2, // NSVisualEffectStateInactive
                _ => 0,                          // NSVisualEffectStateFollowsWindowActiveState
            });

        // NSVisualEffectBlendingModeBehindWindow = 0：材质取窗口**后面**的内容
        ObjCRuntime.SendVoidNint(_effectView, ObjCRuntime.Sel("setBlendingMode:"), 0);
        ObjCRuntime.SendVoidNint(
            _effectView,
            ObjCRuntime.Sel("setAutoresizingMask:"),
            NSViewWidthSizable | NSViewHeightSizable);

        // NSWindowBelow = -1：插在参照视图（dropView）之下；dropView 不存在时插到 contentView 底下
        ObjCRuntime.SendVoidObjNintObj(
            contentView,
            ObjCRuntime.Sel("addSubview:positioned:relativeTo:"),
            _effectView,
            -1,
            _dropView);

        // 3) 圆角（Tauri 的 radius，macOS only）：走 layer.cornerRadius
        if (effects.Radius is { } radius && radius > 0)
        {
            ObjCRuntime.SendVoidBool(_effectView, ObjCRuntime.Sel("setWantsLayer:"), true);
            nint layer = ObjCRuntime.SendId(_effectView, ObjCRuntime.Sel("layer"));
            if (layer != 0)
            {
                ObjCRuntime.SendVoidDouble(layer, ObjCRuntime.Sel("setCornerRadius:"), radius);
                ObjCRuntime.SendVoidBool(layer, ObjCRuntime.Sel("setMasksToBounds:"), true);
            }
        }

        if (effects.Interactive)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] setEffects：interactive 需要 macOS 26 的液态玻璃，本库未实现，已忽略。");
        }
    }

    /// <inheritdoc />
    public void ClearEffects()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        RemoveEffectView();

        // 还原窗口与 webview 的背景：材质没了之后，不透明窗口比"漏底"更接近默认观感。
        // **注意不能只调 ApplyBackgroundColor**：它在"没设过底色"时直接返回，
        // 而 setEffects 刚刚把窗口底色改成了 clear——那样窗口会一直透明下去
        // （表现为"清掉材质后整个窗口是黑的/透的"）。所以没显式底色时要显式还原成
        // 系统的窗口底色。
        ObjCRuntime.SendVoidBool(_nsWindow, ObjCRuntime.Sel("setOpaque:"), true);
        _runtimeBackgroundArgb = null;
        if (_options.TryGetBackgroundArgb(out _, out _, out _, out _))
        {
            ApplyBackgroundColor();
        }
        else
        {
            RestoreSystemBackgroundColor();
        }

        SetWebviewDrawsBackground(true);
    }

    /// <summary>把窗口与 webview 的底色还原成系统的窗口底色（<c>NSColor.windowBackgroundColor</c>）。</summary>
    private void RestoreSystemBackgroundColor()
    {
        nint color = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSColor"), ObjCRuntime.Sel("windowBackgroundColor"));
        if (color == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setBackgroundColor:"), color);

        if (_webview != 0
            && ObjCRuntime.SendBoolRetObj(
                _webview,
                ObjCRuntime.Sel("respondsToSelector:"),
                ObjCRuntime.Sel("setUnderPageBackgroundColor:")))
        {
            ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setUnderPageBackgroundColor:"), color);
        }
    }

    /// <summary>把材质视图摘掉并释放（幂等）。</summary>
    private void RemoveEffectView()
    {
        if (_effectView == 0)
        {
            return;
        }

        ObjCRuntime.SendVoid(_effectView, ObjCRuntime.Sel("removeFromSuperview"));
        ObjCRuntime.objc_release(_effectView);
        _effectView = 0;
    }
}
