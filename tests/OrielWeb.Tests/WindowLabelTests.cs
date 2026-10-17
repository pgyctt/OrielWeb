using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 窗口 label 的规则：默认值、字符集、大小写归一。
/// </summary>
/// <remarks>
/// <para>
/// label 是**寻址单位**（<c>GetWindow</c> / 将来的 <c>emitTo</c> / 能力配置都按它找窗口），
/// 所以它的校验规则一旦松掉，症状是"事件发不到"或"配置匹配不上"——两者都不报错。
/// </para>
/// <para>
/// 唯一性判定在 <see cref="OrielApp"/> 里（需要应用实例才知道已有哪些 label），
/// 那部分由 <c>OrielAppTests</c> 覆盖；这里只钉住"单个 label 长什么样"。
/// </para>
/// </remarks>
public sealed class WindowLabelTests
{
    [Fact]
    public void Label_DefaultsToMain()
    {
        Assert.Equal("main", new OrielWindowOptions().Label);
        Assert.Equal("main", OrielWindowOptions.DefaultLabel);
    }

    [Fact]
    public void WithLabel_SetsAndReturnsSameInstanceForChaining()
    {
        var options = new OrielWindowOptions();

        Assert.Same(options, options.WithLabel("settings"));
        Assert.Equal("settings", options.Label);
    }

    [Fact]
    public void Label_IsNormalizedToLowerCase()
    {
        // 归一成小写：三平台的文件系统与配置比对大小写规则不同（Windows 不敏感、Linux 敏感），
        // 不归一的话 "Main" 与 "main" 会被当成两个窗口，且只在某个平台上出错。
        Assert.Equal("main", new OrielWindowOptions().WithLabel("Main").Label);
        Assert.Equal("settings", new OrielWindowOptions().WithLabel("SETTINGS").Label);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("settings")]
    [InlineData("win-2")]
    [InlineData("panel.1")]
    [InlineData("a_b")]
    [InlineData("123")]
    public void Label_AcceptsAllowedCharacters(string label)
    {
        Assert.Equal(label, new OrielWindowOptions().WithLabel(label).Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Label_RejectsEmpty(string? label)
    {
        var options = new OrielWindowOptions();

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Label = label!);
        Assert.Contains("不能为空", error.Message);
    }

    [Theory]
    [InlineData("my window")]   // 空格
    [InlineData("窗口")]         // 非 ASCII
    [InlineData("a/b")]         // 路径分隔符
    [InlineData("a\\b")]        // 反斜杠
    [InlineData("a:b")]         // 会与将来的 "label:command" 形态混淆
    [InlineData("a*b")]         // 通配符：能力模式里 "*" 有特殊含义
    [InlineData("a\"b")]        // 引号：注入脚本里的安全边界
    public void Label_RejectsIllegalCharacters(string label)
    {
        var options = new OrielWindowOptions();

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Label = label);
        Assert.Contains("非法字符", error.Message);
    }

    [Fact]
    public void Label_IllegalCharacterMessage_NamesTheOffendingCharacter()
    {
        // 只说"非法"会让调用方去猜；说清是哪个字符才能直接改。
        var options = new OrielWindowOptions();

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Label = "my window");
        Assert.Contains("「 」", error.Message);
    }

    [Fact]
    public void ValidateLabel_IsUsableWithoutAnOptionsInstance()
    {
        // OrielApp.GetWindow 与 EnsureLabelAvailable 都用它做归一，所以它必须是 internal static 的。
        Assert.Equal("main", OrielWindowOptions.ValidateLabel("MAIN"));
        Assert.Throws<ArgumentException>(() => OrielWindowOptions.ValidateLabel("bad label"));
    }

    // ---- 唯一性（OrielApp.EnsureLabelAvailable 的纯函数形态）----
    //
    // 抽成静态纯函数才能在这里测：真正调用它的 AttachWindow 需要真实窗口后端，
    // 而"重复 label 会不会被发现"这件事恰恰不能只在真机上验。

    [Fact]
    public void EnsureLabelAvailable_EmptySet_Allows()
    {
        Assert.Null(Record.Exception(() => OrielApp.EnsureLabelAvailable("main", [])));
    }

    [Fact]
    public void EnsureLabelAvailable_Duplicate_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => OrielApp.EnsureLabelAvailable("main", ["main"]));

        // 文案必须说清"为什么不能重复"和"怎么改"，否则调用方只会看到一句拒绝
        Assert.Contains("已被占用", error.Message);
        Assert.Contains("main", error.Message);
        Assert.Contains("OrielWindowOptions.Label", error.Message);
    }

    [Fact]
    public void EnsureLabelAvailable_DifferentLabels_Allows()
    {
        Assert.Null(Record.Exception(() => OrielApp.EnsureLabelAvailable("settings", ["main"])));
    }

    [Fact]
    public void EnsureLabelAvailable_SkipsNullLabels()
    {
        // 后端尚未就位的窗口 Label 是 null（见 WebviewWindow.Label）。跳过它而不是抛，
        // 否则"两个窗口同时在建"这种时机就会误报重复。
        Assert.Null(Record.Exception(() => OrielApp.EnsureLabelAvailable("main", [null, "other"])));
    }

    [Fact]
    public void EnsureLabelAvailable_IsOrdinalCaseSensitive()
    {
        // 比较用 Ordinal：ValidateLabel 已经把 label 归一小写，所以这里两侧都是小写。
        // 若日后有人把比较改成 OrdinalIgnoreCase，"Main" 与 "main" 会被当成同一个——
        // 那与"归一小写"重复，且会掩盖归一被绕过的情况。
        Assert.Null(Record.Exception(() => OrielApp.EnsureLabelAvailable("main", ["other"])));
        Assert.Throws<InvalidOperationException>(
            () => OrielApp.EnsureLabelAvailable("main", ["main"]));
    }

    [Fact]
    public void EnsureLabelAvailable_ChecksEveryEntry()
    {
        // 命中最后一个也要抛（不能只查第一个）
        Assert.Throws<InvalidOperationException>(
            () => OrielApp.EnsureLabelAvailable("c", ["a", "b", "c"]));
    }
}
