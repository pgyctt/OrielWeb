using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 主线程投递队列的回收语义（2026-11-11 评审 L8 的回归闸）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷</b>：投递是"先入队、再让主循环排空"两步。第二步失败时（Linux 的
/// <c>g_idle_add_full</c> 返回 0、macOS 的 pump helper 不可用），那个动作永远不会被排空。
/// Linux 侧原来的写法是 <c>TryDequeue(out _)</c>——摘掉的是**队首**，也就是最早排进来、
/// 可能已经有一个 idle source 在等它的那个。后果是那一个永远丢失，而刚入队的这一个
/// 留在队列里等一个永远不会到来的调度。macOS 侧则干脆不回收。
/// </para>
/// <para>
/// <b>为什么这里只能断言结构</b>：触发条件是"主循环资源建不出来"，
/// Linux 那侧需要真实 GTK 且构造出资源耗尽，macOS 那侧需要 pump helper 建不出来。
/// 单测（Windows 上跑、无 GTK/ObjC）造不出这些条件；行为层面的取证在真机自检那一侧。
/// 所以这条用**结构断言**挡住退回到旧写法，并如实标注它证明的范围。
/// </para>
/// <para>
/// 断言**不锚定行号或精确缩进**（CRLF/LF 差异与缩进变化都会让它假红）：
/// 只断言"两个平台的失败分支都走共享的按引用回收"这几件事实。
/// </para>
/// </remarks>
public sealed class MainThreadDispatchQueueTests
{
    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OrielWeb.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string ReadSource(params string[] relativeParts)
    {
        string path = Path.Combine([RepositoryRoot(), .. relativeParts]);
        Assert.True(File.Exists(path), $"找不到源码：{path}");
        return File.ReadAllText(path);
    }

    /// <summary>剥掉 <c>//</c> 行注释（注释里会引用被修掉的旧代码）。</summary>
    private static string StripComments(string source)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int comment = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                lines[i] = lines[i][..comment];
            }
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public void SharedHelper_ReclaimsByReferenceAndKeepsTheRest()
    {
        // 按引用相等比较：委托的 `==` 是**值语义**（逐项比较调用列表），
        // 两个内容相同的 lambda 会相等，用它做判断会误删另一个动作。
        string shared = ReadSource("src", "OrielWeb", "Platform", "MainThreadDispatchQueue.cs");

        Assert.Contains("ReferenceEquals(item, action)", shared, StringComparison.Ordinal);

        // 只丢一个：`removed` 标志保证同一个委托的多个副本不会全被丢掉。
        Assert.Contains("bool removed = false", shared, StringComparison.Ordinal);
        Assert.Contains("if (!removed && ReferenceEquals(item, action))", shared, StringComparison.Ordinal);

        // 其余动作原样入队（取空再塞回时顺序保持）。
        Assert.Contains("queue.Enqueue(item)", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxReclaimsTheJustQueuedAction()
    {
        string source = ReadSource("src", "OrielWeb", "Platform", "Linux", "LinuxSignalHandlers.cs");

        // 失败分支要按引用精确回收刚入队的那个动作。
        Assert.Contains("MainThreadDispatchQueue.TryRemoveOne(MainThreadQueue, action)", source, StringComparison.Ordinal);

        // 反面：不能再用"摘队首"的写法（注释里提到它，所以剥掉注释再查）。
        Assert.DoesNotContain("TryDequeue(out _)", StripComments(source), StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSAlsoReclaimsTheJustQueuedAction()
    {
        // macOS 此前**根本不回收**：失败时那个动作留在队列里。
        // 单个动作留着不致命（pump 不工作，队列本来就不会被排空），
        // 但与 Linux 的语义不一致，且让"队列长度"这个诊断信号失真。
        string source = ReadSource("src", "OrielWeb", "Platform", "MacOS", "MacOSPlatformBackend.cs");

        Assert.Contains("MainThreadDispatchQueue.TryRemoveOne(MainThreadQueue, action)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSPumpDiagnostic_ExplainsCauseImpactAndFix()
    {
        // 文案项（2026-11-11 评审 L8）：原文只说"pump helper 不可用"，
        // 读的人既不知道那是什么，也不知道该去查什么。
        string source = ReadSource("src", "OrielWeb", "Platform", "MacOS", "MacOSPlatformBackend.cs");

        Assert.Contains("pump helper", source, StringComparison.Ordinal);

        // 影响：await 续体不回主线程。
        Assert.Contains("await 之后的续体不会回到主线程", source, StringComparison.Ordinal);

        // 常见原因：指向真正会返回 0 的那个调用点。
        Assert.Contains("CreatePumpHelper", source, StringComparison.Ordinal);

        // 怎么办：给出可执行的排查方向。
        Assert.Contains("OrielApp.Run() 在主线程调用", source, StringComparison.Ordinal);
    }
}
