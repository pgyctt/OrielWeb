using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 应用元信息（F4a）：<see cref="OrielAppInfo"/> 的推断/校验与 <c>app.*</c> 只读命令。
/// </summary>
/// <remarks>
/// <para>
/// 这里钉两类事：
/// </para>
/// <list type="number">
///   <item><description><b>推断的退化顺序</b>——各项独立退化，缺一项不影响其余项；</description></item>
///   <item><description><b>标识符的校验</b>——它将成为数据目录的组成部分，所以字符集必须卡死，
///     而 <c>"."</c> / <c>".."</c> 这类"能过字符集但作为目录名指向别处"的输入要单独挡住。</description></item>
/// </list>
/// <para>
/// 第二条是这组测试的主要价值：校验放在 <c>Build()</c> 期间，而"启动后第一次写配置才发现标识符非法"
/// 的故障点离原因很远。
/// </para>
/// </remarks>
public sealed class AppInfoTests
{
    private static OrielAppInfo Info(string name, string version, string identifier)
        => new() { Name = name, Version = version, Identifier = identifier };

    // ---- 校验 ----

    [Fact]
    public void ValidInfoPassesValidation()
        => Info("我的应用", "1.2.3", "com.example.myapp").Validate();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNameIsRejected(string name)
        => Assert.Throws<ArgumentException>(() => Info(name, "1.0.0", "app").Validate());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyVersionIsRejected(string version)
        => Assert.Throws<ArgumentException>(() => Info("app", version, "app").Validate());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIdentifierIsRejected(string identifier)
        => Assert.Throws<ArgumentException>(() => Info("app", "1.0.0", identifier).Validate());

    [Fact]
    public void NameAllowsAnyCharacters()
    {
        // 名称是给人看的：空格、中文、符号都可能合理出现，不做字符集限制
        Info("我的 应用 (工业版) — v2", "1.0.0", "com.example.app").Validate();
    }

    [Theory]
    [InlineData("com.example.app")]
    [InlineData("com-example-app")]
    [InlineData("com_example_app")]
    [InlineData("MyApp")]
    [InlineData("a1")]
    public void IdentifierAllowsLettersDigitsAndSeparators(string identifier)
        => Info("app", "1.0.0", identifier).Validate();

    [Theory]
    [InlineData("com/example")]
    [InlineData("com\\example")]
    [InlineData("com example")]
    [InlineData("com:example")]
    [InlineData("应用")]
    [InlineData("com.example*")]
    public void IdentifierRejectsIllegalCharacters(string identifier)
    {
        // 路径分隔符尤其重要：它会让"应用数据目录"跳到别的地方去，
        // 而那是从一个配置里读出来的值，不该有这种能力。
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Info("app", "1.0.0", identifier).Validate());

        Assert.Contains("非法字符", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("-")]
    [InlineData("..-..")]
    public void IdentifierOfOnlyDotsOrDashesIsRejected(string identifier)
    {
        // 这些能通过字符集检查，但作为目录名分别指"当前目录""上级目录"——
        // 是最糟糕的一类输入（写入会落到预期之外的位置）。
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Info("app", "1.0.0", identifier).Validate());

        Assert.Contains("当前/上级目录", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationErrorMessageNamesTheOffendingCharacter()
    {
        // 报错要指出**是哪个字符**：只说"标识符非法"会让人逐个字符去看
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Info("app", "1.0.0", "com/example").Validate());

        Assert.Contains("/", ex.Message, StringComparison.Ordinal);
    }

    // ---- 推断 ----

    [Fact]
    public void DetectGivesNonEmptyValues()
    {
        OrielAppInfo detected = OrielAppInfo.Detect();

        Assert.False(string.IsNullOrWhiteSpace(detected.Name));
        Assert.False(string.IsNullOrWhiteSpace(detected.Version));
        Assert.False(string.IsNullOrWhiteSpace(detected.Identifier));

        // 推断出来的标识符必须能过校验——否则"用默认值的最小应用"会在启动时炸
        detected.Validate();
    }

    [Fact]
    public void DetectIdentifierIsLowercaseAndDirectorySafe()
    {
        string identifier = OrielAppInfo.Detect().Identifier;

        // 归一成小写：Windows/macOS 上目录名大小写不敏感、Linux 上敏感，
        // 而"同一个应用在两台机器上写出两个目录"（MyApp / myapp）是很难查的一类问题
        Assert.Equal(identifier.ToLowerInvariant(), identifier);

        foreach (char c in identifier)
        {
            Assert.True(
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.',
                $"推断出的标识符含非法字符：{c}");
        }
    }

    // ---- 库版本 ----

    [Fact]
    public void LibraryVersionIsThreePartNumeric()
    {
        string version = OrielLibrary.Version;

        // 形如 0.4.0：页面拿它做能力探测，所以形状必须稳定可比较
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    [Fact]
    public void LibraryVersionMatchesTheAssemblyVersion()
    {
        // 单一来源：版本号只能来自程序集（最终是 csproj 的 <Version>），
        // 不是任何地方的字面量。写死会与包版本各自漂移，静默误导兼容判断。
        Version? assemblyVersion = typeof(OrielLibrary).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);

        Assert.Equal(
            $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}",
            OrielLibrary.Version);
    }

    // ---- app.* 命令的注册与放行 ----

    [Theory]
    [InlineData("app.getName")]
    [InlineData("app.getVersion")]
    [InlineData("app.getIdentifier")]
    [InlineData("app.getTauriVersion")]
    [InlineData("app.supportsMultipleWindows")]
    public void AppCommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInAppCommands.TryGet(name, out _), $"未注册：{name}");

    [Fact]
    public void UnknownAppCommandIsNotHandled()
        => Assert.False(OrielBuiltInAppCommands.TryGet("app.nonexistent", out _));

    [Theory]
    [InlineData("app.getName")]
    [InlineData("app.getVersion")]
    [InlineData("app.getIdentifier")]
    [InlineData("app.getTauriVersion")]
    [InlineData("app.supportsMultipleWindows")]
    public void AppMetadataCommandsAreAlwaysAllowed(string name)
        => Assert.True(OrielCapabilityRules.IsAlwaysAllowedAppCommand(name));

    [Theory]
    [InlineData("app.exit")]
    [InlineData("app.show")]
    [InlineData("app.hide")]
    [InlineData("app.setTheme")]
    [InlineData("app.getBundleType")]
    [InlineData("apps.getName")]
    [InlineData("app.")]
    public void StatefulAppCommandsAreNotAlwaysAllowed(string name)
    {
        // 这条是**门禁姿态的核心断言**：放行的是**列举出来的那几条只读元信息**，
        // 不是 app.* 前缀。将来加 exit/show/hide 时它们不在名单里，会照旧走能力校验。
        //
        // 若哪天有人图省事改成"前缀匹配"，这条会立刻变红——那正是它存在的理由。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(name));
    }
}
