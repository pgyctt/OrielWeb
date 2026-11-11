using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 整读命令的体积闸与 BCL 异常净化（2026-11-11 评审 L1 / L2 余项）。
/// </summary>
/// <remarks>
/// <para>
/// <b>L1</b>：<c>fs.read</c> 一直有 8 MiB 的单次上限，而更重的 <c>fs.readFile</c> /
/// <c>fs.readTextFile</c>（读满整个文件**再序列化成 JSON**）没有闸——页面一句 invoke
/// 就能让宿主分配掉任意大的缓冲区。
/// </para>
/// <para>
/// <b>L2</b>：内建命令的异常原样透传是**有意取舍**（排障信息是页面唯一可见的线索），
/// 但 BCL 异常的 Message 由运行时拼装、常带宿主绝对路径——那等于把"某个路径是否存在"
/// 的探测通道开到错误消息里。所以只净化 BCL 那一类，<c>OrielIpcException</c> 照旧透传。
/// </para>
/// </remarks>
public sealed class FsReadLimitTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "oriel-readlimit-" + Guid.NewGuid().ToString("N"));

    public FsReadLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言；临时目录留给系统回收
        }
    }

    [Theory]
    [InlineData("fs.readFile")]
    [InlineData("fs.readTextFile")]
    public void OversizedFileIsRejectedWithGuidance(string command)
    {
        string big = Path.Combine(_root, "big.bin");
        using (FileStream stream = File.Create(big))
        {
            // 稀疏文件：瞬间造出 9 MiB，不用真的写 9 MiB 数据
            stream.SetLength(9L * 1024 * 1024);
        }

        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, command, new { path = big }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);

        // 文案要指向"怎么改"：分块读（而不是让调用方去猜上限从哪来）
        Assert.Contains("fs.read", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fs.readFile")]
    [InlineData("fs.readTextFile")]
    public void NormalSizedFileIsStillReadable(string command)
    {
        // 闸不能误伤正常用法
        string path = Path.Combine(_root, "small.txt");
        File.WriteAllText(path, new string('a', 4096));

        using OrielApp app = CreateApp();

        Assert.NotNull(InvokeWith(app, command, new { path = path }));
    }

    [Fact]
    public async Task BclFailureDoesNotLeakTheHostPath()
    {
        // 读一个"在 scope 内、但不存在"的文件 → FileNotFoundException（BCL），
        // 它的 Message 带宿主绝对路径。回执必须净化掉它。
        using OrielApp app = CreateApp();
        string missing = Path.Combine(_root, "missing-" + Guid.NewGuid().ToString("N") + ".txt");

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        var dispatcher = new OrielCommandDispatcher([], TestJsonContext.Default, app.Guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "fs.readFile", new { path = missing }, window: window);

        string error = reply.RootElement.GetProperty("error").GetString()!;

        Assert.DoesNotContain(_root, error, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, error, StringComparison.Ordinal);

        // 类型名保留：调用方仍能分辨"文件不存在"（FileNotFoundException）与"没有权限"
        Assert.Contains(nameof(FileNotFoundException), error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrielIpcExceptionStillPassesThroughVerbatim()
    {
        // 净化的边界要清楚：库自己抛的文案是给调用方看的，不能被一起抹掉
        using OrielApp app = CreateApp();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        var dispatcher = new OrielCommandDispatcher([], TestJsonContext.Default, app.Guard);

        string outside = Path.Combine(Path.GetTempPath(), "oriel-readlimit-out-" + Guid.NewGuid().ToString("N"), "x.txt");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "fs.readFile", new { path = outside }, window: window);

        string error = reply.RootElement.GetProperty("error").GetString()!;

        // path_not_allowed 的原因为"路径不在任何允许的模式内…"——那段文案必须还在
        Assert.Contains("允许的模式", error, StringComparison.Ordinal);
    }

    private OrielApp CreateApp()
        => Oriel.CreateBuilder()
            .UseCapabilities(c => c.Allow("fs.*", s => s.AllowPath(_root.Replace('\\', '/') + "/**")))
            .Build();

    private static object? InvokeWith(OrielApp app, string command, object args)
    {
        Assert.True(OrielBuiltInFsCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        return handler(window, command, doc.RootElement);
    }
}
