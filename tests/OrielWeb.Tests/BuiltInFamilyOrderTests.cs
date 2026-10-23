using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建命令族的表结构与派发前提（R1）。
/// </summary>
/// <remarks>
/// <para>
/// 这些断言锁住两件事。它们原先只由 <c>HandleInvokeAsync</c> 里 10 段 if 的**代码位置**保证，
/// 没有任何测试能看出来：
/// </para>
/// <list type="number">
///   <item><description><c>win.*</c> / <c>tray.*</c> 是库保留前缀，必须最先解析；</description></item>
///   <item><description>只有 <c>path.*</c> 允许没有来源窗口，其余族缺窗口必须报 <c>no_source_window</c>。</description></item>
/// </list>
/// <para>
/// 第 2 条尤其值得钉住：新增族若误标 <see cref="IBuiltInCommandFamily.RequiresWindow"/>，
/// 症状是无窗口调用时拿到 <see cref="NullReferenceException"/>，而不是一条说得清的错误。
/// </para>
/// <para>
/// "应用命令与内建精确名重合"不在本文件覆盖：那种注册在程序集加载期就被
/// <c>OrielCommandRegistry.AddRouter</c> 拒掉了，见 <c>RegistryTests.AddRouter_BuiltInExactCommandName_ThrowsAtRegistration</c>。
/// </para>
/// </remarks>
public sealed class BuiltInFamilyOrderTests
{
    private static OrielCommandDispatcher NewDispatcher()
        => new([], null, TestGuards.AllowAll());

    [Fact]
    public void FamilyTable_OrderStartsWithReservedPrefixes()
    {
        string[] names = [.. OrielBuiltInCommandFamilies.All.Select(f => f.Name)];

        Assert.Equal(
            new[] { "win", "tray", "event", "path", "fs", "dialog", "platform", "app", "window", "menu" },
            names);
    }

    [Fact]
    public void FamilyTable_NamesAreUnique()
    {
        // 族名只用于诊断文案，但重名意味着有人复制粘贴时忘了改名——
        // 那通常也意味着整个族的适配器抄错了。
        string[] duplicates = [.. OrielBuiltInCommandFamilies.All
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        Assert.Empty(duplicates);
    }

    [Fact]
    public void FamilyTable_OnlyPathAllowsMissingWindow()
    {
        string[] withoutWindow = [.. OrielBuiltInCommandFamilies.All
            .Where(f => !f.RequiresWindow)
            .Select(f => f.Name)];

        Assert.Equal(new[] { "path" }, withoutWindow);
    }

    [Fact]
    public async Task PathJoin_WithoutWindow_Succeeds()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            NewDispatcher(),
            "path.join",
            new { paths = new[] { "a", "b" } },
            window: null);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.EndsWith(Path.Combine("a", "b"), reply.Value().GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FsRead_WithoutWindow_ReportsNoSourceWindow()
    {
        // 这条**不能**因为"参数是文件路径"就先走到 I/O：缺窗口的判定必须早于任何文件访问。
        var (reply, _) = await TestHarness.DispatchAsync(
            NewDispatcher(),
            "fs.readTextFile",
            new { path = "whatever.txt" },
            window: null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.NoSourceWindow, reply.RootElement.GetProperty("code").GetString());
    }
}
