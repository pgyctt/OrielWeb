using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建路径命令族（<c>path.*</c>）：对标 Tauri 2 的路径运算。
/// </summary>
/// <remarks>
/// <para>
/// 这一族的实现委托给 <see cref="System.IO.Path"/>，所以这些测试**不是**在验证 .NET 的
/// 路径规则（那是 BCL 的事），而是在钉三件库自己的事：
/// </para>
/// <list type="number">
///   <item><description>参数形状与错误码（缺 <c>paths</c>、元素不是字符串）；</description></item>
///   <item><description>与 Tauri/Node 的**口径差异**（<c>extname</c> 不带点、<c>dirname</c> 到根给空串、
///     <c>basename</c> 的 ext 比较区分大小写）；</description></item>
///   <item><description><c>normalize</c> **不做 I/O**：对不存在的路径也要能工作
///     （Tauri 会在这里报错，那是有意的分歧）。</description></item>
/// </list>
/// <para>
/// 断言的是"形状"而不是夹在字面量里的完整路径：拿 <c>/a/b</c> 去断言 Windows 上的结果
/// 会得到一个只在 Linux 上绿的测试。<c>Path.DirectorySeparatorChar</c> 用来拼平台相关的输入。
/// </para>
/// <para>
/// 参数用**手拼的 JSON 文本**而不是匿名对象 + 序列化：测试项目不开源生成上下文，
/// 而反射序列化在 AOT 分析下会报 IL2026/IL3050（本仓库把警告当错误）。
/// 手拼也更贴近真实回执的样子。
/// </para>
/// </remarks>
public sealed class BuiltInPathCommandTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    // ---- 纯字符串运算 ----

    [Fact]
    public void Join_ConcatenatesSegments()
    {
        Assert.Equal(
            $"a{Sep}b{Sep}c",
            Invoke<string>("path.join", $$"""{ "paths": ["a", "b", "c"] }"""));
    }

    [Fact]
    public void Join_EmptyArrayGivesEmptyString()
        => Assert.Equal(string.Empty, Invoke<string>("path.join", """{ "paths": [] }"""));

    [Fact]
    public void Dirname_ReturnsParent()
    {
        Assert.Equal(
            $"a{Sep}b",
            Invoke<string>("path.dirname", $$"""{ "path": {{S($"a{Sep}b{Sep}c.txt")}} }"""));
    }

    [Fact]
    public void Dirname_AtRootGivesEmptyString()
    {
        // Tauri 的 Path::parent() 在根上给 None，序列化成空串。
        // 断言空串而不是抛错：调用方据此判断"到头了"，抛错会逼它到处包 try。
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Equal(string.Empty, Invoke<string>("path.dirname", $$"""{ "path": {{S(root)}} }"""));
    }

    [Fact]
    public void Extname_OmitsTheLeadingDot()
    {
        // **与 Node 的 path.extname 不同**（Node 给 ".html"），跟 Tauri/Rust 一致。
        Assert.Equal("html", Invoke<string>("path.extname", """{ "path": "file.html" }"""));
    }

    [Fact]
    public void Extname_NoExtensionGivesEmptyString()
        => Assert.Equal(string.Empty, Invoke<string>("path.extname", """{ "path": "Makefile" }"""));

    [Theory]
    [InlineData(".gitignore")]
    [InlineData("archive.tar.")]
    public void Extname_DotfileAndTrailingDotGiveEmptyString(string name)
    {
        // .gitignore：整名算文件名（前导点不是扩展名分隔符）
        // archive.tar.：尾随点后面是空的，所以没有扩展名
        Assert.Equal(string.Empty, Invoke<string>("path.extname", $$"""{ "path": "{{name}}" }"""));
    }

    [Fact]
    public void Extname_TakesTheLastExtensionOnly()
        => Assert.Equal("gz", Invoke<string>("path.extname", """{ "path": "archive.tar.gz" }"""));

    [Fact]
    public void Basename_ReturnsLastSegment()
    {
        Assert.Equal(
            "app.conf",
            Invoke<string>("path.basename", $$"""{ "path": {{S($"a{Sep}b{Sep}app.conf")}} }"""));
    }

    [Fact]
    public void Basename_StripsTheGivenExtension()
        => Assert.Equal(
            "app",
            Invoke<string>("path.basename", """{ "path": "dir/app.conf", "ext": ".conf" }"""));

    [Fact]
    public void Basename_ExtensionComparisonIsCaseSensitive()
    {
        // 刻意区分大小写（与 Tauri 一致）：不同平台的文件名大小写规则不同，
        // "传什么就按什么比"是最不容易猜错的口径。要去掉 .PNG 就传 PNG。
        Assert.Equal(
            "logo.PNG",
            Invoke<string>("path.basename", """{ "path": "logo.PNG", "ext": ".png" }"""));
    }

    [Fact]
    public void Basename_WithoutExtLeavesTheNameIntact()
        => Assert.Equal("a.txt", Invoke<string>("path.basename", """{ "path": "d/a.txt" }"""));

    [Fact]
    public void Basename_IgnoresTrailingSeparators()
    {
        // Rust 的 Path::file_name() 忽略尾随分隔符（Tauri 文档里明写这条），
        // 而 .NET 的 Path.GetFileName("/a/b/") 给空串——所以这里显式去尾再取。
        Assert.Equal(
            "b",
            Invoke<string>("path.basename", $$"""{ "path": {{S($"a{Sep}b{Sep}")}} }"""));
    }

    [Fact]
    public void Dirname_IgnoresTrailingSeparators()
    {
        // 同理：Tauri 文档的例子 dirname('/path/to/somedir/') 给 '/path/to'，
        // 而不是 'somedir'（.NET 的 GetDirectoryName 会给后者）
        Assert.Equal(
            $"a{Sep}b",
            Invoke<string>("path.dirname", $$"""{ "path": {{S($"a{Sep}b{Sep}c{Sep}")}} }"""));
    }

    [Fact]
    public void IsAbsolute_PlatformSpecific()
    {
        // 判据是平台相关的，所以断言"明显该为真的那些"，而不是拿 "/foo" 跨平台断言
        // （Windows 上它有根无盘符，仍算绝对）。
        string absolute = Path.GetFullPath("x");

        Assert.True(Invoke<bool>("path.isAbsolute", $$"""{ "path": {{S(absolute)}} }"""));
        Assert.False(Invoke<bool>("path.isAbsolute", """{ "path": "a/b" }"""));
    }

    // ---- normalize / resolve：词法，不做 I/O ----

    [Fact]
    public void Normalize_ResolvesDotSegments()
    {
        string result = Invoke<string>(
            "path.normalize",
            $$"""{ "path": {{S($"a{Sep}b{Sep}..{Sep}c")}} }""");

        Assert.EndsWith($"a{Sep}c", result);
        Assert.True(Path.IsPathFullyQualified(result), "normalize 的结果应当是绝对路径");
    }

    [Fact]
    public void Normalize_WorksOnNonexistentPaths()
    {
        // **这是与 Tauri 的关键分歧**：Tauri 的 normalize 用 std::fs::canonicalize，
        // 路径不存在时直接报错。而"把用户输入的路径先归一一下"恰恰经常发生在
        // 路径还不存在的时候——那条路上报错是很糟的体验。本库纯词法，所以这条必须通过。
        string ghost = Path.Combine(Path.GetTempPath(), "oriel-nonexistent-" + Guid.NewGuid().ToString("N"));

        string result = Invoke<string>("path.normalize", $$"""{ "path": {{S(ghost)}} }""");

        Assert.Contains(Path.GetFileName(ghost), result);
    }

    [Fact]
    public void Normalize_IsPureFunctionOfItsInput()
    {
        // "不做 I/O"的一个可观测后果：同一个输入在任何文件系统状态下都给出同一个结果。
        // 解析符号链接会让结果依赖文件系统状态——那正是要避免的。
        string input = $"nonexistent-a{Sep}nonexistent-b{Sep}file";
        string args = $$"""{ "path": {{S(input)}} }""";

        Assert.Equal(Invoke<string>("path.normalize", args), Invoke<string>("path.normalize", args));
    }

    [Fact]
    public void Normalize_InvalidPathGivesInvalidArgumentCode()
    {
        // NUL 在路径里不合法——这类输入要给出可判别的错误码，而不是让调用方去看文案
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.normalize", """{ "path": "a\u0000b" }"""));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Resolve_MakesRelativePathsAbsolute()
    {
        string result = Invoke<string>("path.resolve", $$"""{ "paths": ["sub", "file.txt"] }""");

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.EndsWith($"sub{Sep}file.txt", result);
    }

    [Fact]
    public void Resolve_DiffersFromJoinOnRelativeInput()
    {
        // resolve 保证绝对、join 不保证——这是两者的实质差别（Tauri 亦然）
        string joined = Invoke<string>("path.join", """{ "paths": ["sub", "f.txt"] }""");
        string resolved = Invoke<string>("path.resolve", """{ "paths": ["sub", "f.txt"] }""");

        Assert.False(Path.IsPathFullyQualified(joined));
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    // ---- 参数校验与错误码 ----

    [Fact]
    public void Join_MissingPathsGivesMissingArgumentCode()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke<string>("path.join", "{ }"));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void Join_NonArrayPathsGivesInvalidArgumentCode()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.join", """{ "paths": "not-an-array" }"""));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Join_NonStringElementGivesInvalidArgumentCode()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.join", """{ "paths": ["ok", 42] }"""));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void UnknownPathCommand_IsNotHandled()
    {
        // 前缀对上但名字不认识：要报"未知命令"而不是静默当成 path.* 处理
        Assert.False(OrielBuiltInPathCommands.TryGet("path.nonexistent", out _));
    }

    // ---- 能力：path.* 整族放行 ----

    [Theory]
    [InlineData("path.join")]
    [InlineData("path.dirname")]
    [InlineData("path.extname")]
    [InlineData("path.basename")]
    [InlineData("path.isAbsolute")]
    [InlineData("path.normalize")]
    [InlineData("path.resolve")]
    public void PathCommandsAreAlwaysAllowed(string name)
        => Assert.True(OrielCapabilityRules.IsAlwaysAllowedPathCommand(name));

    [Theory]
    [InlineData("paths.join")]
    [InlineData("fs.readTextFile")]
    [InlineData("path")]
    [InlineData("path.")]
    public void SimilarNamesAreNotAllowedByThePathRule(string name)
    {
        // 前缀相近但不同族的名字不该被这条规则放行——放行面的边界要能挡住这类拼写
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(name));
    }

    /// <summary>把字符串编成 JSON 字面量（测试夹具用，只需要处理引号与反斜杠）。</summary>
    private static string S(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>直接调内建处理函数（不经分发器：这些命令不需要窗口，也没有路由状态）。</summary>
    private static T Invoke<T>(string name, string argsJson)
    {
        Assert.True(OrielBuiltInPathCommands.TryGet(name, out var handler), $"未注册的命令：{name}");

        using var doc = JsonDocument.Parse(argsJson);
        return (T)handler(doc.RootElement)!;
    }
}
