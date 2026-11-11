using System.Reflection;
using OrielWeb.Platform.Windows;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// Windows 后端的资源释放契约（2026-11-11 评审 L9 的回归闸）。
/// </summary>
/// <remarks>
/// <para>
/// 这类缺陷没有症状：一个 <c>HWND_MESSAGE</c> 隐藏窗口不销毁，进程退出后操作系统会回收，
/// 所以"漏了"在单进程单窗口的测试里完全看不出来。但它是**真实的**泄漏——
/// 同一进程里重建后端（托盘 + 主窗口组合、或测试套件里反复 Build）会持续累积句柄。
/// </para>
/// <para>
/// 这里只能断言"结构上做了"（Dispose 里确实调了 DestroyWindow / OleUninitialize），
/// 不能断言"真的销毁成功"——那需要真实消息循环与 HWND 校验，属真机自检的范围。
/// 这一点如实写在每条用例里，不假装已证。
/// </para>
/// </remarks>
public sealed class WindowsBackendLifetimeTests
{
    private static string Source()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OrielWeb.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);

        string path = Path.Combine(dir!, "src", "OrielWeb", "Platform", "Windows", "WindowsPlatformBackend.cs");
        Assert.True(File.Exists(path), $"找不到源码：{path}");

        return File.ReadAllText(path);
    }

    [Fact]
    public void DisposeDestroysTheDispatchWindow()
    {
        // 调度窗口是 HWND_MESSAGE 的隐藏窗口：消息循环退出只是"没人泵消息"，
        // 句柄与窗口类都还在。原注释写的"随消息循环结束销毁"是错的。
        string source = Source();
        int start = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(start >= 0, "没找到 WindowsPlatformBackend.Dispose——断言随重构会失效，请同步更新");

        // 截到方法结束：取到下一个同级成员声明之前的范围足够覆盖整个 Dispose。
        int end = source.IndexOf("// ---- 消息窗口 ----", start, StringComparison.Ordinal);
        Assert.True(end > start, "Dispose 之后没找到「消息窗口」分节标记，无法界定范围");

        string body = source[start..end];

        Assert.Contains("DestroyWindow(_messageHwnd)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposePairsOleUninitialize()
    {
        // 每一次成功的 OleInitialize（含 S_FALSE）都要在同一线程配对一次 OleUninitialize。
        // 无条件解除也不对：初始化失败时那次调用没加计数，解除会把别人的减掉。
        string source = Source();

        Assert.Contains("_oleInitialized = true", source, StringComparison.Ordinal);
        Assert.Contains("OleUninitialize()", source, StringComparison.Ordinal);

        int start = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        int end = source.IndexOf("// ---- 消息窗口 ----", start, StringComparison.Ordinal);
        string body = source[start..end];

        Assert.Contains("_oleInitialized", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeIsIdempotentInShape()
    {
        // Dispose 可能被调多次（OrielApp.Dispose 与终结路径）。这里不要求"只执行一次"，
        // 而要求每一步本身是幂等的：
        //   * _messageHwnd 是 readonly，销毁两次的第二次会失败并报诊断（不会崩）；
        //   * _oleInitialized 用字段门控，解除后应当清掉标志，否则第二次会多减一次计数。
        string source = Source();
        int start = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        int end = source.IndexOf("// ---- 消息窗口 ----", start, StringComparison.Ordinal);
        string body = source[start..end];

        // 解除之后要把标志复位，这样第二次 Dispose 不会再调 OleUninitialize。
        Assert.Matches(@"_oleInitialized\s*=\s*false", body);
    }
}
