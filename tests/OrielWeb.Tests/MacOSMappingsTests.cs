using OrielWeb.Platform.MacOS;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <see cref="MacOSMappings"/> 的表驱动测试：库枚举 → AppKit 常量/选择子。
/// </summary>
/// <remarks>
/// <para>
/// 这类 switch 的失败方式很隐蔽：把 <c>NSVisualEffectMaterial</c> 的编号写错一位，
/// 编译期毫无反应，运行期只是材质长得不对；把选择子名拼错，要到运行时发消息才崩。
/// 所以每个枚举值都钉一条用例——它们跑在 Windows 上也能验证，这正是把映射
/// 从宿主里抽出来的好处。
/// </para>
/// </remarks>
public sealed class MacOSMappingsTests
{
    // ---- NSVisualEffectMaterial ----
    //
    // 编号取自 AppKit 头文件；缺失（-1）表示本平台没有对应材质，宿主会回退。

    [Theory]
    [InlineData(OrielEffect.AppearanceBased, 0)]
    [InlineData(OrielEffect.Light, 1)]
    [InlineData(OrielEffect.Dark, 2)]
    [InlineData(OrielEffect.Titlebar, 3)]
    [InlineData(OrielEffect.Selection, 4)]
    [InlineData(OrielEffect.Menu, 5)]
    [InlineData(OrielEffect.Popover, 6)]
    [InlineData(OrielEffect.Sidebar, 7)]
    [InlineData(OrielEffect.MediumLight, 8)]
    [InlineData(OrielEffect.UltraDark, 9)]
    [InlineData(OrielEffect.HeaderView, 10)]
    [InlineData(OrielEffect.Sheet, 11)]
    [InlineData(OrielEffect.WindowBackground, 12)]
    [InlineData(OrielEffect.HudWindow, 13)]
    [InlineData(OrielEffect.FullScreenUi, 15)]   // 14 在 AppKit 里不存在，跳号是对的
    [InlineData(OrielEffect.Tooltip, 17)]        // 16 同上
    [InlineData(OrielEffect.ContentBackground, 18)]
    [InlineData(OrielEffect.UnderWindowBackground, 21)]
    [InlineData(OrielEffect.UnderPageBackground, 22)]
    public void ToVisualEffectMaterial_MapsKnownEffects(OrielEffect effect, int expected)
        => Assert.Equal(expected, MacOSMappings.ToVisualEffectMaterial(effect));

    [Theory]
    [InlineData(OrielEffect.LiquidGlassRegular)]
    [InlineData(OrielEffect.LiquidGlassClear)]
    [InlineData(OrielEffect.Mica)]
    [InlineData(OrielEffect.Blur)]
    [InlineData(OrielEffect.Acrylic)]
    [InlineData(OrielEffect.Tabbed)]
    [InlineData(OrielEffect.TabbedDark)]
    [InlineData(OrielEffect.TabbedLight)]
    public void ToVisualEffectMaterial_ReturnsMinusOneForForeignEffects(OrielEffect effect)
    {
        // 这些是 Windows 侧或不实现的材质。返回 -1（而不是 0/-1 混淆或抛异常）是契约：
        // 宿主据此判断"要不要建材质视图"。若哪天有人把默认分支改成 0，
        // 页面在 macOS 上会看到一个 AppearanceBased 空视图，而不是什么都没有。
        Assert.Equal(-1, MacOSMappings.ToVisualEffectMaterial(effect));
    }

    [Fact]
    public void ToVisualEffectMaterial_NeverMapsTwoEffectsToOneMaterial()
    {
        // 复制粘贴一个 case 再把编号改错，最典型的结果是"两个效果映射到同一个编号"。
        // 那种错误在运行时表现为"换了材质外观没变"，用户只会说"这个属性不管用"。
        var duplicates = Enum.GetValues<OrielEffect>()
            .Select(e => (Effect: e, Material: MacOSMappings.ToVisualEffectMaterial(e)))
            .Where(x => x.Material >= 0)
            .GroupBy(x => x.Material)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    // ---- 光标选择子 ----

    [Theory]
    [InlineData(OrielCursorIcon.Crosshair, "crosshairCursor")]
    [InlineData(OrielCursorIcon.Cell, "crosshairCursor")]
    [InlineData(OrielCursorIcon.Hand, "pointingHandCursor")]
    [InlineData(OrielCursorIcon.Move, "openHandCursor")]
    [InlineData(OrielCursorIcon.AllScroll, "openHandCursor")]
    [InlineData(OrielCursorIcon.Grab, "openHandCursor")]
    [InlineData(OrielCursorIcon.Grabbing, "closedHandCursor")]
    [InlineData(OrielCursorIcon.Text, "IBeamCursor")]
    [InlineData(OrielCursorIcon.VerticalText, "IBeamCursorForVerticalLayout")]
    [InlineData(OrielCursorIcon.NotAllowed, "operationNotAllowedCursor")]
    [InlineData(OrielCursorIcon.NoDrop, "operationNotAllowedCursor")]
    [InlineData(OrielCursorIcon.ContextMenu, "contextualMenuCursor")]
    [InlineData(OrielCursorIcon.Alias, "dragLinkCursor")]
    [InlineData(OrielCursorIcon.Copy, "dragCopyCursor")]
    [InlineData(OrielCursorIcon.EResize, "resizeLeftRightCursor")]
    [InlineData(OrielCursorIcon.WResize, "resizeLeftRightCursor")]
    [InlineData(OrielCursorIcon.EwResize, "resizeLeftRightCursor")]
    [InlineData(OrielCursorIcon.ColResize, "resizeLeftRightCursor")]
    [InlineData(OrielCursorIcon.NResize, "resizeUpDownCursor")]
    [InlineData(OrielCursorIcon.SResize, "resizeUpDownCursor")]
    [InlineData(OrielCursorIcon.NsResize, "resizeUpDownCursor")]
    [InlineData(OrielCursorIcon.RowResize, "resizeUpDownCursor")]
    public void CursorSelector_MapsKnownIcons(OrielCursorIcon icon, string expected)
        => Assert.Equal(expected, MacOSMappings.CursorSelector(icon));

    [Theory]
    [InlineData(OrielCursorIcon.Default)]
    [InlineData(OrielCursorIcon.Arrow)]
    [InlineData(OrielCursorIcon.Wait)]
    [InlineData(OrielCursorIcon.Progress)]
    [InlineData(OrielCursorIcon.ZoomIn)]
    [InlineData(OrielCursorIcon.ZoomOut)]
    public void CursorSelector_FallsBackToArrow(OrielCursorIcon icon)
    {
        // AppKit 没有等待/缩放类光标（那些是 CSS 概念）。给箭头是"看得见但不对"，
        // 比给 null 更好：null 会让 NSCursor 收到 nil 选择子并抛 Objective-C 异常。
        Assert.Equal("arrowCursor", MacOSMappings.CursorSelector(icon));
    }

    [Fact]
    public void CursorSelector_NeverReturnsEmpty()
    {
        // 空串会让 ObjCRuntime.Sel("") 返回 null，随后 objc_msgSend 崩在原生侧，
        // 栈里看不到 C# 帧——这是最难查的一类崩溃。
        foreach (OrielCursorIcon icon in Enum.GetValues<OrielCursorIcon>())
        {
            Assert.False(string.IsNullOrEmpty(MacOSMappings.CursorSelector(icon)), icon.ToString());
        }
    }

    // ---- 编辑类菜单 role → AppKit 选择子 ----

    [Theory]
    [InlineData(OrielMenuRole.Cut, "cut:")]
    [InlineData(OrielMenuRole.Copy, "copy:")]
    [InlineData(OrielMenuRole.Paste, "paste:")]
    [InlineData(OrielMenuRole.Undo, "undo:")]
    [InlineData(OrielMenuRole.Redo, "redo:")]
    [InlineData(OrielMenuRole.SelectAll, "selectAll:")]
    [InlineData(OrielMenuRole.Delete, "delete:")]
    public void EditingSelector_MapsEditableRoles(string role, string expected)
        => Assert.Equal(expected, MacOSMappings.EditingSelector(role));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(OrielMenuRole.Quit)]
    [InlineData(OrielMenuRole.About)]
    [InlineData("not-a-role")]
    public void EditingSelector_ReturnsNullForNonEditingRoles(string? role)
    {
        // null 是"这不是编辑动作，别走响应链"的信号；给 "" 会变成一次注定失败的原生调用。
        Assert.Null(MacOSMappings.EditingSelector(role));
    }

    [Theory]
    [InlineData(OrielMenuRole.Cut, "x")]
    [InlineData(OrielMenuRole.Copy, "c")]
    [InlineData(OrielMenuRole.Paste, "v")]
    [InlineData(OrielMenuRole.Undo, "")]
    [InlineData(OrielMenuRole.SelectAll, "")]
    [InlineData("not-a-role", "")]
    public void EditingKeyEquivalent_OnlyForClipboardRoles(string? role, string expected)
    {
        // 只给剪切/复制/粘贴编快捷键：撤销/全选在 AppKit 里用 ⌘Z / ⌘A，
        // 由系统菜单提供，自建菜单再标一次会变成两个菜单项抢同一个键。
        Assert.Equal(expected, MacOSMappings.EditingKeyEquivalent(role));
    }
}
