using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>fs.*</c> 命令：两道关（命令授权 + 路径 scope）与归一顺序。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试的重点**不是**"读写文件能不能成"（那是 BCL 的事），而是
/// **scope 会不会被绕过**。所以每条写命令都试一遍 <c>..</c> 逃逸，
/// 每个路径参数（含 <c>rename</c>/<c>copyFile</c> 的目标）都单独验。
/// </para>
/// <para>
/// 为什么这么写：scope 检查里"先归一还是先判定"这一处顺序错了，
/// 功能测试**全都会通过**——只有专门构造逃逸路径的测试才会红。
/// 而那个顺序错了等于 scope 形同虚设。
/// </para>
/// </remarks>
public sealed class FsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oriel-fs-" + Guid.NewGuid().ToString("N"));

    public FsCommandTests()
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
            // 测试清理失败不该让测试变红
        }

        GC.SuppressFinalize(this);
    }

    // ---- 正常路径 ----

    [Fact]
    public void WriteThenReadTextRoundTrips()
    {
        string file = Path.Combine(_root, "a.txt");

        Invoke("fs.writeTextFile", new { path = file, data = "你好，世界" });

        Assert.Equal("你好，世界", Invoke<string>("fs.readTextFile", new { path = file }));
    }

    [Fact]
    public void WriteTextDoesNotEmitABom()
    {
        // **实测踩到的缺陷**：File.WriteAllText(..., Encoding.UTF8) 会写 BOM（EF BB BF），
        // 于是写 5 个汉字得到 18 字节而不是 15——而调用方算的是字符数乘 3。
        // 更麻烦的是下游：BOM 会让 JSON/CSV/shell 解析器把首字节当成内容的一部分。
        // Tauri 的 writeTextFile 走 String::into_bytes()，**不写 BOM**，所以这里对齐它。
        string file = Path.Combine(_root, "bom.txt");

        Invoke("fs.writeTextFile", new { path = file, data = "你好，世界" });

        byte[] raw = File.ReadAllBytes(file);

        Assert.Equal(15, raw.Length);   // 5 个字符 × 3 字节，不含 BOM 的 3 字节
        Assert.False(
            raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "不该有 UTF-8 BOM");
        Assert.Equal(15, Invoke<long>("fs.size", new { path = file }));
    }

    [Fact]
    public void ReadTextAcceptsAFileWrittenWithABom()
    {
        // 读的时候不必操心 BOM：File.ReadAllText 会自己探测并去掉。
        // 所以"别人写的带 BOM 文件"也能正确读出来——这条钉住那个不对称是有意的
        // （写不带、读兼容），而不是漏了一边。
        string file = Path.Combine(_root, "with-bom.txt");
        File.WriteAllText(file, "内容", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("内容", Invoke<string>("fs.readTextFile", new { path = file }));
    }

    [Fact]
    public void WriteCreatesParentDirectories()
    {
        // 不自动建父目录的话页面每次写都要先 mkdir，而漏建的症状是
        // DirectoryNotFoundException——报错说"目录不存在"，而调用方以为自己在写文件
        string file = Path.Combine(_root, "deep", "nested", "a.txt");

        Invoke("fs.writeTextFile", new { path = file, data = "x" });

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ByteRoundTrip()
    {
        string file = Path.Combine(_root, "b.bin");
        byte[] payload = [0, 1, 127, 128, 255];

        Invoke("fs.writeFile", new { path = file, data = payload });

        Assert.Equal(payload, Invoke<byte[]>("fs.readFile", new { path = file }));
    }

    [Fact]
    public void ExistsSizeStat()
    {
        string file = Path.Combine(_root, "c.txt");
        File.WriteAllText(file, "12345");

        Assert.True(Invoke<bool>("fs.exists", new { path = file }));
        Assert.False(Invoke<bool>("fs.exists", new { path = Path.Combine(_root, "nope") }));

        Assert.Equal(5, Invoke<long>("fs.size", new { path = file }));

        // JsonElement 是结构体（分析器会对 Assert.NotNull 报 xUnit2002），所以直接断言字段
        JsonElement stat = Invoke<JsonElement>("fs.stat", new { path = file });
        Assert.True(stat.GetProperty("isFile").GetBoolean());
        Assert.Equal(5, stat.GetProperty("size").GetInt64());
    }

    [Fact]
    public void ReadDirListsEntries()
    {
        File.WriteAllText(Path.Combine(_root, "f1.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        JsonElement entries = Invoke<JsonElement>("fs.readDir", new { path = _root });
        string json = entries.GetRawText();

        // 字段名必须是 **camelCase**（与 Tauri 的 DirEntry 一致，页面侧写 entry.name）。
        // STJ 默认写 PascalCase，所以实现里是手写 JSON——这条断言就是钉这件事的。
        Assert.Contains("f1.txt", json, StringComparison.Ordinal);
        Assert.Contains("sub", json, StringComparison.Ordinal);
        Assert.Contains("\"isDirectory\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IsDirectory\"", json, StringComparison.Ordinal);

        JsonElement fileEntry = entries.EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "f1.txt");
        Assert.True(fileEntry.GetProperty("isFile").GetBoolean());
        Assert.False(fileEntry.GetProperty("isDirectory").GetBoolean());
    }

    [Fact]
    public void StatUsesCamelCaseAndMillisecondTimestamps()
    {
        string file = Path.Combine(_root, "s.txt");
        File.WriteAllText(file, "12345");

        JsonElement stat = Invoke<JsonElement>("fs.stat", new { path = file });
        string json = stat.GetRawText();

        Assert.DoesNotContain("\"IsFile\"", json, StringComparison.Ordinal);
        Assert.True(stat.GetProperty("isFile").GetBoolean());
        Assert.Equal(5, stat.GetProperty("size").GetInt64());

        // 毫秒时间戳，取 UTC（本地时区参与会让同一份数据在不同机器上不一致）
        long mtime = stat.GetProperty("mtime").GetInt64();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(mtime, now - 3600_000, now + 60_000);
    }

    [Fact]
    public void MkdirRemoveRenameCopy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));

        Invoke("fs.rename", new { oldPath = Path.Combine(_root, "d"), newPath = Path.Combine(_root, "e") });
        Assert.True(Directory.Exists(Path.Combine(_root, "e")));

        File.WriteAllText(Path.Combine(_root, "x.txt"), "x");
        Invoke("fs.copyFile", new { oldPath = Path.Combine(_root, "x.txt"), newPath = Path.Combine(_root, "y.txt") });
        Assert.True(File.Exists(Path.Combine(_root, "y.txt")));

        Invoke("fs.remove", new { path = Path.Combine(_root, "y.txt") });
        Assert.False(File.Exists(Path.Combine(_root, "y.txt")));

        Directory.CreateDirectory(Path.Combine(_root, "tree", "inner"));
        Invoke("fs.remove", new { path = Path.Combine(_root, "tree"), recursive = true });
        Assert.False(Directory.Exists(Path.Combine(_root, "tree")));
    }

    [Fact]
    public void TruncateShrinksTheFile()
    {
        string file = Path.Combine(_root, "t.txt");
        File.WriteAllText(file, "0123456789");

        Invoke("fs.truncate", new { path = file, len = 4 });

        Assert.Equal("0123", File.ReadAllText(file));
    }

    [Fact]
    public void MkdirNonRecursiveOnExistingDirectoryThrows()
    {
        // 与 Tauri / Node 一致：非递归且已存在 → 报错；递归时已存在是正常的
        string dir = Path.Combine(_root, "dup");
        Directory.CreateDirectory(dir);

        Assert.ThrowsAny<Exception>(() => Invoke("fs.mkdir", new { path = dir }));

        // 递归形式不该抛
        Invoke("fs.mkdir", new { path = dir, recursive = true });
    }

    // ---- scope：逃逸必须被挡 ----

    [Theory]
    [InlineData("fs.readTextFile")]
    [InlineData("fs.readFile")]
    [InlineData("fs.exists")]
    [InlineData("fs.size")]
    [InlineData("fs.stat")]
    [InlineData("fs.lstat")]
    [InlineData("fs.readDir")]
    [InlineData("fs.writeTextFile")]
    [InlineData("fs.writeFile")]
    [InlineData("fs.mkdir")]
    [InlineData("fs.remove")]
    [InlineData("fs.truncate")]
    public void EscapeViaDotDotIsBlockedOnEveryCommand(string command)
    {
        // **这一条是整组测试的核心**。scope 声明的是 "$ROOT/**"，而这里给的路径
        // 字面上也在 $ROOT 之内（以 $ROOT 开头），但归一之后跑到了外面。
        // 若实现是"先判 scope 再归一"，功能测试全绿而这条会红——那意味着 scope 形同虚设。
        string escaping = Path.Combine(_root, "..", "outside.txt");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke(command, ArgsFor(command, escaping)));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void RenameBlockedOnBothSourceAndTarget()
    {
        // 只查源不查目标的话，页面能把 $ROOT 里的文件"搬"到 scope 之外（那是一次写）
        string inside = Path.Combine(_root, "a.txt");
        File.WriteAllText(inside, "x");
        string outside = Path.Combine(_root, "..", "moved.txt");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.rename", new { oldPath = inside, newPath = outside }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void CopyFileBlockedOnBothSourceAndTarget()
    {
        string inside = Path.Combine(_root, "a.txt");
        File.WriteAllText(inside, "x");
        string outside = Path.Combine(_root, "..", "copied.txt");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.copyFile", new { oldPath = inside, newPath = outside }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void AbsolutePathOutsideScopeIsBlocked()
    {
        string other = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.readTextFile", new { path = other }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void CommandAllowedWithoutScopeDeniesEverything()
    {
        // 用"授权了命令但没给 scope"的应用配置
        OrielApp app = CreateAppExact(c => c.Allow("fs.readTextFile"));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "fs.readTextFile", new { path = Path.Combine(_root, "a.txt") }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
        Assert.Contains("没有声明任何路径 scope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthorizedCommandGetsNotAllowedNotPathNotAllowed()
    {
        // 两种失败要能分开：没声明命令 vs 声明了命令但路径不对。
        // 混成一个码的话，"我明明声明了 fs.readTextFile"会把人带向错的方向。
        OrielApp app = CreateAppExact(c => c.Allow("fs.writeTextFile", s => s.AllowPath(_root + "/**")));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "fs.readTextFile", new { path = Path.Combine(_root, "a.txt") }));

        Assert.Equal(OrielIpcErrorCodes.NotAllowed, ex.Code);
    }

    [Fact]
    public void DenyInsideScopeWins()
    {
        OrielApp app = CreateAppExact(c => c.Allow(
            "fs.readTextFile",
            s => s.AllowPath(_root + "/**").DenyPath(_root + "/secret/**")));

        string secret = Path.Combine(_root, "secret", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        File.WriteAllText(secret, "s");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "fs.readTextFile", new { path = secret }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    // ---- 参数校验 ----

    [Fact]
    public void EmptyPathIsRejected()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.readTextFile", new { path = "" }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void MissingPathIsRejected()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("fs.readTextFile", new { }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void OutOfRangeByteIsRejected(double value)
    {
        // 直接 (byte) 强转会把 300 变成 44，静默写坏数据；
        // 而"写进去的文件内容不对"是最难归因的一类故障（看起来像编码问题）
        string file = Path.Combine(_root, "z.bin");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.writeFile", new { path = file, data = new object[] { value } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.False(File.Exists(file), "参数校验失败时不该已经写出文件");
    }

    [Fact]
    public void NonStringDataIsRejected()
    {
        string file = Path.Combine(_root, "z.txt");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.writeTextFile", new { path = file, data = 42 }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void NegativeTruncateLengthIsRejected()
    {
        string file = Path.Combine(_root, "t2.txt");
        File.WriteAllText(file, "abc");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("fs.truncate", new { path = file, len = -1 }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void UnknownFsCommandIsNotHandled()
        => Assert.False(OrielBuiltInFsCommands.TryGet("fs.nonexistent", out _));

    [Fact]
    public void FsCommandsAreNotInAnyAlwaysAllowedList()
    {
        // fs 与 path 的**关键差别**：path 只做运算所以整族放行，fs 碰文件系统所以绝不放行。
        // 若哪天有人把 fs.* 加进某个"始终放行"名单，这条会立刻变红。
        foreach (string command in new[]
                 {
                     "fs.readTextFile", "fs.writeTextFile", "fs.readFile", "fs.writeFile",
                     "fs.exists", "fs.readDir", "fs.size", "fs.stat", "fs.lstat",
                     "fs.mkdir", "fs.remove", "fs.rename", "fs.copyFile", "fs.truncate",
                 })
        {
            Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(command), command);
            Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(command), command);
            Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(command), command);
        }
    }

    // ---- 夹具 ----

    /// <summary>
    /// 造一个应用：<c>fs.*</c> 全部授权，scope 是"测试临时目录之内"。
    /// </summary>
    /// <param name="configure">
    /// 追加配置。注意 <c>UseCapabilities</c> 是**追加**语义（多次 Allow 会叠加），
    /// 所以想测"授权了命令但没给 scope"这类场景不能用这个重载——用
    /// <see cref="CreateAppExact"/>，否则默认那条带 scope 的 <c>fs.*</c> 会盖过它。
    /// </param>
    private OrielApp CreateApp(Action<OrielCapabilityOptions>? configure = null)
        => CreateAppExact(c =>
        {
            c.Allow("fs.*", s => s.AllowPath(ScopeRoot()));
            configure?.Invoke(c);
        });

    /// <summary>造一个应用，能力配置**完全由调用方给**（没有默认的 <c>fs.*</c>）。</summary>
    private OrielApp CreateAppExact(Action<OrielCapabilityOptions> configure)
        => Oriel.CreateBuilder().UseCapabilities(configure).Build();

    private string ScopeRoot() => _root.Replace('\\', '/') + "/**";

    /// <summary>给一条命令构造一组"能用但会逃逸"的参数。</summary>
    private static object ArgsFor(string command, string escapingPath) => command switch
    {
        "fs.rename" or "fs.copyFile" => new { oldPath = escapingPath, newPath = escapingPath },
        "fs.writeTextFile" => new { path = escapingPath, data = "x" },
        "fs.writeFile" => new { path = escapingPath, data = new byte[] { 1 } },
        "fs.truncate" => new { path = escapingPath, len = 0 },
        "fs.mkdir" => new { path = escapingPath, recursive = true },
        _ => new { path = escapingPath },
    };

    private T Invoke<T>(string command, object args) => InvokeWith<T>(CreateApp(), command, args);

    private void Invoke(string command, object args) => InvokeWith(CreateApp(), command, args);

    private static T InvokeWith<T>(OrielApp app, string command, object args)
    {
        Assert.True(OrielBuiltInFsCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        return (T)handler(window, command, doc.RootElement)!;
    }

    private static void InvokeWith(OrielApp app, string command, object args)
        => InvokeWith<object?>(app, command, args);
}
