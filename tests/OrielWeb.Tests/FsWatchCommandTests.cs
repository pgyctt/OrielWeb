using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>fs.watch</c> / <c>fs.unwatch</c>：参数校验、**每个路径都过 scope**、编号的取消语义。
/// </summary>
/// <remarks>
/// 这组测试重点不在"文件变了能不能收到事件"（那是
/// <see cref="FsWatcherRegistryTests"/> 的事，用真文件跑），而在命令这一层：
/// 参数形状、门禁、以及"只放行第一项路径"这类**看起来能跑、实际是漏**的实现。
/// </remarks>
public sealed class FsWatchCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oriel-watchcmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "oriel-watchout-" + Guid.NewGuid().ToString("N"));

    public FsWatchCommandTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (string dir in new[] { _root, _outside })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不该让测试变红
            }
        }

        GC.SuppressFinalize(this);
    }

    // ---- 注册与门禁 ----

    [Theory]
    [InlineData("fs.watch")]
    [InlineData("fs.unwatch")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInFsCommands.TryGet(name, out _), $"未注册：{name}");

    [Theory]
    [InlineData("fs.watch")]
    [InlineData("fs.unwatch")]
    public void WatchCommandsAreNotAlwaysAllowed(string name)
    {
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    // ---- 正常路径 ----

    [Fact]
    public void WatchReturnsAnIdAndUnwatchRemovesIt()
    {
        using OrielApp app = CreateApp();

        long id = Invoke<long>(app, "fs.watch", new { paths = new object[] { _root } });

        Assert.True(id > 0, "监视器编号应当从 1 开始");
        Assert.Equal(new[] { id }, app.FsWatchers.Active());

        Assert.True(Invoke<bool>(app, "fs.unwatch", new { id }));
        Assert.Empty(app.FsWatchers.Active());

        // 重复取消：编号已经不存在，返回 false 而不是抛错（与"关已关闭的句柄"不同——
        // 收尾时重复调用 unwatch 是常态，让它抛错只会逼调用方自己记账）
        Assert.False(Invoke<bool>(app, "fs.unwatch", new { id }));
    }

    [Fact]
    public void RecursiveAndDelayArePassedThrough()
    {
        using OrielApp app = CreateApp();

        long id = Invoke<long>(
            app,
            "fs.watch",
            new { paths = new object[] { _root }, recursive = true, delayMs = 250 });

        Assert.Equal(new[] { id }, app.FsWatchers.Active());

        Invoke<bool>(app, "fs.unwatch", new { id });
    }

    [Fact]
    public void DelayMsZeroIsAccepted()
    {
        // 0 与"没给"在宿主侧都是 immediate：给 0 的调用方想表达的是"不要窗口"，
        // 而不是"给一个 0 毫秒的窗口然后抱怨它没合并"
        using OrielApp app = CreateApp();

        long id = Invoke<long>(app, "fs.watch", new { paths = new object[] { _root }, delayMs = 0 });
        Assert.True(id > 0);
        Invoke<bool>(app, "fs.unwatch", new { id });
    }

    // ---- 路径 scope：**每一**项都要过 ----

    [Fact]
    public void EveryPathMustBeInsideScope()
    {
        // 只校验第一项是一个"能跑但漏"的实现：第二条路径就成了搭便车的
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = new object[] { _root, _outside } }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);

        // 整个调用失败，不留"半套监视"
        Assert.Empty(app.FsWatchers.Active());
    }

    [Fact]
    public void PathTraversalIsRejected()
    {
        using OrielApp app = CreateApp();
        string escaping = Path.Combine(_root, "..", Path.GetFileName(_outside));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = new object[] { escaping } }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
        Assert.Empty(app.FsWatchers.Active());
    }

    [Fact]
    public void MissingPathReportsInvalidArgument()
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = new object[] { Path.Combine(_root, "nope") } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("路径不存在", ex.Message, StringComparison.Ordinal);
    }

    // ---- 参数形状 ----

    [Fact]
    public void PathsMustBeAnArray()
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = _root }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("字符串数组", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPathsIsRejected()
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = Array.Empty<object>() }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void MissingPathsReportsMissingArgument()
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { recursive = true }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void NonStringPathIsRejected()
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = new object[] { 42 } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("paths[0]", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void InvalidDelayIsRejected(double delayMs)
    {
        using OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.watch", new { paths = new object[] { _root }, delayMs }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Empty(app.FsWatchers.Active());
    }

    [Fact]
    public void UnwatchUnknownIdReturnsFalse()
    {
        using OrielApp app = CreateApp();

        Assert.False(Invoke<bool>(app, "fs.unwatch", new { id = 9999L }));
    }

    // ---- 夹具 ----

    private OrielApp CreateApp()
        => Oriel.CreateBuilder()
            .UseCapabilities(c => c.Allow("fs.*", s => s.AllowPath(_root.Replace('\\', '/') + "/**")))
            .Build();

    private static T Invoke<T>(OrielApp app, string command, object args)
    {
        Assert.True(OrielBuiltInFsCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        return (T)handler(window, command, doc.RootElement)!;
    }
}
