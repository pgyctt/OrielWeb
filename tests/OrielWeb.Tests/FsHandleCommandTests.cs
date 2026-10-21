using System.Text;
using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 句柄式 <c>fs.*</c> 命令（B17：open / create / read / write / seek / fstat / ftruncate / close）。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试钉两件事：
/// </para>
/// <list type="number">
///   <item><description><b>句柄语义</b>：读写位置真的推进、EOF 真的给 null、
///     重复 close 真的报错、选项冲突真的在打开前就被拒。</description></item>
///   <item><description><b>scope 仍然在 open/create 上生效</b>：句柄化不能变成一条
///     绕过路径授权的路——`fs.open` 拿到 id 之后不再判路径（与 Tauri 一致），
///     所以"打开那一下"必须判。</description></item>
/// </list>
/// </remarks>
public sealed class FsHandleCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oriel-fsh-" + Guid.NewGuid().ToString("N"));

    public FsHandleCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不该让测试变红
        }

        GC.SuppressFinalize(this);
    }

    // ---- 基本读写 ----

    [Fact]
    public void CreateWriteSeekReadRoundTrips()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "a.bin");

        long id = Invoke<long>(app, "fs.create", new { path = file });
        Assert.Equal(3, Invoke<int>(app, "fs.write", new { id, data = new byte[] { 1, 2, 3 } }));
        Assert.Equal(0, Invoke<long>(app, "fs.seek", new { id, offset = 0 }));

        byte[] read = Invoke<byte[]>(app, "fs.read", new { id, length = 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, read);

        Invoke(app, "fs.close", new { id });
    }

    [Fact]
    public void ReadAdvancesThePositionAndGivesNullAtEof()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "b.bin");
        File.WriteAllBytes(file, [10, 11, 12, 13]);

        long id = Invoke<long>(app, "fs.open", new { path = file, read = true });

        byte[] first = Invoke<byte[]>(app, "fs.read", new { id, length = 2 });
        Assert.Equal(new byte[] { 10, 11 }, first);

        byte[] second = Invoke<byte[]>(app, "fs.read", new { id, length = 2 });
        Assert.Equal(new byte[] { 12, 13 }, second);

        // EOF 必须是 null，而不是空数组：两者在页面侧语义不同（null = 读完了）。
        Assert.Null(Invoke<object?>(app, "fs.read", new { id, length = 2 }));

        Invoke(app, "fs.close", new { id });
    }

    [Fact]
    public void WriteTruncatesExistingContentWhenCreateIsUsed()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "c.txt");
        File.WriteAllText(file, "旧内容旧内容");

        long id = Invoke<long>(app, "fs.create", new { path = file });
        Invoke(app, "fs.write", new { id, data = Encoding.UTF8.GetBytes("新") });
        Invoke(app, "fs.close", new { id });

        // create 必须截断已有内容
        Assert.Equal("新", File.ReadAllText(file));
    }

    [Fact]
    public void AppendModeWritesAtTheEnd()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "d.txt");
        File.WriteAllText(file, "abc");

        long id = Invoke<long>(app, "fs.open", new { path = file, append = true });
        Invoke(app, "fs.write", new { id, data = Encoding.UTF8.GetBytes("def") });
        Invoke(app, "fs.close", new { id });

        Assert.Equal("abcdef", File.ReadAllText(file));
    }

    [Fact]
    public void CreateNewFailsWhenTheFileExists()
    {
        // createNew 的语义是"必须不存在"——已存在时必须报错而不是复用已有内容。
        // 那条规则只有专门测才拦得住（正常路径永远造不出这个冲突）。
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "exists.txt");
        File.WriteAllText(file, "已有");

        Assert.ThrowsAny<IOException>(
            () => Invoke<long>(app, "fs.open", new { path = file, write = true, createNew = true }));
    }

    // ---- 选项校验（在打开文件之前）----

    [Theory]
    [InlineData("{\"path\":\"FILE\"}", "read / write / append")]
    [InlineData("{\"path\":\"FILE\",\"truncate\":true}", "truncate 需要写权限")]
    [InlineData("{\"path\":\"FILE\",\"create\":true,\"createNew\":true,\"write\":true}", "createNew 与 create")]
    public void InvalidOptionCombinationsAreRejectedBeforeTouchingTheFile(string argsJson, string expected)
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "opt.txt").Replace("\\", "\\\\");

        using var doc = JsonDocument.Parse(argsJson.Replace("FILE", file));

        Assert.True(OrielBuiltInFsCommands.TryGet("fs.open", out var handler));
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => handler(window, "fs.open", doc.RootElement));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.True(
            ex.Message.Contains(expected, StringComparison.Ordinal),
            $"错误文案应包含「{expected}」，实际是「{ex.Message}」");
    }

    // ---- 元信息与截断 ----

    [Fact]
    public void FstatReportsTheHandleLengthAndFileFlag()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "stat.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);

        long id = Invoke<long>(app, "fs.open", new { path = file, read = true });
        JsonElement stat = Invoke<JsonElement>(app, "fs.fstat", new { id });

        Assert.True(stat.GetProperty("isFile").GetBoolean());
        Assert.False(stat.GetProperty("isDirectory").GetBoolean());
        Assert.Equal(5, stat.GetProperty("size").GetInt64());
        Assert.True(stat.GetProperty("mtime").GetInt64() > 0);

        Invoke(app, "fs.close", new { id });
    }

    [Fact]
    public void FtruncateWithoutLengthTruncatesAtTheCurrentPosition()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "trunc.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        long id = Invoke<long>(app, "fs.open", new { path = file, write = true });
        Invoke<long>(app, "fs.seek", new { id, offset = 2 });
        Invoke(app, "fs.ftruncate", new { id });
        Invoke(app, "fs.close", new { id });

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void FtruncateWithZeroEmptiesTheFile()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "trunc0.bin");
        File.WriteAllBytes(file, [1, 2, 3]);

        long id = Invoke<long>(app, "fs.open", new { path = file, write = true });
        Invoke(app, "fs.ftruncate", new { id, len = 0 });
        Invoke(app, "fs.close", new { id });

        Assert.Empty(File.ReadAllBytes(file));
    }

    // ---- 句柄生命周期 ----

    [Fact]
    public void ClosingTwiceThrows()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "close.txt");
        File.WriteAllText(file, "x");

        long id = Invoke<long>(app, "fs.open", new { path = file, read = true });
        Invoke(app, "fs.close", new { id });

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke(app, "fs.close", new { id }));
        Assert.Contains("不存在或已关闭", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingAClosedHandleThrows()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "closed-read.txt");
        File.WriteAllBytes(file, [1]);

        long id = Invoke<long>(app, "fs.open", new { path = file, read = true });
        Invoke(app, "fs.close", new { id });

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<byte[]>(app, "fs.read", new { id, length = 1 }));
        Assert.Contains("不存在或已关闭", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingAnUnknownIdThrows()
    {
        OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<byte[]>(app, "fs.read", new { id = 9999, length = 1 }));
        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void ReadLengthIsCapped()
    {
        // 8 MiB 上限：挡住"一句 invoke 分配掉进程内存"，同时也让错误文案说清边界。
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "cap.bin");
        File.WriteAllBytes(file, [1]);

        long id = Invoke<long>(app, "fs.open", new { path = file, read = true });

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<byte[]>(app, "fs.read", new { id, length = 9 * 1024 * 1024 }));
        Assert.Contains("length 必须在", ex.Message, StringComparison.Ordinal);

        Invoke(app, "fs.close", new { id });
    }

    [Fact]
    public void DisposeClosesHandlesOpenedByTheApp()
    {
        // 应用退出时未 close 的句柄要落盘并释放——否则 Windows 上文件被占着，
        // 下一次启动写同一个文件会失败。
        string file = Path.Combine(_root, "dispose.txt");
        OrielApp app = CreateApp();
        long id = Invoke<long>(app, "fs.create", new { path = file });
        Invoke(app, "fs.write", new { id, data = new byte[] { 42 } });

        app.Dispose();

        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(file));
        Assert.Throws<OrielIpcException>(() => Invoke<byte[]>(app, "fs.read", new { id, length = 1 }));
    }

    // ---- scope 仍然生效 ----

    [Fact]
    public void OpenOutsideScopeIsRejected()
    {
        // 句柄化最容易出的事故：open 时忘了判 scope，后面的读写就全都"合法"了。
        OrielApp app = CreateApp();
        string outside = Path.Combine(Path.GetTempPath(), "oriel-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "should not be readable");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.open", new { path = outside, read = true }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);

        File.Delete(outside);
    }

    [Fact]
    public void OpenRejectsEscapingPaths()
    {
        OrielApp app = CreateApp();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<long>(app, "fs.open", new { path = Path.Combine(_root, "..", "escape.txt"), read = true }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    // ---- readTextFileLines ----

    [Fact]
    public void ReadTextFileLinesSplitsLfAndCrlfAndDropsTheTrailingNewline()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "lines.txt");
        File.WriteAllText(file, "a\r\nb\n\nc\n");

        string[] lines = Invoke<string[]>(app, "fs.readTextFileLines", new { path = file });

        Assert.Equal(["a", "b", "", "c"], lines);
    }

    [Fact]
    public void ReadTextFileLinesOnEmptyFileGivesNoLines()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "empty.txt");
        File.WriteAllText(file, "");

        Assert.Empty(Invoke<string[]>(app, "fs.readTextFileLines", new { path = file }));
    }

    [Fact]
    public void ReadTextFileLinesWithoutTrailingNewlineKeepsTheLastLine()
    {
        OrielApp app = CreateApp();
        string file = Path.Combine(_root, "nolf.txt");
        File.WriteAllText(file, "x\ny");

        Assert.Equal(["x", "y"], Invoke<string[]>(app, "fs.readTextFileLines", new { path = file }));
    }

    // ---- 装具 ----

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

    private static void Invoke(OrielApp app, string command, object args)
        => Invoke<object?>(app, command, args);
}
