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

    // ---- ORIELWEB106：win. 保留前缀（Warning：不阻断构建，但必须让人看见） ----

    [Fact]
    public void ReservedWinPrefix_IsWarnedButStillGenerates()
    {
        var diagnostics = OrielDiagnostics("""
            public static class Commands
            {
                [OrielWeb.Ipc.OrielCommand("win.custom")]
                public static string Ok() => "ok";
            }
            """);

        var warning = Assert.Single(diagnostics);
        Assert.Equal("ORIELWEB106", warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }
}
