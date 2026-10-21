using System.Globalization;

namespace OrielWeb.Ipc;

/// <summary>
/// 句柄式 fs（B17）的宿主侧句柄表：<c>id</c> → 一个打开的文件。
/// </summary>
/// <remarks>
/// <para>
/// 对标 Tauri 2 的**资源表**（ResourceTable + <c>rid</c>）。差别在于本库只有这一张表
/// （没有通用的 Resource 体系），所以 id 是普通的递增整数、不对外暴露成"资源"概念。
/// </para>
/// <para>
/// <b>作用域是应用</b>，不是窗口：与 Tauri 的进程级资源表同一姿态。这意味着一个窗口
/// 打开的句柄，另一个窗口拿到 id 也能读写——<b>能力模型不隔离窗口间的句柄</b>。
/// 这在单应用多窗口的形态下与 Tauri 一致；若将来要做窗口级隔离，改的是这张表
/// 的键（加窗口 label），而不是命令的形状。
/// </para>
/// <para>
/// <b>scope 只在 <c>open</c>/<c>create</c> 时判</b>：拿到 id 之后的读写不再看路径。
/// 这与 Tauri 相同——句柄代表"已经过授权的那个打开的文件"，反复按路径重判既无意义
/// （路径可能已经变了），也会把"同一文件换了名字就打不开"变成常态。
/// </para>
/// <para>
/// 线程：命令层在 UI 线程执行，但文件 I/O 由调用方线程直接做（见
/// <see cref="OrielFileHandle.Stream"/> 的说明）。表本身加锁，避免
/// "一个窗口关句柄、另一个窗口读"时的可见性问题。
/// </para>
/// </remarks>
internal sealed class OrielFileHandleRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, OrielFileHandle> _handles = [];
    private long _nextId;

    /// <summary>登记一个已打开的文件，返回它的 id。</summary>
    /// <remarks>
    /// id **单调递增、永不复用**：复用会让"旧句柄的迟到调用"作用到新文件上，
    /// 而那类事故的症状是"偶尔读到别人的内容"——比报错难查得多。
    /// </remarks>
    internal long Add(FileStream stream, string path)
    {
        lock (_gate)
        {
            long id = ++_nextId;
            _handles[id] = new OrielFileHandle(stream, path);
            return id;
        }
    }

    /// <summary>
    /// 取句柄；不存在（从未分配 / 已关闭）时抛 <see cref="OrielIpcException"/>。
    /// </summary>
    /// <param name="id">句柄 id（来自 <c>fs.open</c> / <c>fs.create</c> 的返回值）。</param>
    /// <param name="command">命令名（只用于错误文案）。</param>
    internal OrielFileHandle Get(long id, string command)
    {
        lock (_gate)
        {
            if (_handles.TryGetValue(id, out OrielFileHandle? handle))
            {
                return handle;
            }
        }

        throw new OrielIpcException(
            $"{command}: 句柄 {id.ToString(CultureInfo.InvariantCulture)} 不存在或已关闭。")
        {
            Code = OrielIpcErrorCodes.InvalidArgument,
        };
    }

    /// <summary>关闭并移除一个句柄。不存在时报错（与"重复 close"一致，见 Tauri 的 Resource 语义）。</summary>
    internal void Close(long id, string command)
    {
        OrielFileHandle handle;
        lock (_gate)
        {
            if (!_handles.TryGetValue(id, out handle!))
            {
                throw new OrielIpcException(
                    $"{command}: 句柄 {id.ToString(CultureInfo.InvariantCulture)} 不存在或已关闭。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            _handles.Remove(id);
        }

        // 在锁外 Dispose：flush 可能阻塞，锁内做会把别的窗口的读写一起卡住。
        handle.Dispose();
    }

    /// <summary>关闭全部句柄（应用退出时调用）。逐个 best-effort，不抛。</summary>
    public void Dispose()
    {
        OrielFileHandle[] all;
        lock (_gate)
        {
            all = [.. _handles.Values];
            _handles.Clear();
        }

        foreach (OrielFileHandle handle in all)
        {
            try
            {
                handle.Dispose();
            }
            catch (IOException)
            {
                // 退出清理不该因为某个文件写不回去而中断其余句柄的关闭。
                // 未落盘的数据在这里丢掉是"进程正在退出"的固有代价，不是本方法的缺陷。
            }
        }
    }
}

/// <summary>表里的一条记录：打开的文件流 + 它的路径（<c>fstat</c> 用）。</summary>
internal sealed class OrielFileHandle(FileStream stream, string path) : IDisposable
{
    /// <summary>
    /// 打开的文件流。读写位置由 <c>fs.seek</c> / 顺序读写推进。
    /// </summary>
    /// <remarks>
    /// <b>不加锁</b>：同一句柄的并发读写本来就由调用方负责（Tauri 的 <c>File</c>
    /// 也是 <c>&amp;mut</c> 独占）。多个窗口共用同一 id 时的交错由文件系统语义决定。
    /// </remarks>
    internal FileStream Stream { get; } = stream;

    /// <summary>打开时的路径（<c>fstat</c> 取时间戳用；不保证文件仍在原处）。</summary>
    internal string Path { get; } = path;

    public void Dispose() => Stream.Dispose();
}
