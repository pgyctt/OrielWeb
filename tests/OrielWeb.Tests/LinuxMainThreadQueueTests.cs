using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// Linux 主线程投递队列的回收语义（2026-11-11 评审 L8 的回归闸）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷</b>：<c>g_idle_add_full</c> 失败时要回收"刚入队的那个动作"，
/// 原实现却是 <c>TryDequeue(out _)</c>——摘掉的是**队首**，也就是最早排进来、
/// 可能已经有一个 idle source 在等它的那个。后果是那一个永远丢失，
/// 而刚入队的这一个留在队列里等一个永远不会到来的调度。
/// </para>
/// <para>
/// <b>为什么这里只能断言结构</b>：触发条件是"GTK 的 idle source 建不出来"，
/// 那需要真实 GTK 且构造出资源耗尽。单测（Windows 上跑、无 GTK）造不出这个条件。
/// 行为层面的取证在真机自检那一侧——那里的 <c>PostToMainThread</c> 是真跑的。
/// 所以这条用**结构断言**挡住"改回 TryDequeue(out _)"，并如实标注它证明的范围。
/// </para>
/// <para>
/// 断言**不锚定行号或精确缩进**（CRLF/LF 差异与缩进变化都会让它假红）：
/// 只断言"这个文件里有按引用回收的实现"与"没有裸的 TryDequeue(out _)"这两件事实。
/// </para>
/// </remarks>
public sealed class LinuxMainThreadQueueTests
{
    private static string Source()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OrielWeb.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);

        string path = Path.Combine(dir!, "src", "OrielWeb", "Platform", "Linux", "LinuxSignalHandlers.cs");
        Assert.True(File.Exists(path), $"找不到源码：{path}");

        return File.ReadAllText(path);
    }

    [Fact]
    public void IdleSourceFailureReclaimsTheJustQueuedAction()
    {
        string source = Source();

        // 失败分支要按引用精确回收刚入队的那个动作。
        Assert.Contains("TryRemoveOne(action)", source, StringComparison.Ordinal);

        // 反面：不能再用"摘队首"的写法。那正是被修掉的那个缺陷——
        // 它摘掉的是最早排进来、可能已有 idle source 在等它的动作。
        //
        // **先把注释剥掉再查**：修掉的那行代码会出现在解释它的注释里
        // （"原实现是 `TryDequeue(out _)`"），直接搜会把注释当成违规。
        string code = StripComments(source);
        Assert.DoesNotContain("TryDequeue(out _)", code, StringComparison.Ordinal);
    }

    /// <summary>剥掉 <c>//</c> 行注释（本文件的注释里会引用被修掉的旧代码）。</summary>
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
    public void ReclaimComparesByReferenceNotValue()
    {
        // 委托的 `==` 是**值语义**（逐项比较调用列表），两个内容相同的 lambda 会相等。
        // 用它做判断会误删另一个动作——所以回收必须按引用相等。
        string source = Source();

        Assert.Contains("ReferenceEquals(item, action)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReclaimPreservesTheOtherActions()
    {
        // 取空再塞回时，其余动作必须原样入队（顺序保持）。否则"回收一个"会变成"丢掉一批"。
        string source = Source();

        Assert.Contains("MainThreadQueue.Enqueue(item)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReclaimIsScopedToOneAction()
    {
        // 只丢一个：`removed` 标志保证循环里最多丢弃一次。
        // 少了它，队列里若有同一个委托的多个副本（重复投递）会全部丢掉。
        string source = Source();

        Assert.Contains("bool removed = false", source, StringComparison.Ordinal);
        Assert.Contains("if (!removed && ReferenceEquals(item, action))", source, StringComparison.Ordinal);
    }
}
