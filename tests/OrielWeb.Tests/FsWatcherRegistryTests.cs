using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <see cref="OrielFsWatcherRegistry"/>：事件归类、debounce 合并、取消与释放。
/// </summary>
/// <remarks>
/// <para>
/// 这里有两类测试：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>纯函数</b>（<see cref="OrielFsWatcherRegistry.BuildPayload"/>）：把原生事件翻成
///   Tauri 形状的 <c>WatchEvent</c>。归类错了页面侧会静默分支到错的代码上
///   （比如把 create 当成 modify），而那种错在日志里看不出来。
///   </description></item>
///   <item><description>
///   <b>真监视</b>：<see cref="FileSystemWatcher"/> 在三个平台落到不同设施
///   （ReadDirectoryChangesW / inotify / FSEvents），所以"到底收不收得到"必须真跑一遍。
///   等待时间给得比较宽（macOS 的 FSEvents 有合并延迟），避免在 CI 上偶发红。
///   </description></item>
/// </list>
/// </remarks>
public sealed class FsWatcherRegistryTests : IDisposable
{
    /// <summary>等待事件的上限。macOS 的 FSEvents 合并延迟可达 1 秒级，留足余量。</summary>
    private const int WaitMs = 15000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oriel-watch-" + Guid.NewGuid().ToString("N"));

    public FsWatcherRegistryTests() => Directory.CreateDirectory(_root);

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

    // ---- 纯函数：事件归类 ----

    [Fact]
    public void ChangedBecomesModifyData()
    {
        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Changed, "/tmp/a.txt", null));

        JsonElement root = doc.RootElement;
        Assert.Equal("data", root.GetProperty("type").GetProperty("modify").GetProperty("kind").GetString());
        Assert.Equal("any", root.GetProperty("type").GetProperty("modify").GetProperty("mode").GetString());
        Assert.Equal("/tmp/a.txt", root.GetProperty("paths")[0].GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("attrs").ValueKind);
        Assert.Empty(root.GetProperty("attrs").EnumerateObject());
    }

    [Fact]
    public void CreatedFileBecomesCreateFile()
    {
        string file = Path.Combine(_root, "created.txt");
        File.WriteAllText(file, "x");

        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Created, file, null));

        Assert.Equal("file", doc.RootElement.GetProperty("type").GetProperty("create").GetProperty("kind").GetString());
    }

    [Fact]
    public void CreatedDirectoryBecomesCreateFolder()
    {
        string dir = Path.Combine(_root, "created-dir");
        Directory.CreateDirectory(dir);

        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Created, dir, null));

        Assert.Equal("folder", doc.RootElement.GetProperty("type").GetProperty("create").GetProperty("kind").GetString());
    }

    [Fact]
    public void CreatedThatAlreadyVanishedBecomesCreateAny()
    {
        // 瞬生瞬灭（事件到达时又没了）：stat 不出来就报 any，而不是猜一个
        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(
                WatcherChangeTypes.Created, Path.Combine(_root, "ghost.txt"), null));

        Assert.Equal("any", doc.RootElement.GetProperty("type").GetProperty("create").GetProperty("kind").GetString());
    }

    [Fact]
    public void DeletedBecomesRemoveAny()
    {
        // 已经删掉的东西没法再 stat 判断原来是文件还是目录——只能报 any
        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Deleted, "/tmp/gone.txt", null));

        Assert.Equal("any", doc.RootElement.GetProperty("type").GetProperty("remove").GetProperty("kind").GetString());
    }

    [Fact]
    public void RenamedCarriesBothPaths()
    {
        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Renamed, "/tmp/new.txt", "/tmp/old.txt"));

        JsonElement type = doc.RootElement.GetProperty("type").GetProperty("modify");
        Assert.Equal("rename", type.GetProperty("kind").GetString());
        Assert.Equal("both", type.GetProperty("mode").GetString());

        JsonElement paths = doc.RootElement.GetProperty("paths");
        Assert.Equal(2, paths.GetArrayLength());
        Assert.Equal("/tmp/old.txt", paths[0].GetString());
        Assert.Equal("/tmp/new.txt", paths[1].GetString());
    }

    [Fact]
    public void PayloadEscapesAwkwardPaths()
    {
        // 路径里带引号/反斜杠/换行时，JSON 必须是合法的——否则整条事件在页面上解析失败
        const string awkward = "C:\\a \"quoted\"\nline.txt";

        using JsonDocument doc = JsonDocument.Parse(
            OrielFsWatcherRegistry.BuildPayload(WatcherChangeTypes.Changed, awkward, null));

        Assert.Equal(awkward, doc.RootElement.GetProperty("paths")[0].GetString());
    }

    // ---- 真监视 ----

    [Fact]
    public void ImmediateWatchDeliversCreateEventToTheCallingWindow()
    {
        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: null);

        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id && e.Payload.Contains("\"create\""))),
            $"没收到 create 事件：{harness.Dump()}");

        Assert.All(harness.Snapshot(), e => Assert.Equal("main", e.Label));
    }

    [Fact]
    public void RenameDeliversBothPaths()
    {
        string before = Path.Combine(_root, "before.txt");
        string after = Path.Combine(_root, "after.txt");
        File.WriteAllText(before, "x");

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: null);

        File.Move(before, after);

        Assert.True(
            harness.WaitFor(list => list.Any(e =>
                e.Id == id
                && e.Payload.Contains("\"rename\"")
                && e.Payload.Contains("before.txt")
                && e.Payload.Contains("after.txt"))),
            $"没收到带两个路径的 rename 事件：{harness.Dump()}");
    }

    [Fact]
    public void WatchingASingleFileIgnoresSiblings()
    {
        string watched = Path.Combine(_root, "only.txt");
        File.WriteAllText(watched, "x");

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [watched], recursive: false, delayMs: null);

        File.WriteAllText(Path.Combine(_root, "other.txt"), "x");
        File.AppendAllText(watched, "y");

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id && e.Payload.Contains("only.txt"))),
            $"盯着的那一个文件的变化没收到：{harness.Dump()}");

        Assert.DoesNotContain(
            harness.Snapshot(),
            e => e.Id == id && e.Payload.Contains("other.txt"));
    }

    [Fact]
    public void RecursiveWatchSeesSubdirectories()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: true, delayMs: null);

        File.WriteAllText(Path.Combine(sub, "deep.txt"), "x");

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id && e.Payload.Contains("deep.txt"))),
            $"递归监视没收到子目录里的变化：{harness.Dump()}");
    }

    [Fact]
    public void NonRecursiveWatchIgnoresSubdirectories()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: null);

        File.WriteAllText(Path.Combine(sub, "deep.txt"), "x");

        // 正对照：根目录里的变化必须能收到——否则"没收到子目录事件"可能只是监视器还没就绪
        File.WriteAllText(Path.Combine(_root, "shallow.txt"), "x");
        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id && e.Payload.Contains("shallow.txt"))),
            $"根目录的变化都没收到，这个测试的否定结论不成立：{harness.Dump()}");

        Assert.DoesNotContain(
            harness.Snapshot(),
            e => e.Id == id && e.Payload.Contains("deep.txt"));
    }

    [Fact]
    public void MultiplePathsAreWatchedTogether()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [a, b], recursive: false, delayMs: null);

        File.WriteAllText(Path.Combine(b, "in-b.txt"), "x");

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id && e.Payload.Contains("in-b.txt"))),
            $"第二个路径的变化没收到：{harness.Dump()}");
    }

    [Fact]
    public void DebounceWaitsForTheWindowBeforeDelivering()
    {
        string file = Path.Combine(_root, "debounced.txt");

        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: 600);

        File.WriteAllText(file, "1");
        Thread.Sleep(200);

        // 窗口还没结束，不该有投递（原生事件到得晚也算通过，见断言里的说明）
        Assert.DoesNotContain(harness.Snapshot(), e => e.Id == id);

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == id)),
            $"debounce 窗口结束后仍没有投递：{harness.Dump()}");

        // 同一路径在窗口里被合并成一条（写入两次 = create + changed，合并后只剩最后那条）。
        // 只针对被写的那个文件：目录自身的 mtime 也会变，那是另一条路径，理应各算一条
        Assert.Single(harness.Snapshot().Where(e => e.Id == id && e.Payload.Contains("debounced.txt")));
    }

    [Fact]
    public void UnwatchStopsDeliveryAndIsNotRepeatable()
    {
        using var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: null);

        Assert.True(harness.Registry.Unwatch(id));
        Assert.False(harness.Registry.Unwatch(id), "重复 unwatch 应当返回 false（编号已不存在）");
        Assert.Empty(harness.Registry.Active());

        int seen = harness.Snapshot().Count;
        File.WriteAllText(Path.Combine(_root, "after-unwatch.txt"), "x");
        Thread.Sleep(800);

        Assert.Equal(seen, harness.Snapshot().Count);
    }

    [Fact]
    public void UnwatchOneOfTwoKeepsTheOther()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        using var harness = new Harness();
        long first = harness.Registry.Watch("main", [a], recursive: false, delayMs: null);
        long second = harness.Registry.Watch("other", [b], recursive: false, delayMs: null);

        Assert.True(harness.Registry.Unwatch(first));

        File.WriteAllText(Path.Combine(b, "kept.txt"), "x");

        Assert.True(
            harness.WaitFor(list => list.Any(e => e.Id == second && e.Payload.Contains("kept.txt"))),
            $"取消第一个监视不该影响第二个：{harness.Dump()}");

        Assert.DoesNotContain(harness.Snapshot(), e => e.Id == first);
    }

    [Fact]
    public void WatchingAMissingPathReportsInvalidArgument()
    {
        using var harness = new Harness();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => harness.Registry.Watch("main", [Path.Combine(_root, "nope")], recursive: false, delayMs: null));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("路径不存在", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Registry.Active());
    }

    [Fact]
    public void DisposeStopsEverythingAndRejectsNewWatches()
    {
        var harness = new Harness();
        long id = harness.Registry.Watch("main", [_root], recursive: false, delayMs: null);
        Assert.True(id > 0, "监视器编号应当从 1 开始");

        harness.Registry.Dispose();
        Assert.Empty(harness.Registry.Active());

        int seen = harness.Snapshot().Count;
        File.WriteAllText(Path.Combine(_root, "after-dispose.txt"), "x");
        Thread.Sleep(800);
        Assert.Equal(seen, harness.Snapshot().Count);

        Assert.Throws<ObjectDisposedException>(
            () => harness.Registry.Watch("main", [_root], recursive: false, delayMs: null));
    }

    /// <summary>收事件的测试夹具。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<(string? Label, long Id, string Payload)> _events = [];

        internal Harness()
        {
            Registry = new OrielFsWatcherRegistry((label, id, payload) =>
            {
                lock (_events)
                {
                    _events.Add((label, id, payload));
                }
            });
        }

        internal OrielFsWatcherRegistry Registry { get; }

        internal List<(string? Label, long Id, string Payload)> Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        internal bool WaitFor(Func<List<(string? Label, long Id, string Payload)>, bool> predicate)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitMs);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate(Snapshot()))
                {
                    return true;
                }

                Thread.Sleep(50);
            }

            return predicate(Snapshot());
        }

        internal string Dump()
        {
            List<(string? Label, long Id, string Payload)> events = Snapshot();
            return events.Count == 0
                ? "（一条事件都没有）"
                : string.Join(" | ", events.Select(e => $"[{e.Id}] {e.Payload}"));
        }

        public void Dispose() => Registry.Dispose();
    }
}
