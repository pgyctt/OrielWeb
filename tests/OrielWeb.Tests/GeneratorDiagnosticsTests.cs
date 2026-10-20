using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 源生成器编译期诊断（ORIELWEB1xx）：这些形态此前要么被静默吞掉（空白命令名 → 前端
/// "未知命令"、win. 前缀被内建遮蔽），要么变成生成文件里定位不了的 CS 错误（private 方法、
/// 泛型宿主、ref/out 参数、含换行的名字）。诊断必须指回用户方法的位置。
/// </summary>
/// <remarks>
/// 用内存编译跑**真生成器**：引用 OrielWeb.dll（特性与生成代码依赖的类型都在里面），
/// 生成器实例经反射从 OrielWeb.Generators.dll 取（由测试项目的库引用带进输出目录）。
/// 只断言 ORIELWEB1xx——生成代码在内存编译里的其它问题（引用集刻意不全）与本测试无关。
/// </remarks>
public sealed class GeneratorDiagnosticsTests
{
    // 测试专用反射（按名取生成器类型 + 程序集 Location 当引用源）：
    // 测试程序集不参与 single-file/AOT 发布，与 TestHarness 的抑制同一理由。
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "测试程序集不参与单文件发布。")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "类型名是字面量，且测试程序集不参与裁剪发布。")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "生成器有无参构造，测试程序集不参与裁剪发布。")]
    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<GeneratedSourceResult> Sources) Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorProbe",
            // path 必须给：ParseText 默认空路径会让诊断的 Location.GetLineSpan().Path 为空
            [CSharpSyntaxTree.ParseText(source, path: "Probe.cs")],
            [
                MetadataReference.CreateFromFile(typeof(OrielWeb.Ipc.OrielCommandAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assembly generatorAssembly = Assembly.Load("OrielWeb.Generators");
        var generatorType = generatorAssembly.GetType("OrielWeb.Generators.OrielCommandGenerator")
            ?? throw new InvalidOperationException("找不到 OrielCommandGenerator 类型（检查生成器库引用）。");
        var generator = (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;

        var result = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation).GetRunResult();
        return (result.Diagnostics, result.Results[0].GeneratedSources);

    }

    private static ImmutableArray<Diagnostic> OrielDiagnostics(string source)
        => Compile(source).Diagnostics.Where(d => d.Id.StartsWith("ORIELWEB", StringComparison.Ordinal)).ToImmutableArray();

    // ---- 正常路径：无诊断，且路由源文件真的生成了 ----

    [Fact]
    public void ValidCommand_ProducesNoOrielDiagnosticsAndGeneratesRouter()
    {
        var (diagnostics, sources) = Compile("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("t.ok")]
                public static string Ok() => "ok";
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Id.StartsWith("ORIELWEB", StringComparison.Ordinal)));
        Assert.Contains(
            sources,
            s => s.HintName == "OrielWeb.Generated.g.cs" && s.SyntaxTree.ToString().Contains("OrielModuleInit"));
    }

    // ---- ORIELWEB101：命令名不可用 ----

    [Theory]
    [InlineData("[OrielWeb.Ipc.OrielCommand(\"\")]")]       // 空白：此前被静默丢弃，前端得到"未知命令"
    [InlineData("[OrielWeb.Ipc.OrielCommand(\" \\t \")]")]  // 纯空白
    [InlineData("[OrielWeb.Ipc.OrielCommand(\"a\\nb\")]")]  // 换行（值里是真换行）：此前生成 CS1010
    public void InvalidCommandName_IsReportedOnTheUserMethod(string attribute)
    {
        var diagnostics = OrielDiagnostics($$"""
            public static class Commands
            {
                {{attribute}}
                public static string Ok() => "ok";
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ORIELWEB101", diagnostic.Id);
        // 诊断必须落在用户方法上（而不是生成文件）
        Assert.Equal("Probe.cs", diagnostic.Location.GetLineSpan().Path);
    }

    // ---- ORIELWEB102：同名命令（此前只在运行期启动时抛异常） ----

    [Fact]
    public void DuplicateCommandName_IsReportedForBothDeclarations()
    {
        var diagnostics = OrielDiagnostics("""
            public static class CommandsA
            {
                [OrielWeb.Ipc.OrielCommand("t.dup")]
                public static string A() => "a";
            }

            public static class CommandsB
            {
                [OrielWeb.Ipc.OrielCommand("t.dup")]
                public static string B() => "b";
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("ORIELWEB102", d.Id));
    }

    // ---- ORIELWEB103/104/105：此前是生成文件里的 CS 错误 ----

    [Fact]
    public void PrivateCommandMethod_IsReported()
    {
        var diagnostics = OrielDiagnostics("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("t.private")]
                private static string Ok() => "ok";
            }
            """);

        Assert.Equal("ORIELWEB103", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void GenericHostType_IsReported()
    {
        var diagnostics = OrielDiagnostics("""
            public static class Commands<T>
            {
                [OrielWeb.Ipc.OrielCommand("t.generic")]
                public static string Ok() => "ok";
            }
            """);

        Assert.Equal("ORIELWEB104", Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("out")]
    public void RefOrOutParameter_IsReported(string modifier)
    {
        var diagnostics = OrielDiagnostics($$"""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("t.byref")]
                public static void WithByRef({{modifier}} int value) { }
            }
            """);

        Assert.Equal("ORIELWEB105", Assert.Single(diagnostics).Id);
    }

    // ---- ORIELWEB106：win. 保留前缀（Error：构建期拦下，不再靠运行期排障） ----

    [Fact]
    public void ReservedWinPrefix_IsAnErrorButStillGenerates()
    {
        var (all, sources) = Compile("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("win.custom")]
                public static string Ok() => "ok";
            }
            """);

        var error = Assert.Single(all.Where(d => d.Id.StartsWith("ORIELWEB", StringComparison.Ordinal)));
        Assert.Equal("ORIELWEB106", error.Id);

        // **Error 而不是 Warning**：这条描述的是"注册成功但永远不会被调用"——
        // 一个永远不会执行的分支，没有合法的使用场景。Warning 时它的症状是
        // "我明明写了这个命令，页面说找不到"，而根因在另一条命令上，极难归因。
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);

        // 仍然生成路由：诊断不该把生成器打成"什么都不产出"，
        // 否则同一个编译里会再冒出十几条"找不到路由"的次生错误。
        Assert.Contains("win.custom", string.Join('\n', sources.Select((s) => s.SourceText.ToString())));
    }

    // ---- ORIELWEB107：event.emit 与库内建命令重合 ----

    [Fact]
    public void ReservedEventEmitName_IsWarnedButStillGenerates()
    {
        // event.emit 是库内建（@orielweb/api/event 的 emit/emitTo 依赖它），
        // 应用同名命令会被它遮蔽——注册成功但永不可达。
        var diagnostics = OrielDiagnostics("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("event.emit")]
                public static string Ok() => "ok";
            }
            """);

        var warning = Assert.Single(diagnostics);
        Assert.Equal("ORIELWEB107", warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Theory]
    [InlineData("event.onUserAction")]
    [InlineData("event.log")]
    [InlineData("events.emit")]
    [InlineData("event")]
    public void OtherEventLikeNames_AreNotWarned(string commandName)
    {
        // 只保留了 event.emit **这一个名字**，不是整个 event. 前缀——
        // 应用定义 event.onUserAction 之类完全合法，按前缀警告只会制造噪音。
        var diagnostics = OrielDiagnostics($$"""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("{{commandName}}")]
                public static string Ok() => "ok";
            }
            """);

        Assert.Empty(diagnostics);
    }

    // ---- 通道参数：绑定 + 自动关闭（B9） ----

    [Fact]
    public void ChannelParameter_GeneratesBindAndFinally()
    {
        var (diagnostics, sources) = Compile("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("t.stream")]
                public static void Stream(OrielWeb.Ipc.OrielChannel c) => c.Send(1);
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("ORIELWEB", StringComparison.Ordinal));

        string all = string.Join('\n', sources.Select(s => s.SourceText.ToString()));

        // 绑定：字符串参数 → OrielChannel（推送能力来自分发器给的 sink）
        Assert.Contains("OrielChannel.Bind", all);
        Assert.Contains("sink, jsonContext", all);

        // 自动关闭：命令返回、抛异常、后续参数绑定失败都要走 finally。
        // 少了它，页面侧每次调用会漏一个监听——而那种泄漏在功能上看不出来。
        Assert.Contains("finally", all);
        Assert.Contains("?.Close();", all);
    }
}
