using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = DirectN.IDCompositionDevice;
using DCompFunctions = DirectN.Functions;
using DCompHWND = DirectN.HWND;
using DCompPoint = DirectN.POINT;
using DCompTarget = DirectN.IDCompositionTarget;
using DCompVisual = DirectN.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>Windows 合成特效（Mica/Acrylic/Backdrop）：属性探测与回退。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    // ---- 特效 ----

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 取值映射（与 Tauri 的 Windows 侧同一组落点）：
    /// <c>mica</c> → <c>DWMSBT_MAINWINDOW</c>、<c>acrylic</c> → <c>DWMSBT_TRANSIENTWINDOW</c>、
    /// <c>tabbed/tabbedDark/tabbedLight</c> → <c>DWMSBT_TABBEDWINDOW</c>（后两个顺带切
    /// 非客户区的深浅色）。这四个走 Win11 22H2+ 的系统背板属性。
    /// <c>blur</c> / 不满足 22H2 的 <c>acrylic</c> 走 <c>SetWindowCompositionAttribute</c>
    /// 的强调色策略（未文档化，但与 Tauri/tao 同一入口）。
    /// </para>
    /// <para>
    /// <b>一项都应用不了时抛错</b>：消息里列出 Windows 支持的取值——macOS 材质
    /// （<c>sidebar</c>、<c>hudWindow</c>…）在 Windows 上没有对应物，静默成功会让
    /// "材质没出现"变成无法解释的现象。
    /// </para>
    /// </remarks>
    public void SetEffects(OrielEffects effects)
    {
        if (!WindowAlive)
        {
            return;
        }

        // EffectState 只有 macOS 有意义（见 IWindowBackend.SetEffects）
        if (effects.State != OrielEffectState.FollowsWindowActiveState)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] setEffects：state「{effects.State}」在 Windows 上被忽略——"
                + "DWM 材质本身跟随窗口激活态，没有强制激活/非激活的入口。");
        }

        int lastHr = 0;
        bool attempted = false;

        foreach (OrielEffect effect in effects.Effects)
        {
            if (TryApplyWindowsEffect(effect, effects.ColorArgb, out int hr))
            {
                return;
            }

            if (hr != 0)
            {
                attempted = true;
                lastHr = hr;
            }
        }

        if (attempted)
        {
            throw new InvalidOperationException(
                $"window.setEffects：材质应用失败（最后一次 HRESULT=0x{lastHr:X8}）。"
                + "Mica/Acrylic/Tabbed 需要 Windows 11 22H2 及以上。");
        }

        throw new NotSupportedException(
            "Windows 只支持这些材质：mica / acrylic / blur / tabbed / tabbedDark / tabbedLight。"
            + "传入的列表里没有一个是 Windows 的材质（macOS 的 vibrancy 材质在 Windows 上没有对应物）。");
    }

    /// <summary>应用一个 Windows 材质；返回是否成功。<paramref name="hr"/> 非 0 表示"是 Windows 材质但系统拒绝了"。</summary>
    private bool TryApplyWindowsEffect(OrielEffect effect, uint? colorArgb, out int hr)
    {
        hr = 0;

        switch (effect)
        {
            case OrielEffect.Mica:
                return TrySetBackdrop(Win32Constants.DWMSBT_MAINWINDOW, null, out hr);

            case OrielEffect.Acrylic:
                // Win11 22H2+ 用系统背板；更早的系统退回强调色策略（Win10 1803+）
                if (TrySetBackdrop(Win32Constants.DWMSBT_TRANSIENTWINDOW, colorArgb, out hr))
                {
                    return true;
                }

                return TrySetAccent(Win32Constants.ACCENT_ENABLE_ACRYLICBLURBEHIND, colorArgb);

            case OrielEffect.Tabbed:
            case OrielEffect.TabbedDark:
            case OrielEffect.TabbedLight:
            {
                if (effect != OrielEffect.Tabbed)
                {
                    // Tabbed 的深浅由非客户区的深浅色决定（tabbedDark 只在系统是深色时才"深"，
                    // tabbedLight 强制浅色——与 Tauri 的说明一致）
                    int dark = effect == OrielEffect.TabbedDark ? 1 : 0;
                    _ = Win32.DwmSetWindowAttribute(
                        _hwnd, Win32Constants.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                }

                return TrySetBackdrop(Win32Constants.DWMSBT_TABBEDWINDOW, null, out hr);
            }

            case OrielEffect.Blur:
                return TrySetAccent(Win32Constants.ACCENT_ENABLE_BLURBEHIND, colorArgb);

            default:
                // macOS 材质：不是"系统拒绝"，是"Windows 没有这个概念"（hr 保持 0）
                return false;
        }
    }

    /// <summary>设置 DWM 系统背板（Win11 22H2+）；失败时 <paramref name="hr"/> 带 HRESULT。</summary>
    private bool TrySetBackdrop(int backdrop, uint? colorArgb, out int hr)
    {
        int value = backdrop;
        hr = Win32.DwmSetWindowAttribute(
            _hwnd, Win32Constants.DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));

        if (hr < 0)
        {
            // 老系统（Win10 / Win11 21H2）不认这个属性
            return false;
        }

        hr = 0;

        // 背板要求窗口的客户区能透出 DWM 画的东西：窗口本身仍是不透明的，
        // 页面需要有透明区域才看得见（写进文档的已知前提）
        if (colorArgb is { } argb && backdrop == Win32Constants.DWMSBT_TRANSIENTWINDOW)
        {
            TrySetAccent(Win32Constants.ACCENT_ENABLE_ACRYLICBLURBEHIND, argb);
        }

        return true;
    }

    /// <summary>设置强调色策略（未文档化的整窗模糊）；失败返回 false。</summary>
    private unsafe bool TrySetAccent(int accentState, uint? colorArgb)
    {
        var policy = new ACCENTPOLICY
        {
            AccentState = accentState,
            AccentFlags = 0,
            GradientColor = colorArgb is { } argb ? ToAbgr(argb) : 0,
            AnimationId = 0,
        };

        var data = new WINDOWCOMPOSITIONATTRIBDATA
        {
            Attribute = Win32Constants.WCA_ACCENT_POLICY,
            Data = &policy,
            SizeOfData = Marshal.SizeOf<ACCENTPOLICY>(),
        };

        int ok = Win32.SetWindowCompositionAttribute(_hwnd, ref data);
        if (ok == 0)
        {
            OrielDiagnostics.Report("[OrielWeb] setEffects：SetWindowCompositionAttribute 失败（无 DWM 会话？）。");
        }

        return ok != 0;
    }

    /// <summary>ARGB → ABGR（强调色策略的 <c>GradientColor</c> 是 ABGR 序）。</summary>
    private static uint ToAbgr(uint argb)
    {
        uint a = (argb >> 24) & 0xFF;
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <inheritdoc />
    public void ClearEffects()
    {
        if (!WindowAlive)
        {
            return;
        }

        // 没设过也照样清：清一个不存在的状态不该失败，而"重复清"很容易发生
        // （页面切换路由时清一次、销毁前清一次）
        _ = TrySetBackdrop(Win32Constants.DWMSBT_NONE, null, out _);
        _ = TrySetAccent(Win32Constants.ACCENT_DISABLED, null);
    }
}
