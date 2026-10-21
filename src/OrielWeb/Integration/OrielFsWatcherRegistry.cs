using System.Globalization;
using System.Text;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 运行中的文件系统监视器（对标 Tauri fs 插件的 watcher 表）。
/// </summary>
/// <remarks>
/// <para>
/// <b>底层是 <see cref="FileSystemWatcher"/></b>，它在三个平台上分别落到
/// <c>ReadDirectoryChangesW</c>（Windows）、<c>inotify</c>（Linux）、<c>FSEvents</c>（macOS）
/// ——与 Tauri 用的 <c>notify</c> crate 是**同一组系统设施**，所以"能看到哪些变化"这件事
/// 两边基本一致，差别主要在事件怎么归类（见 <see cref="BuildPayload"/>）。
/// </para>
/// <para>
/// <b>两种模式</b>：
/// <list type="bullet">
///   <item><description>
///   immediate（<c>delayMs</c> 为 <c>null</c>）：来一条发一条。
///   </description></item>
///   <item><description>
///   debounce（给了 <c>delayMs</c>）：窗口内的变化先攒着，窗口结束再发。攒的时候**按路径合并、
///   后来的种类覆盖先前的**（"先建后改"最终报 modify），重命名按 <c>(旧, 新)</c> 单独成条。
///   这不是 <c>notify-debouncer-full</c> 的逐字段合并算法，只是"少发几条、别丢路径"的近似
///   ——差别写在 docs/API.md，不假装相同。
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>单文件与目录</b>：给一个文件路径时，<see cref="FileSystemWatcher"/> 的 <c>Path</c> 设成它的
/// 所在目录、<c>Filter</c> 设成文件名；给目录时 <c>Path</c> 就是它、<c>Filter</c> 是 <c>*</c>。
/// 文件被"删了再建"（很多编辑器的保存方式）在两种情况下都能继续收到事件。
/// </para>
/// <para>
/// <b>线程模型</b>：<see cref="FileSystemWatcher"/> 的回调与 debounce 定时器都在线程池上，
/// 与命令（UI 线程）并发。状态在 <see cref="_gate"/> 下改动；投递在锁外做
/// ——各平台的 <c>EmitEvent</c> 会自己切回 UI 线程。
/// </para>
/// <para>
/// <b>编号单调递增、永不复用</b>（与 fs 句柄表、通知登记表同一条理由）：
/// 复用会让"取消一个早就关掉的监视"作用到另一个目录上，症状是"偶尔少收/多收事件"。
/// </para>
/// </remarks>
internal sealed class OrielFsWatcherRegistry : IDisposable
{
    /// <summary>
    /// 每个 watcher 的内核缓冲区大小。
    /// </summary>
    /// <remarks>
    /// 默认值是 8 KiB，一次大批量写入（解压、<c>git checkout</c>）就能把它冲爆，
    /// 而溢出的事件是**永久丢失**的（只发一条 <c>Error</c>）。调大只是降低概率，不是保证
    /// ——这种"丢事件"在三个平台上都存在，Tauri 的 notify 同样如此。
    /// </remarks>
    private const int WatcherBufferSize = 64 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly Action<string?, long, string> _emit;
    private long _nextId;
    private bool _disposed;

    /// <param name="emit">
    /// 把一条事件送给页面：(窗口 label, 监视器编号, JSON 载荷)。
    /// label 为 <c>null</c> 时由调用方决定送给谁（通常是广播）。
    /// </param>
    internal OrielFsWatcherRegistry(Action<string?, long, string> emit) => _emit = emit;

    /// <summary>
    /// 开始监视一批路径，返回监视器编号。
    /// </summary>
    /// <param name="windowLabel">发起这次监视的窗口（事件只回给它）。</param>
    /// <param name="paths">已经过 scope 校验的**绝对**路径。</param>
    /// <param name="recursive">目录是否递归（对单文件无意义）。</param>
    /// <param name="delayMs">debounce 窗口（毫秒）；<c>null</c> = 来一条发一条。</param>
    /// <exception cref="OrielIpcException">
    /// 某个路径不存在，或系统拒绝监视（权限、路径过长）。
    /// </exception>
    internal long Watch(string? windowLabel, IReadOnlyList<string> paths, bool recursive, int? delayMs)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("至少要给一个路径。", nameof(paths));
        }

        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = new Entry(++_nextId, windowLabel, delayMs);
        }

        // 先把所有 watcher 建好再入表：任何一个路径不存在都在这里失败，
        // 不会留下"半套监视"（一半路径在收事件、另一半没有）。
        var watchers = new List<FileSystemWatcher>(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                watchers.Add(CreateWatcher(entry, path, recursive));
            }

            if (delayMs is int delay)
            {
                entry.Debounce = new Timer(_ => Flush(entry), state: null, Timeout.Infinite, Timeout.Infinite);
            }

            entry.Watchers = [.. watchers];

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _entries[entry.Id] = entry;
            }

            // 入表之后再开闸：否则第一条事件可能在表里还查不到它的时候到达
            foreach (FileSystemWatcher watcher in entry.Watchers)
            {
                watcher.EnableRaisingEvents = true;
            }

            return entry.Id;
        }
        catch (Exception ex)
        {
            entry.Dispose();

            // 已经入表的话要撤掉（重复 Dispose 是安全的，所以这里不用额外判断）
            lock (_gate)
            {
                _entries.Remove(entry.Id);
            }

            if (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new OrielIpcException($"fs.watch: 无法监视给定路径：{ex.Message}", ex)
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            throw;
        }
    }

    /// <summary>停止监视；编号不存在时返回 <c>false</c>（与"重复 unwatch"一致）。</summary>
    internal bool Unwatch(long id)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.Remove(id, out entry))
            {
                return false;
            }
        }

        entry.Dispose();
        return true;
    }

    /// <summary>当前在监视的编号（升序）。给测试与诊断用。</summary>
    internal IReadOnlyList<long> Active()
    {
        lock (_gate)
        {
            return [.. _entries.Keys.OrderBy(id => id)];
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (Entry entry in entries)
        {
            entry.Dispose();
        }
    }

    /// <summary>建一个针对单个路径的 watcher（目录或文件都认）。</summary>
    private FileSystemWatcher CreateWatcher(Entry entry, string path, bool recursive)
    {
        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new OrielIpcException($"fs.watch: 路径不存在「{path}」。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        // 文件：盯它所在目录 + 文件名过滤；目录：盯目录本身 + 全匹配
        string directory = isDirectory ? path : Path.GetDirectoryName(path) ?? path;
        string filter = isDirectory ? "*" : Path.GetFileName(path);

        var watcher = new FileSystemWatcher(directory, filter)
        {
            // 单文件不递归：同名文件在子目录里不是我们要盯的那个
            IncludeSubdirectories = isDirectory && recursive,

            // 不订阅 LastAccess：Linux 上读一次文件就会触发一长串事件，
            // 而"谁读了它"既不是 Tauri 的 WatchEventKind 关心的，页面通常也不想要
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite | NotifyFilters.Size
                           | NotifyFilters.CreationTime | NotifyFilters.Attributes,

            InternalBufferSize = WatcherBufferSize,
        };

        watcher.Created += (_, e) => Report(entry, WatcherChangeTypes.Created, e.FullPath, null);
        watcher.Changed += (_, e) => Report(entry, WatcherChangeTypes.Changed, e.FullPath, null);
        watcher.Deleted += (_, e) => Report(entry, WatcherChangeTypes.Deleted, e.FullPath, null);
        watcher.Renamed += (_, e) => Report(entry, WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
        watcher.Error += (_, e) => ReportError(entry, e.GetException().Message);

        return watcher;
    }

    /// <summary>收到一条原生事件：立即发，或攒进 debounce 窗口。</summary>
    private void Report(Entry entry, WatcherChangeTypes kind, string path, string? oldPath)
    {
        if (entry.DelayMs is not int delay)
        {
            Emit(entry, kind, path, oldPath);
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_entries.ContainsKey(entry.Id))
            {
                return;
            }

            // 同一条路径在窗口里只留最后一次（"先建后改"报 modify）；
            // 重命名按 (旧, 新) 成对保留，免得连续改名时中间的名字被吃掉
            string key = oldPath is null ? path : oldPath + "\0" + path;
            entry.Pending[key] = new Pending(kind, path, oldPath);
            entry.Debounce?.Change(delay, Timeout.Infinite);
        }
    }

    /// <summary>debounce 窗口结束：把攒下的一条条发出去。</summary>
    private void Flush(Entry entry)
    {
        List<Pending> batch;
        lock (_gate)
        {
            if (entry.Pending.Count == 0)
            {
                return;
            }

            batch = [.. entry.Pending.Values];
            entry.Pending.Clear();
        }

        foreach (Pending pending in batch)
        {
            Emit(entry, pending.Kind, pending.Path, pending.OldPath);
        }
    }

    /// <summary>送一条事件给页面（投递失败不影响监视本身）。</summary>
    private void Emit(Entry entry, WatcherChangeTypes kind, string path, string? oldPath)
    {
        // 关掉之后再收到的事件直接丢：unwatch 之后还往页面推一条"你刚取消了的东西"
        // 只会让调用方以为自己取消漏了
        lock (_gate)
        {
            if (_disposed || !_entries.ContainsKey(entry.Id))
            {
                return;
            }
        }

        try
        {
            _emit(entry.WindowLabel, entry.Id, BuildPayload(kind, path, oldPath));
        }
        catch (Exception ex)
        {
            // 窗口正在销毁 / 页面还没就绪：事件送不到不是监视失败
            OrielDiagnostics.Report($"[OrielWeb] 推送 fs.watch 事件失败：{ex.Message}");
        }
    }

    /// <summary>系统报错（缓冲区溢出、目录被删等）走一条 <c>other</c> 事件。</summary>
    private void ReportError(Entry entry, string message)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.ContainsKey(entry.Id))
            {
                return;
            }
        }

        try
        {
            // paths 留空：报错时**不知道**是哪个路径出的问题，给一个猜的比给空更糟
            _emit(
                entry.WindowLabel,
                entry.Id,
                "{\"type\":\"other\",\"paths\":[],\"attrs\":{\"error\":" + JsonText.EncodeString(message) + "}}");
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 推送 fs.watch 错误事件失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把原生事件翻成 Tauri 的 <c>WatchEvent</c>：<c>{ type, paths, attrs }</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 归类上有三处**如实降级**（原生 watcher 给不出更细的信息）：
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>Changed</c> 一律报 <c>{modify:{kind:'data',mode:'any'}}</c>：
    ///   <see cref="FileSystemWatcher"/> 不区分"内容变了"与"元数据变了"
    ///   （<c>notify</c> 在部分平台能分），所以给不出 <c>metadata</c>。
    ///   </description></item>
    ///   <item><description>
    ///   <c>Deleted</c> 一律报 <c>{remove:{kind:'any'}}</c>：文件已经没了，没法再 stat
    ///   判断它原来是文件还是目录。
    ///   </description></item>
    ///   <item><description>
    ///   <c>Created</c> 会在事件到达时 stat 一次，能分 <c>file</c> / <c>folder</c>；
    ///   瞬生瞬灭（事件到达前又被删了）的只能报 <c>any</c>。
    ///   </description></item>
    /// </list>
    /// <para>
    /// <c>attrs</c> 恒为 <c>{}</c>：Tauri 那边是 <c>notify</c> 的 <c>EventAttributes</c>
    /// （tracker / flag），而这套设施没有对应概念。**给空对象而不是编一个字段**——
    /// 多出来的字段看起来像有信息，实际是假的。
    /// </para>
    /// </remarks>
    internal static string BuildPayload(WatcherChangeTypes kind, string path, string? oldPath)
    {
        string type;
        string[] paths;

        switch (kind)
        {
            case WatcherChangeTypes.Created:
                string createdKind = Directory.Exists(path) ? "folder"
                    : File.Exists(path) ? "file"
                    : "any";
                type = "{\"create\":{\"kind\":\"" + createdKind + "\"}}";
                paths = [path];
                break;

            case WatcherChangeTypes.Deleted:
                type = "{\"remove\":{\"kind\":\"any\"}}";
                paths = [path];
                break;

            case WatcherChangeTypes.Renamed:
                // 原生只能给"旧名 → 新名"，给不出"只移进/只移出"，
                // 所以 mode 是 both（Tauri 的 notify 在同样场景下也是 both）
                type = "{\"modify\":{\"kind\":\"rename\",\"mode\":\"both\"}}";
                paths = [oldPath ?? path, path];
                break;

            default:
                type = "{\"modify\":{\"kind\":\"data\",\"mode\":\"any\"}}";
                paths = [path];
                break;
        }

        var builder = new StringBuilder(96 + (path.Length * 2));
        _ = builder.Append("{\"type\":").Append(type).Append(",\"paths\":[");

        for (int i = 0; i < paths.Length; i++)
        {
            if (i > 0)
            {
                _ = builder.Append(',');
            }

            _ = builder.Append(JsonText.EncodeString(paths[i]));
        }

        return builder.Append("],\"attrs\":{}}").ToString();
    }

    /// <summary>一次 debounce 窗口里攒下的一条变化。</summary>
    private readonly record struct Pending(WatcherChangeTypes Kind, string Path, string? OldPath);

    /// <summary>一个监视器（可能同时盯多个路径）。</summary>
    private sealed class Entry : IDisposable
    {
        private bool _disposed;

        internal Entry(long id, string? windowLabel, int? delayMs)
        {
            Id = id;
            WindowLabel = windowLabel;
            DelayMs = delayMs;
        }

        internal long Id { get; }

        /// <summary>发起这次监视的窗口（事件只回给它）。</summary>
        internal string? WindowLabel { get; }

        /// <summary>debounce 窗口（毫秒）；<c>null</c> = immediate。</summary>
        internal int? DelayMs { get; }

        internal FileSystemWatcher[] Watchers { get; set; } = [];

        internal Timer? Debounce { get; set; }

        /// <summary>窗口内攒下的变化，键是路径（重命名是 <c>旧\0新</c>）。由注册表的锁保护。</summary>
        internal Dictionary<string, Pending> Pending { get; } = [];

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 先停事件再释放：顺序反过来的话，释放过程中到达的回调会摸到已释放的对象
            foreach (FileSystemWatcher watcher in Watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    // 目录已经被删掉时 Dispose 也可能抛（某些平台）。清理不该因此中断
                    OrielDiagnostics.Report($"[OrielWeb] 释放 fs.watch 失败：{ex.Message}");
                }
            }

            Debounce?.Dispose();
            Debounce = null;
        }
    }
}
