using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 加速键解析的单测。解析器服务于菜单与托盘的加速键（窗口上下文菜单、托盘菜单），
/// 且它的行为随平台而变（<c>CmdOrCtrl</c> 的落点、展示文本），所以两个平台都覆盖。
/// </summary>
public sealed class AcceleratorTests
{
    private static OrielAccelerator Parse(string text, bool isMac = false)
    {
        Assert.True(OrielAccelerator.TryParse(text, isMac, out var accelerator), $"应能解析：{text}");
        return accelerator!;
    }

    // ---- 基本形态 ----

    [Fact]
    public void SingleModifierAndKey()
    {
        var a = Parse("Ctrl+A");
        Assert.True(a.Control);
        Assert.False(a.Alt);
        Assert.False(a.Shift);
        Assert.False(a.Command);
        Assert.Equal("A", a.Key);
    }

    [Fact]
    public void MultipleModifiersInAnyOrder()
    {
        var a = Parse("Shift+Ctrl+Alt+A");
        Assert.True(a.Control);
        Assert.True(a.Alt);
        Assert.True(a.Shift);
        // 顺序无关：两种写法必须落到同一个规范化结果（展示串是它的最直接体现）
        Assert.Equal(a.ToDisplayString(isMac: false), Parse("Ctrl+Alt+Shift+A").ToDisplayString(isMac: false));
    }

    [Fact]
    public void PublicOverload_UsesCurrentPlatform()
    {
        // 公开重载按当前平台解释 CmdOrCtrl；这条断言在两个平台上都成立
        Assert.True(OrielAccelerator.TryParse("CmdOrCtrl+A", out var a));
        Assert.Equal(!OperatingSystem.IsMacOS(), a!.Control);
        Assert.Equal(OperatingSystem.IsMacOS(), a!.Command);
    }

    [Fact]
    public void RepeatedModifierIsHarmless()
    {
        var a = Parse("Ctrl+Ctrl+Shift+A");
        Assert.True(a.Control);
        Assert.True(a.Shift);
    }

    // ---- CmdOrCtrl 的平台语义 ----

    [Fact]
    public void CmdOrCtrl_MapsToControlOffMac()
    {
        var a = Parse("CmdOrCtrl+A", isMac: false);
        Assert.True(a.Control);
        Assert.False(a.Command);
    }

    [Fact]
    public void CmdOrCtrl_MapsToCommandOnMac()
    {
        var a = Parse("CmdOrCtrl+A", isMac: true);
        Assert.True(a.Command);
        Assert.False(a.Control);
    }

    [Fact]
    public void CmdOrCtrl_IsDistinctFromExplicitCtrlOnMac()
    {
        // 显式写 Ctrl 在 macOS 上就是 Control 键，不应被折叠成 Command
        var a = Parse("Ctrl+A", isMac: true);
        Assert.True(a.Control);
        Assert.False(a.Command);
    }

    // ---- 别名与大小写 ----

    [Theory]
    [InlineData("Control+A")]
    [InlineData("CTRL+A")]
    [InlineData("ctrl+a")]
    public void ControlAliases(string text) => Assert.True(Parse(text).Control);

    [Theory]
    [InlineData("Cmd+A")]
    [InlineData("Command+A")]
    [InlineData("Super+A")]
    [InlineData("Win+A")]
    [InlineData("Meta+A")]
    public void CommandAliases(string text) => Assert.True(Parse(text).Command);

    [Theory]
    [InlineData("Option+A")]
    [InlineData("Alt+A")]
    public void AltAliases(string text) => Assert.True(Parse(text).Alt);

    // ---- 键名规范化 ----

    [Fact]
    public void LetterAndDigitKeysAreUppercased()
    {
        Assert.Equal("A", Parse("ctrl+a").Key);
        Assert.Equal("7", Parse("ctrl+7").Key);
    }

    [Fact]
    public void FunctionKeysNormalize()
    {
        Assert.Equal("F5", Parse("ctrl+f5").Key);
        Assert.Equal("F24", Parse("ctrl+F24").Key);
    }

    [Fact]
    public void NamedKeysNormalize()
    {
        Assert.Equal("PageDown", Parse("ctrl+pgdn").Key);
        Assert.Equal("PageDown", Parse("ctrl+pagedown").Key);
        Assert.Equal("Space", Parse("ctrl+space").Key);
        Assert.Equal("Escape", Parse("ctrl+esc").Key);
        Assert.Equal("Enter", Parse("ctrl+return").Key);
        Assert.Equal("Backspace", Parse("ctrl+back").Key);
    }

    // ---- 非法输入 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]           // 尾随加号
    [InlineData("+A")]              // 前置加号
    [InlineData("Ctrl++A")]         // 空段
    [InlineData("Ctrl+Shift")]      // 只有修饰键
    [InlineData("A+B")]             // 两个键名
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NotAKey")]    // 认不出的键名
    [InlineData("Ctrl+1A")]         // 既不是单字符也不是 F 键
    [InlineData("Ctrl+F25")]        // 超出功能键范围
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+-")]          // 标点必须用具名键（Minus）
    public void InvalidInputsAreRejected(string? text)
        => Assert.False(OrielAccelerator.TryParse(text, isMac: false, out _), $"不应解析成功：{text}");

    [Fact]
    public void PunctuationUsesNamedKeys()
    {
        Assert.Equal("Minus", Parse("ctrl+minus").Key);
        Assert.Equal("Comma", Parse("ctrl+comma").Key);
    }

    // ---- 裸功能键 ----

    /// <remarks>
    /// 菜单加速键允许只有功能键（如 <c>F5</c>）：它只在菜单展开时生效，不会吞掉正常输入。
    /// </remarks>
    [Fact]
    public void BareFunctionKeyIsValid()
    {
        Assert.True(OrielAccelerator.TryParse("F5", isMac: false, out var bare));
        Assert.Equal("F5", bare!.Key);
    }

    [Fact]
    public void ModifiedKeyIsValid()
        => Assert.True(OrielAccelerator.TryParse("CmdOrCtrl+Shift+P", isMac: false, out _));

    // ---- 展示文本 ----

    [Fact]
    public void DisplayOffMac_UsesNamedModifiers()
    {
        var a = Parse("CmdOrCtrl+Shift+A", isMac: false);
        Assert.Equal("Ctrl+Shift+A", a.ToDisplayString(isMac: false));
    }

    [Fact]
    public void DisplayOnMac_UsesSymbolsInFixedOrder()
    {
        var a = Parse("Ctrl+Alt+Shift+Cmd+A", isMac: true);
        Assert.Equal("⌃⌥⇧⌘A", a.ToDisplayString(isMac: true));
    }

    [Fact]
    public void DisplayOffMac_ShowsWinForCommand()
    {
        var a = Parse("Cmd+A", isMac: false);
        Assert.Equal("Win+A", a.ToDisplayString(isMac: false));
    }

    [Fact]
    public void CanonicalString_IsStableForDedup()
    {
        // 同一个组合的不同写法必须落到同一个规范化串，否则按它做的比对会漏
        Assert.Equal(
            Parse("CmdOrCtrl+Shift+A", isMac: false).ToCanonicalString(),
            Parse("ctrl+shift+a", isMac: false).ToCanonicalString());
    }
}
