using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建 <c>dialog.*</c> 命令不得阻塞后续 IPC 分发。
/// </summary>
/// <remarks>
/// <para>
/// 回归背景（2026-11-26 现场报告）：0.6.1 的 <c>dialog.open</c> / <c>dialog.save</c> 在 UI 线程上
/// 同步调用原生文件对话框。对话框是模态阻塞调用，UI 线程整个期间停在那一帧里——
/// 实测保存对话框开 29 秒，期间 <b>零条</b>命令被执行，关闭后 140+ 条积压命令在 5ms 内涌入。
/// </para>
/// <para>
/// 修法分两层：族表支持异步通道、<c>dialog.*</c> 只走异步；Windows 后端再把原生调用整个挪到
/// 专用 STA 线程（见 <c>Win32DialogThread</c>）。本文件锁的是**分发层**那一半——
/// 它不依赖任何真实对话框，因此能在无头 CI 上跑。
/// </para>
/// </remarks>
public sealed class DialogNonBlockingTests
{
    private static OrielCommandDispatcher NewDispatcher()
        => new([], null, TestGuards.AllowAll());

    /// <summary>发起一次分发并立刻返回其 Task（不等命令结束），用于模拟"命令还在飞"。</summary>
    // 测试专用反射序列化（匿名对象作参数）。测试程序集不参与 Native AOT 发布，
    // 因此在此抑制，而不是把 RequiresUnreferencedCode/RequiresDynamicCode 沿调用链传播给每个用例。
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026", Justification = "测试专用反射序列化，测试程序集不参与 AOT 发布。")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT", "IL3050", Justification = "测试专用反射序列化，测试程序集不参与 AOT 发布。")]
    private static async Task<JsonDocument> DispatchWithoutAwaitingAsync(
        OrielCommandDispatcher dispatcher, string name, object? args, int id, IOrielWindowControl window)
    {
        var sink = new TestSink();
        var argsJson = args is null ? "null" : JsonSerializer.Serialize(args);
        string message = $$"""
            { "__oriel": "invoke", "id": {{id}}, "name": "{{name}}", "args": {{argsJson}}, "token": "{{dispatcher.Guard.Token}}" }
            """;
        using var doc = JsonDocument.Parse(message);
        await dispatcher.HandleInvokeAsync(doc.RootElement.Clone(), sink, TestHarness.TrustedUrl, window);
        Assert.Single(sink.Replies);
        return JsonDocument.Parse(sink.Replies[0]);
    }

    [Fact]
    public void DialogFamily_IsAsyncOnly()
    {
        // 锁死"同步通道里不能再出现 dialog.*"：只要有人把某个 dialog 命令重新挂回同步族，
        // UI 线程又会被模态对话框堵住，而那是本批修复的核心目标。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();
        IBuiltInCommandFamily dialog = OrielBuiltInCommandFamilies.All.Single(f => f.Name == "dialog");

        Assert.False(dialog.TryGet("dialog.open", window, out _));
        Assert.False(dialog.TryGet("dialog.save", window, out _));
        Assert.False(dialog.TryGet("dialog.message", window, out _));

        Assert.True(dialog.TryGetAsync("dialog.open", window, out _));
        Assert.True(dialog.TryGetAsync("dialog.save", window, out _));
        Assert.True(dialog.TryGetAsync("dialog.message", window, out _));
    }

    [Fact]
    public async Task DialogSave_WhileOpen_DoesNotBlockOtherCommands()
    {
        // 用门控 Task 模拟"对话框开着"：它一直不完成，直到测试主动放行。
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new BuiltInWindowApiCommandTests.RecordingWindow
        {
            SaveFileDialogAsyncHook = () => gate.Task,
        };

        OrielCommandDispatcher dispatcher = NewDispatcher();

        // 第一条：dialog.save 挂在对话框上（不 await，模拟"用户还没关"）
        Task<JsonDocument> dialogTask = DispatchWithoutAwaitingAsync(
            dispatcher, "dialog.save", new { options = new { title = "probe" } }, id: 1, window);

        Assert.False(dialogTask.IsCompleted);

        // 第二条：对话框仍开着时，另一条命令必须能立刻拿到回执。
        // 修复前这里会一直等 dialog.save 返回（Windows 上真机表现就是"排队到关闭"）。
        var (other, _) = await TestHarness.DispatchAsync(
            dispatcher, "path.join", new { paths = new[] { "a", "b" } }, id: 2, window: null);

        Assert.True(other.RootElement.GetProperty("ok").GetBoolean());
        Assert.EndsWith(Path.Combine("a", "b"), other.Value().GetString(), StringComparison.Ordinal);

        // dialog.save 仍然在飞——证明上面那条是"并行"通过的，不是靠第一条先失败腾出来的
        Assert.False(dialogTask.IsCompleted);

        gate.SetResult("/tmp/probe.txt");
        using JsonDocument reply = await dialogTask;
        Assert.Equal("/tmp/probe.txt", reply.Value().GetString());
    }

    [Fact]
    public async Task DialogMessage_WhileOpen_DoesNotBlockOtherCommands()
    {
        // dialog.message 走的是同一个异步族，单独再钉一条：三个命令都很容易在后续改动里被拆散。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        var gate = new TaskCompletionSource<MessageResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.MessageDialogAsyncHook = () => gate.Task;

        OrielCommandDispatcher dispatcher = NewDispatcher();

        Task<JsonDocument> messageTask = DispatchWithoutAwaitingAsync(
            dispatcher, "dialog.message", new { message = "继续吗？" }, id: 1, window);

        Assert.False(messageTask.IsCompleted);

        var (other, _) = await TestHarness.DispatchAsync(
            dispatcher, "path.join", new { paths = new[] { "x" } }, id: 2, window: null);

        Assert.True(other.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(messageTask.IsCompleted);

        gate.SetResult(MessageResult.Yes);
        using JsonDocument reply = await messageTask;
        Assert.Equal("yes", reply.Value().GetString());
    }
}
