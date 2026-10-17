using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 路径 scope（F4b-2）——glob 匹配、变量展开、allow/deny 判定。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试是**安全判定**的测试，所以写得比别处密：scope 上的"差一点"就是放行面差一点。
/// 三种情形都要覆盖到位——该放行的放行、该拒的拒、以及**边界上那些容易写错的**：
/// </para>
/// <list type="bullet">
///   <item><description><c>**</c> 能不能吃零段（<c>$HOME/**/*.txt</c> 应当匹配 <c>$HOME/a.txt</c>）；</description></item>
///   <item><description><c>*</c> **不能**跨分隔符（否则 <c>$APPDATA/*</c> 会变成"整个子树都能进"）；</description></item>
///   <item><description>前缀相似的目录不能被误放行（<c>/a/b</c> 的模式不能匹配 <c>/a/bc</c>）；</description></item>
///   <item><description>空 scope **拒绝一切**（不是"不限制"）；</description></item>
///   <item><description>不认识的变量**保守拒绝**（拼错的变量不能退化成字面量匹配）。</description></item>
/// </list>
/// </remarks>
public sealed class PathScopeTests
{
    // ---- glob 基本语义 ----

    [Theory]
    [InlineData("a/b", "a/b", true)]
    [InlineData("a/b", "a/c", false)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("a/*", "a/b", true)]
    [InlineData("a/*", "a/b/c", false)]          // `*` 不跨分隔符——这条最容易写错
    [InlineData("a/**", "a/b", true)]
    [InlineData("a/**", "a/b/c/d", true)]
    [InlineData("a/**", "a", true)]              // `**` 能吃零段
    [InlineData("**/c", "a/b/c", true)]          // 前导 `**`
    [InlineData("**/c", "c", true)]              // 前导 `**` 吃零段
    [InlineData("a/**/c", "a/c", true)]          // 中间 `**` 吃零段
    [InlineData("a/**/c", "a/b/c", true)]
    [InlineData("a/**/c", "a/b/d/c", true)]
    [InlineData("**", "a/b/c", true)]
    [InlineData("a/?", "a/b", true)]
    [InlineData("a/?", "a/bb", false)]
    [InlineData("a/b*", "a/bcd", true)]
    [InlineData("a/b*", "a/b/c", false)]
    public void GlobBasics(string pattern, string path, bool expected)
        => Assert.Equal(expected, OrielGlob.IsMatch(pattern, path));

    [Fact]
    public void StarDoesNotCrossSeparators()
    {
        // 单独拎出来强调：`$APPDATA/*` 若让 `*` 跨分隔符，scope 就从"一层"变成"整棵子树"，
        // 而配置里写 `*` 的人以为的是一层。
        Assert.False(OrielGlob.IsMatch("$X/*", "$X/a/b"));
        Assert.True(OrielGlob.IsMatch("$X/**", "$X/a/b"));
    }

    [Theory]
    [InlineData("a/[bc]d", "a/bd", true)]
    [InlineData("a/[bc]d", "a/cd", true)]
    [InlineData("a/[bc]d", "a/dd", false)]
    [InlineData("a/[a-c]d", "a/bd", true)]
    [InlineData("a/[!a-c]d", "a/zd", true)]
    [InlineData("a/[!a-c]d", "a/bd", false)]
    [InlineData("a/[^a-c]d", "a/zd", true)]
    public void CharacterClasses(string pattern, string path, bool expected)
        => Assert.Equal(expected, OrielGlob.IsMatch(pattern, path));

    [Fact]
    public void UnclosedBracketIsLiteral()
    {
        // 手误的 `[` 不该让匹配变成"永远成功"——当字面量是最不容易出错的处理
        Assert.True(OrielGlob.IsMatch("a/[bc", "a/[bc"));
        Assert.False(OrielGlob.IsMatch("a/[bc", "a/b"));
    }

    [Theory]
    [InlineData("a/{b,c}", "a/b", true)]
    [InlineData("a/{b,c}", "a/c", true)]
    [InlineData("a/{b,c}", "a/d", false)]
    [InlineData("a/{b,c}/d", "a/c/d", true)]
    [InlineData("a/{b,{c,d}}/e", "a/d/e", true)]   // 嵌套
    [InlineData("a/{b,c", "a/{b,c", true)]        // 不配对当字面量
    public void Alternation(string pattern, string path, bool expected)
        => Assert.Equal(expected, OrielGlob.IsMatch(pattern, path));

    [Fact]
    public void PrefixSimilarDirectoriesAreNotConfused()
    {
        // 段级匹配天然挡住这类（`/a/b` 与 `/a/bc` 是不同的段），
        // 但它是安全判定的经典翻车点，所以显式钉一条。
        Assert.False(OrielGlob.IsMatch("a/b", "a/bc"));
        Assert.False(OrielGlob.IsMatch("a/b/**", "a/bc/d"));
    }

    [Fact]
    public void DoubleStarInsideSegmentBehavesLikeSingleStar()
    {
        // `a**b` 不是合法模式（glob crate 会报错）。这里当普通 `*` 处理：
        // 不放宽匹配面（仍然不跨分隔符），也不因为一个手误让整条配置失效。
        Assert.True(OrielGlob.IsMatch("a/x**y", "a/xzzy"));
        Assert.False(OrielGlob.IsMatch("a/x**y", "a/x/z/y"));
    }

    // ---- 变量展开 ----

    [Fact]
    public void EveryDirectoryHasAVariableAndViceVersa()
    {
        // 变量表与目录清单必须一一对应。漏了一边都不会报错——
        // 只会让某条 scope 永远不匹配（永远拒绝），而那看起来像"路径写错了"。
        var fromDirectories = new HashSet<string>(OrielKnownDirs.AllDirectories, StringComparer.Ordinal);
        var fromVariables = new HashSet<string>(
            OrielPathVariables.All.Select(v => OrielPathVariables.DirectoryOf(v)!),
            StringComparer.Ordinal);

        Assert.Equal(fromDirectories, fromVariables);
    }

    [Fact]
    public void VariablesAreAllUppercaseWithDollarPrefix()
    {
        foreach (string variable in OrielPathVariables.All)
        {
            Assert.StartsWith("$", variable, StringComparison.Ordinal);

            // 全大写：与 Tauri 的写法一致，迁移时不用改配置
            Assert.Equal(variable.ToUpperInvariant(), variable);
        }
    }

    [Fact]
    public void KnownVariableExpandsToTheResolvedDirectory()
    {
        string root = OrielKnownDirs.Resolve("Home", "com.example.app");
        string? expanded = OrielPathVariables.Expand("$HOME/**", "com.example.app");

        Assert.NotNull(expanded);
        Assert.Equal(root.Replace('\\', '/') + "/**", expanded.Replace('\\', '/'));
    }

    [Fact]
    public void VariableNameIsCaseInsensitive()
    {
        // 配置文件里的大小写笔误后果是"静默不匹配"，那是最难查的一类，
        // 所以这里刻意不区分大小写（Tauri 是区分的，这一点比它宽松）。
        Assert.NotNull(OrielPathVariables.Expand("$home/**", "app"));
        Assert.NotNull(OrielPathVariables.Expand("$Home/**", "app"));
    }

    [Fact]
    public void UnknownVariableExpandsToNull()
    {
        // **保守拒绝**：拼错的 `$APPDTA` 不能退化成"匹配名为 $APPDTA 的目录"——
        // 那台机器上万一真有这个目录，拼错的 scope 就成了意外放行。
        Assert.Null(OrielPathVariables.Expand("$APPDTA/**", "app"));
        Assert.Null(OrielPathVariables.Expand("$NOSUCHVAR", "app"));
    }

    [Fact]
    public void AbsolutePatternWithoutVariableIsUsedAsIs()
    {
        // scope 也可以直接写绝对路径（测试与"固定路径"场景用得上）
        string? expanded = OrielPathVariables.Expand("/srv/data/**", "app");

        Assert.Equal("/srv/data/**", expanded);
    }

    [Fact]
    public void VariableForUnsupportedDirectoryExpandsToNull()
    {
        // Windows 上没有 $RUNTIME 对应的目录。这条 scope 永远不能匹配——
        // 如实失败比"用空串拼出 /xxx"好。
        string variable = "$RUNTIME";

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(OrielPathVariables.Expand(variable + "/**", "app"));
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux 上它可能因为 XDG_RUNTIME_DIR 没设而解析不出来，那是环境相关；
            // 只断言"不抛异常"，两种结果都合理。
            _ = OrielPathVariables.Expand(variable + "/**", "app");
        }
    }

    // ---- scope 判定 ----

    private static OrielPathScope Scope(Action<OrielPathScope> configure)
    {
        var scope = new OrielPathScope();
        configure(scope);
        return scope;
    }

    [Fact]
    public void EmptyScopeDeniesEverything()
    {
        // **这条是整层最重要的断言**：空 scope 表示"不允许任何路径"，不是"不限制"。
        // 若反过来，Allow("fs.readTextFile") 会变成"能读整台机器"。
        var scope = new OrielPathScope();

        Assert.False(scope.IsMatch("/tmp/anything", "app", out string? reason));
        Assert.Contains("空 scope", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowMatchesAndReasonIsNullOnSuccess()
    {
        var scope = Scope(s => s.AllowPath("/srv/**"));

        Assert.True(scope.IsMatch("/srv/a/b", "app", out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void DenyBeatsAllow()
    {
        var scope = Scope(s => s.AllowPath("/srv/**").DenyPath("/srv/secret/**"));

        Assert.True(scope.IsMatch("/srv/public/a", "app", out _));
        Assert.False(scope.IsMatch("/srv/secret/a", "app", out string? reason));
        Assert.Contains("拒绝名单", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideAllowIsDeniedWithThePatternList()
    {
        var scope = Scope(s => s.AllowPath("/srv/**"));

        Assert.False(scope.IsMatch("/etc/passwd", "app", out string? reason));
        Assert.Contains("/srv/**", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyOnlyScopeDeniesEverything()
    {
        // 只写 deny 不写 allow：没有 allow 就什么都不允许（deny 是附加约束）
        var scope = Scope(s => s.DenyPath("/srv/secret/**"));

        Assert.False(scope.IsMatch("/srv/public/a", "app", out _));
    }

    // ---- 命令模式 → scope 的接线 ----

    private static OrielCapabilityOptions Options(Action<OrielCapabilityOptions> configure)
    {
        var options = new OrielCapabilityOptions();
        configure(options);
        return options;
    }

    [Fact]
    public void AllowWithScopeAlsoGrantsTheCommand()
    {
        // scope 是**附加**约束，不是授权本身。少了这步的后果是"声明了 scope 却仍被拒"，
        // 而错误文案会说"命令没授权"——把人引向完全错的方向。
        var options = Options(c => c.Allow("fs.readTextFile", s => s.AllowPath("/srv/**")));

        Assert.Contains("fs.readTextFile", options.AllowedCommands);
        Assert.True(options.IsPathAllowed("fs.readTextFile", "/srv/a", "app", out _));
    }

    [Fact]
    public void EmptyScopeInAllowThrows()
    {
        // 传了一个什么都不做的 lambda（多半是忘了写 AllowPath）应当**立刻**报错，
        // 而不是留到运行时变成"路径全被拒"。
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Options(c => c.Allow("fs.readTextFile", _ => { })));

        Assert.Contains("空 scope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandAllowedWithoutScopeDeniesAllPaths()
    {
        // 常见误解的正面反例：授权了命令但没给 scope
        var options = Options(c => c.Allow("fs.readTextFile"));

        Assert.True(OrielCapabilityRules.IsAllowed("fs.readTextFile", options.AllowedCommands, []));

        Assert.False(options.IsPathAllowed("fs.readTextFile", "/tmp/a", "app", out string? reason));
        Assert.Contains("没有声明任何路径 scope", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAndWriteCanHaveDifferentScopes()
    {
        // Tauri 的权限条目各自带 scope，所以"读得宽、写得窄"是能表达的——
        // 这个能力是"scope 挂在命令模式上"这个设计的直接目的。
        var options = Options(c => c
            .Allow("fs.readTextFile", s => s.AllowPath("/data/**"))
            .Allow("fs.writeTextFile", s => s.AllowPath("/data/reports/**")));

        Assert.True(options.IsPathAllowed("fs.readTextFile", "/data/any/a.txt", "app", out _));
        Assert.False(options.IsPathAllowed("fs.writeTextFile", "/data/any/a.txt", "app", out _));
        Assert.True(options.IsPathAllowed("fs.writeTextFile", "/data/reports/a.txt", "app", out _));
    }

    [Fact]
    public void WildcardPatternCoversSeveralCommands()
    {
        var options = Options(c => c.Allow("fs.*", s => s.AllowPath("/data/**")));

        Assert.True(options.IsPathAllowed("fs.readTextFile", "/data/a", "app", out _));
        Assert.True(options.IsPathAllowed("fs.exists", "/data/a", "app", out _));
        Assert.False(options.IsPathAllowed("fs.readTextFile", "/other/a", "app", out _));
    }

    [Fact]
    public void UnrelatedCommandIsNotCoveredByTheScope()
    {
        var options = Options(c => c.Allow("fs.readTextFile", s => s.AllowPath("/data/**")));

        // 别的命令没声明 scope → 拒绝，且原因是"没有声明任何 scope"而不是"路径不匹配"
        Assert.False(options.IsPathAllowed("fs.writeTextFile", "/data/a", "app", out string? reason));
        Assert.Contains("没有声明任何路径 scope", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyFromAnyEntryWins()
    {
        // 写了两条 scope、只在其中一条加 deny 时，另一条不该绕过它——
        // 那正是"先写宽的、再挖例外"这种写法的常见翻车点。
        var options = Options(c => c
            .Allow("fs.*", s => s.AllowPath("/data/**"))
            .Allow("fs.readTextFile", s => s.AllowPath("/data/**").DenyPath("/data/secret/**")));

        Assert.False(options.IsPathAllowed("fs.readTextFile", "/data/secret/a", "app", out _));
        Assert.True(options.IsPathAllowed("fs.readTextFile", "/data/public/a", "app", out _));
    }

    [Fact]
    public void CaseSensitivityFollowsTheFilesystem()
    {
        // Windows/macOS 的默认文件系统不区分大小写，Linux 区分。
        // 在那个区别上写错的话，症状是"模式对、路径也对，就是被拒"——很难查。
        var scope = Scope(s => s.AllowPath("/srv/Logs/**"));

        bool expected = OperatingSystem.IsLinux() ? false : true;
        Assert.Equal(expected, scope.IsMatch("/srv/logs/a.txt", "app", out _));
    }

    [Fact]
    public void PathSeparatorStyleDoesNotMatter()
    {
        // scope 一律用 '/' 写（Tauri 也如此），而 Windows 上调用方给的路径带 '\'
        var scope = Scope(s => s.AllowPath("/srv/data/**"));

        Assert.True(scope.IsMatch(@"\srv\data\a.txt", "app", out _));
        Assert.True(scope.IsMatch("/srv/data/a.txt", "app", out _));
        Assert.True(scope.IsMatch("/srv//data///a.txt", "app", out _));   // 重复分隔符
    }
}
