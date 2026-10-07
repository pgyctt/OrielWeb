using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 安全与能力模型的单测：命令名匹配、allow/deny 判定、令牌、构建配置判定、入站门禁。
/// </summary>
/// <remarks>
/// 这些都是**安全判据**，写错的表现大多不是报错而是静默放行（一条写错的 deny 等于没写、
/// 一个过宽的 `*` 等于关掉整个模型），所以逐例钉死，不靠 review。
/// </remarks>
public sealed class CapabilityTests
{
    // ---- 命令名匹配 ----

    [Theory]
    [InlineData("todo.add", "todo.add", true)]
    [InlineData("todo.add", "todo.list", false)]
    [InlineData("todo.*", "todo.add", true)]
    [InlineData("todo.*", "todo.list", true)]
    // 末尾那个 '.' 是模式的一部分：它让 `todo.*` 不匹配 `todo` 本身
    [InlineData("todo.*", "todo", false)]
    // 前缀必须逐字符：`todo.` 不是 `todos.add` 的前缀
    [InlineData("todo.*", "todos.add", false)]
    [InlineData("*", "anything.at.all", true)]
    // Ordinal：命令名是标识符，不能按文化规则比较（否则某些区域下大小写会互相命中）
    [InlineData("TODO.add", "todo.add", false)]
    [InlineData("todo.add", "TODO.ADD", false)]
    [InlineData("", "todo.add", false)]
    [InlineData("todo.add", "", false)]
    [InlineData("todo.*", "", false)]
    public void PatternMatching(string pattern, string command, bool expected)
        => Assert.Equal(expected, OrielCapabilityMatch.Matches(pattern, command));

    [Fact]
    public void AnyMatchesIgnoresOrder()
    {
        IReadOnlyList<string> patterns = ["sys.*", "todo.add"];

        Assert.True(OrielCapabilityMatch.AnyMatches(patterns, "todo.add"));
        Assert.True(OrielCapabilityMatch.AnyMatches(patterns, "sys.info"));
        Assert.False(OrielCapabilityMatch.AnyMatches(patterns, "todo.list"));
        Assert.False(OrielCapabilityMatch.AnyMatches([], "todo.add"));
    }

    // ---- allow / deny 判定 ----

    [Fact]
    public void DenyWinsOverAllow()
    {
        // "先写宽泛的 allow、再用 deny 挖掉个别例外"是最常见的写法。
        // 若 allow 优先，这条 deny 就等于没写——而且是静默的。
        Assert.False(OrielCapabilityRules.IsAllowed("todo.remove", ["todo.*"], ["todo.remove"]));
        Assert.False(OrielCapabilityRules.IsAllowed("todo.remove", ["*"], ["todo.*"]));
    }

    [Fact]
    public void UnlistedCommandIsDenied()
    {
        // deny-by-default：不在名单上就是没有这个能力
        Assert.False(OrielCapabilityRules.IsAllowed("todo.add", ["sys.*"], []));
        Assert.False(OrielCapabilityRules.IsAllowed("todo.add", [], []));
    }

    [Fact]
    public void ListedCommandIsAllowed()
        => Assert.True(OrielCapabilityRules.IsAllowed("todo.add", ["todo.*", "sys.info"], []));

    // ---- 库保留前缀 ----

    [Theory]
    [InlineData("win.close", true)]
    [InlineData("win.dragTo", true)]
    // 前缀是 "win."，不是 "win"：不给 `window.*` 开后门
    [InlineData("window.close", false)]
    [InlineData("win", false)]
    [InlineData("", false)]
    public void BuiltInPrefixIsExact(string command, bool expected)
        => Assert.Equal(expected, OrielCapabilityRules.IsBuiltInCommand(command));

    // ---- 令牌 ----

    [Fact]
    public void GeneratedTokenIsWellFormed()
        => Assert.True(OrielIpcToken.IsWellFormed(OrielIpcToken.Generate()));

    [Fact]
    public void GeneratedTokensAreDistinct()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 32; i++)
        {
            Assert.True(seen.Add(OrielIpcToken.Generate()), "生成了重复的令牌。");
        }
    }

    [Theory]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    // 长度不同直接判否（FixedTimeEquals 要求等长，这里先挡掉）
    [InlineData("abc", "abcd", false)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abc", true)]
    public void TokenComparison(string? expected, string? presented, bool expectedMatch)
        => Assert.Equal(expectedMatch, OrielIpcToken.Equals(expected, presented));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    // 长度对但不是十六进制
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    // 大写十六进制不算：生成侧固定小写，接受大写只会放宽比较
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    public void TokenShape(string? token, bool expected)
        => Assert.Equal(expected, OrielIpcToken.IsWellFormed(token));

    // ---- 构建配置判定 ----

    [Theory]
    [InlineData("Debug", true)]
    [InlineData("debug", true)]
    [InlineData("Release", false)]
    [InlineData("release", false)]
    // 认不出来的一律 null → 交给回退路径，不当成任何一边
    [InlineData("Shipping", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseMetadataValue(string? value, bool? expected)
        => Assert.Equal(expected, OrielBuildConfiguration.ParseMetadataValue(value));

    [Fact]
    public void MetadataWinsOverJitFallback()
    {
        // 元数据在就用它（即便与 JIT 开关矛盾），认不出来才回退
        Assert.True(OrielBuildConfiguration.Resolve(metadataSaysDebug: true, jitOptimizerDisabled: false));
        Assert.False(OrielBuildConfiguration.Resolve(metadataSaysDebug: false, jitOptimizerDisabled: true));
        Assert.True(OrielBuildConfiguration.Resolve(metadataSaysDebug: null, jitOptimizerDisabled: true));
        Assert.False(OrielBuildConfiguration.Resolve(metadataSaysDebug: null, jitOptimizerDisabled: false));
    }

    [Fact]
    public void LibraryAssemblyHasNoBuildMetadata()
    {
        // 那份元数据由包内 targets 注入到**消费方**程序集；库自己是 ProjectReference 引的，
        // 因此这里必须取不到——也正因为取不到，才需要 DebuggableAttribute 这条回退路径。
        Assert.False(OrielBuildConfiguration.TryReadMetadata(typeof(OrielIpcGuard).Assembly, out _));
        Assert.False(OrielBuildConfiguration.TryReadMetadata(null, out _));
    }

    // ---- 入站门禁 ----

    [Fact]
    public void GuardGeneratesWellFormedToken()
        => Assert.True(OrielIpcToken.IsWellFormed(Guard().Token));

    [Fact]
    public void TrustedPrefixIsRecorded()
    {
        // 内嵌资源的来源（自定义 scheme oriel://）自动可信（默认 host 是 app.oriel）
        Assert.True(Guard().IsTrustedUrl("oriel://app.oriel/index.html"));
        Assert.True(Guard().IsTrustedUrl("oriel://app.oriel/"));
        // 兼容别名（0.2.0 及以前的写法）只影响"入参怎么解析"，页面真实来源仍是 oriel://，
        // 所以 https://app.oriel/ 不该被算成可信来源——它谁也服务不了。
        Assert.False(Guard().IsTrustedUrl("https://app.oriel/index.html"));
    }

    [Theory]
    // 前缀带斜杠，所以"把 host 拼进自己域名里"这种样子不会被误放行
    [InlineData("oriel://app.oriel.evil.com/index.html")]
    // 别的来源要显式放行才有（开发期连 dev server）
    [InlineData("http://localhost:5173/index.html")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData(null)]
    public void UntrustedOriginsAreRejected(string? url)
        => Assert.False(Guard().IsTrustedUrl(url));

    [Fact]
    public void AllowedOriginIsAppended()
    {
        var options = new OrielCapabilityOptions().AllowOrigin("http://localhost:5173/");
        OrielIpcGuard guard = Guard(options);

        Assert.True(guard.IsTrustedUrl("http://localhost:5173/"));
        Assert.True(guard.IsTrustedUrl("http://localhost:5173/index.html"));
    }

    [Fact]
    public void AllowedOriginIsNormalizedWithTrailingSlash()
    {
        // 缺尾斜杠是最常见的笔误：不归一的话 "http://localhost:5173" 会逐字节命中
        // "http://localhost:5173.evil.com/"——形似域名即可白拿整套桥接与令牌。
        var options = new OrielCapabilityOptions().AllowOrigin("http://localhost:5173");
        OrielIpcGuard guard = Guard(options);

        Assert.True(guard.IsTrustedUrl("http://localhost:5173/index.html"));
        Assert.False(guard.IsTrustedUrl("http://localhost:5173.evil.com/index.html"));
    }

    [Fact]
    public void MessageOriginTrustFollowsAuthority()
    {
        // macOS 的逐消息来源是 origin（无路径）：拼上 "/" 后与可信前缀同一条判定——
        // authority 命中即可信，形似域名与陌生来源不命中；null（来源取不到）一律不可信。
        OrielIpcGuard guard = Guard();

        Assert.True(guard.IsTrustedOrigin("oriel://app.oriel"));
        Assert.False(guard.IsTrustedOrigin("oriel://app.oriel.evil.com"));
        Assert.False(guard.IsTrustedOrigin("http://localhost:5173"));
        Assert.False(guard.IsTrustedOrigin(null));

        var options = new OrielCapabilityOptions().AllowOrigin("http://localhost:5173/");
        Assert.True(Guard(options).IsTrustedOrigin("http://localhost:5173"));
    }

    [Fact]
    public void AllowedOriginSubPathIsNormalizedToo()
    {
        // 子路径前缀同样补尾斜杠："…/app" 不该命中 "…/app.evil.com/"。
        var options = new OrielCapabilityOptions().AllowOrigin("http://localhost:5173/app");
        OrielIpcGuard guard = Guard(options);

        Assert.True(guard.IsTrustedUrl("http://localhost:5173/app/index.html"));
        Assert.False(guard.IsTrustedUrl("http://localhost:5173/app.evil.com/"));
    }

    [Theory]
    // 没有 scheme：Uri 会把它读成 scheme=localhost、没有 host——正是必须拒绝的笔误
    [InlineData("localhost:5173")]
    // 带 query 的"前缀"匹配不到任何真实页面 URL，留着只会让人以为配了却没生效
    [InlineData("http://localhost:5173?x=1")]
    [InlineData("http://localhost:5173/#top")]
    [InlineData("not a url")]
    public void AllowedOriginRejectsUnusablePrefixes(string prefix)
        => Assert.Throws<ArgumentException>(() => new OrielCapabilityOptions().AllowOrigin(prefix));

    [Fact]
    public void AddTrustedPrefixAppendsAnotherOrigin()
    {
        // 显式追加的来源（开发期的 dev server 等）与内嵌资源走同一条前缀判定。
        OrielIpcGuard guard = Guard();
        Assert.False(guard.IsTrustedUrl("http://localhost:5173/index.html"));

        guard.AddTrustedPrefix("http://localhost:5173/");

        Assert.True(guard.IsTrustedUrl("http://localhost:5173/index.html"));
        Assert.False(guard.IsTrustedUrl("http://localhost:5173-evil/index.html"));
    }

    [Fact]
    public void AcceptRequiresTrustedOrigin()
    {
        OrielIpcGuard guard = Guard();
        using var message = Message(guard.Token);

        Assert.True(guard.TryAccept(TestHarness.TrustedUrl, message.RootElement, out string? rejection));
        Assert.Null(rejection);

        Assert.False(guard.TryAccept("https://evil.example/index.html", message.RootElement, out rejection));
        Assert.Contains("来源", rejection);
    }

    [Fact]
    public void AcceptRequiresMatchingToken()
    {
        OrielIpcGuard guard = Guard();

        // 令牌缺失（不是本应用注入的脚本发的消息）
        using var missing = JsonDocument.Parse("""{ "__oriel": "invoke" }""");
        Assert.False(guard.TryAccept(TestHarness.TrustedUrl, missing.RootElement, out string? rejection));
        Assert.Contains("令牌", rejection);

        // 令牌不是字符串（形状不对）同样判否，而不是当空值放过
        using var wrongShape = JsonDocument.Parse("""{ "token": 12345 }""");
        Assert.False(guard.TryAccept(TestHarness.TrustedUrl, wrongShape.RootElement, out _));

        // 别的进程的令牌
        using var foreign = Message(OrielIpcToken.Generate());
        Assert.False(guard.TryAccept(TestHarness.TrustedUrl, foreign.RootElement, out _));
    }

    // ---- 命令授权 ----

    [Fact]
    public void BuiltInCommandsAreAlwaysAuthorized()
    {
        // Release + 未配置能力：win.* 仍须放行，否则无边框窗口连关闭按钮都失效
        OrielIpcGuard guard = TestGuards.Unconfigured(isDebugBuild: false);

        Assert.True(guard.TryAuthorize("win.close", out string? reason));
        Assert.Null(reason);
        Assert.True(guard.TryAuthorize("win.dragTo", out _));
    }

    [Fact]
    public void UnconfiguredIsOpenInDebugAndClosedInRelease()
    {
        Assert.True(TestGuards.Unconfigured(isDebugBuild: true).TryAuthorize("todo.add", out _));

        Assert.False(TestGuards.Unconfigured(isDebugBuild: false).TryAuthorize("todo.add", out string? reason));
        Assert.Contains("Release", reason);
        Assert.Contains("todo.add", reason);
    }

    [Fact]
    public void ConfiguredGuardUsesTheAllowListInBothConfigurations()
    {
        foreach (bool isDebugBuild in new[] { true, false })
        {
            var options = new OrielCapabilityOptions().Allow("todo.*");
            var guard = new OrielIpcGuard(options, isDebugBuild, "app.oriel");

            Assert.True(guard.IsConfigured);
            Assert.True(guard.TryAuthorize("todo.add", out _));
            Assert.False(guard.TryAuthorize("sys.info", out string? reason));
            Assert.Contains("sys.info", reason);
        }
    }

    [Fact]
    public void DenialReasonTellsDenyFromNotListed()
    {
        // 两种拒绝要能区分：一个说"你把它 deny 了"，一个说"你没声明过它"。
        // 混成一句话的话，"我明明加进 Allow 了却不生效"将无从下手。
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("todo.*").Deny("todo.remove"), false, "app.oriel");

        Assert.False(guard.TryAuthorize("todo.remove", out string? denied));
        Assert.Contains("Deny", denied);

        Assert.False(guard.TryAuthorize("sys.info", out string? notListed));
        Assert.Contains("Allow", notListed);
    }

    // ---- 配置对象本身 ----

    [Fact]
    public void AllowAndDenyAccumulate()
    {
        var options = new OrielCapabilityOptions()
            .Allow("todo.*")
            .Allow("sys.info")
            .Deny("todo.remove")
            .AllowOrigin("http://localhost:5173/");

        Assert.Equal(2, options.AllowedCommands.Count);
        Assert.Single(options.DeniedCommands);
        Assert.Single(options.AllowedOrigins);
    }

    [Fact]
    public void BlankPatternsAreRejected()
    {
        // 空模式不是"拒绝一切"而是"写错了"：静默放进名单只会让人以为配了却没生效
        Assert.Throws<ArgumentException>(() => new OrielCapabilityOptions().Allow(""));
        Assert.Throws<ArgumentException>(() => new OrielCapabilityOptions().Deny("   "));
        Assert.Throws<ArgumentNullException>(() => new OrielCapabilityOptions().Allow(null!));
    }

    [Fact]
    public void BlankOriginIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new OrielCapabilityOptions().AllowOrigin(" "));
        Assert.Throws<ArgumentNullException>(() => new OrielCapabilityOptions().AllowOrigin(null!));
    }

    private static OrielIpcGuard Guard(OrielCapabilityOptions? options = null)
        => new(options, isDebugBuild: false, "app.oriel");

    private static JsonDocument Message(string token)
        => JsonDocument.Parse($$"""{ "__oriel": "invoke", "token": "{{token}}" }""");
}
