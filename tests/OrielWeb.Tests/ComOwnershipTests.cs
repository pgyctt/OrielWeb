using System.Reflection;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.WebView2.Com;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// Windows COM 引用所有权的回归闸（2026-11-11 评审 M2 / M9）。
/// </summary>
/// <remarks>
/// <para>
/// 这两个缺陷的共同点是**没有症状**：少 Release 一份引用不会立刻崩，只是长会话里对象越攒越多；
/// 而"装配链跨过窗口销毁"只在"用户在 WebView2 起来之前关窗口"时出现——手工测几乎撞不到。
/// 所以断言只能钉在**结构**上：谁在什么时候负责释放、哪些调用点不该造包装器。
/// </para>
/// <para>
/// 真正的引用计数配平需要真机 COM 对象，本机（Windows，但无 WebView2 运行时依赖的测试上下文）
/// 只能验到"声明形状"这一层。这一点在每条用例的注释里写明，不假装已证。
/// </para>
/// </remarks>
public sealed class ComOwnershipTests
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void ComObject_HasFinalizer()
    {
        // 构造函数自己 AddRef 一份（理由见 ComObject<T> 的构造注释），所以必须有终结器兜底：
        // 调用方忘了 Dispose 时，那份引用不能活到进程结束。
        var type = typeof(ComObject<>);
        MethodInfo? finalizer = type.GetMethod(
            "Finalize", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotNull(finalizer);
    }

    [Fact]
    public void Dispose_SuppressesFinalization()
    {
        // 有终结器就必须抑制：否则这份引用会被 Release 两次（第二次在终结队列上），
        // 而第二次 Release 打到的是可能已被复用的地址。
        string source = File.ReadAllText(SourcePath("WebView2", "Com", "ComObject.cs"));

        Assert.Contains("GC.SuppressFinalize(this)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ToManaged_DoesNotCreateAWrapper()
    {
        // 四处 "FromPointer<T>(p)?.Object" 是 M2 的根因：造一个包装器、只取 Object、
        // 包装器变垃圾。包装器构造时 AddRef，没人 Dispose 就是净 +1。
        // 正确的写法是直接转换（ToManaged），它不做 AddRef、也就不需要释放。
        var method = typeof(ComObject).GetMethod("ToManaged", AnyStatic);

        Assert.NotNull(method);
        Assert.True(method!.IsGenericMethodDefinition, "ToManaged 应当是泛型方法");

        // 返回类型必须是 T（不是 IComObject<T>）：返回包装器就等于又回到了旧写法
        Type returnType = method.ReturnType;
        Assert.True(
            returnType.IsGenericParameter,
            $"ToManaged 应当直接返回托管对象（T），实际返回 {returnType.Name}");
    }

    [Theory]
    [InlineData("get_Request")]
    [InlineData("get_MenuItems")]
    [InlineData("GetValueAtIndex")]
    public void PropertyAccessors_UseToManagedInsteadOfWrapper(string memberName)
    {
        // 这三处是 M2 的原始现场（NativeExtensions.cs）：每取一次属性泄漏一个引用。
        string source = File.ReadAllText(SourcePath("WebView2", "Utilities", "NativeExtensions.cs"));

        // 抓出该成员附近的那一段，断言里面用的是 ToManaged 而不是 FromPointer
        int index = source.IndexOf(memberName, StringComparison.Ordinal);
        Assert.True(index >= 0, $"没找到 {memberName}——这条断言随代码搬动会失效，请同步更新");

        string slice = source.Substring(index, Math.Min(600, source.Length - index));

        Assert.Contains("ComObject.ToManaged<", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("FromPointer<", slice, StringComparison.Ordinal);
    }

    [Fact]
    public void DcompObjects_AreReleasedOnWindowDestroy()
    {
        // M2 的另一半：DirectComposition 的三个包装器此前只置 null、从不 Dispose，
        // 每次建/关窗口泄漏三个引用。
        string source = File.ReadAllText(SourcePath("Win32WindowHost.cs"));
        int start = source.IndexOf("internal void OnWindowDestroyedCore", StringComparison.Ordinal);
        Assert.True(start >= 0, "没找到 OnWindowDestroyedCore——断言随重构会失效，请同步更新");

        string body = source.Substring(start, Math.Min(4000, source.Length - start));

        foreach (string field in new[] { "_dcompVisual", "_dcompTarget", "_dcompDevice" })
        {
            Assert.Contains($"Interlocked.Exchange(ref {field}, null)?.Dispose()", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HwndIsZeroedOnDestroy()
    {
        // M9：装配是异步的，用户在装配完成前关窗口是常态。不清零 _hwnd 时那条链路醒来
        // 仍认为"窗口还活着"，会拿一个已释放的 HWND 去建组合控制器（系统可能已复用它）。
        string source = File.ReadAllText(SourcePath("Win32WindowHost.cs"));
        int start = source.IndexOf("internal void OnWindowDestroyedCore", StringComparison.Ordinal);
        Assert.True(start >= 0);

        string body = source.Substring(start, Math.Min(4000, source.Length - start));

        Assert.Contains("_hwnd = 0;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyChain_RechecksLivenessAfterEveryAwait()
    {
        // 装配链里每个 await 之后都必须复查 WindowAlive：漏一个就等于留了一条
        // "往已销毁窗口上挂东西"的路。这里数的是**结构比例**而不是具体行号：
        // await 的数量与复查的数量必须匹配（await 之后紧跟 return 的那种也算）。
        string source = File.ReadAllText(SourcePath("Win32WindowHost.Content.cs"));
        int start = source.IndexOf("private async Task InitializeWebView2Async", StringComparison.Ordinal);
        Assert.True(start >= 0, "没找到 InitializeWebView2Async——断言随重构会失效，请同步更新");

        int end = source.IndexOf("catch (Exception ex)", start, StringComparison.Ordinal);
        Assert.True(end > start);

        string body = source.Substring(start, end - start);

        int awaits = CountOccurrences(body, ".ConfigureAwait(true);")
                     + CountOccurrences(body, "await _webView.AddScriptToExecuteOnDocumentCreatedAsync");

        // ConfigureAwait(true) 与那条脚本注入各算一次；脚本注入那行也带 ConfigureAwait(true)，
        // 所以按 ConfigureAwait 计数即可（它只出现在 await 结尾）。
        int livenessChecks = CountOccurrences(body, "if (!WindowAlive)");

        Assert.True(
            livenessChecks >= awaits,
            $"装配链里有 {awaits} 处 await，却只有 {livenessChecks} 处 WindowAlive 复查（M9 回归）");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>按相对路径读本库源码（测试项目不复制源码，直接按仓库布局回溯）。</summary>
    private static string SourcePath(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OrielWeb.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);

        var segments = new List<string> { dir!, "src", "OrielWeb", "Platform", "Windows" };
        segments.AddRange(parts);

        string path = Path.Combine([.. segments]);
        Assert.True(File.Exists(path), $"找不到源码文件：{path}");

        return path;
    }
}
