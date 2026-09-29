using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace OrielWeb;

/// <summary>
/// 单实例守卫：用命名管道（.NET 在 Unix 上映射为 Unix domain socket）判断"是否已有实例在跑"，
/// 并让第二个实例把"激活"请求发给首实例。
/// </summary>
/// <remarks>
/// 为什么三平台统一用命名管道，而不是各平台的原生手段（Windows 命名互斥体 / Linux 的 D-Bus /
/// macOS 的 NSRunningApplication）：这里需要的语义只有两条——"能否独占这个名字"和"能否给持有者
/// 发一句话"。统一实现换来的是**一套**失败模式，而不是三套需要分别验证（且只能在各自 CI 上验证）的。
/// </remarks>
internal static class SingleInstanceGuard
{
    /// <summary>连接与读写的等待上限。第二个实例不该为了通知首实例而卡住。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 尝试成为首实例：用一个独占打开的文件当锁。已有实例在跑时返回 false。
    /// </summary>
    /// <remarks>
    /// **不用"同名命名管道能否创建成功"来判定**：在 Unix 上 .NET 会先把已存在的 socket 文件删掉再绑定，
    /// 于是第二个实例也能"成功"创建，两个进程都以为自己是首实例（实测就是这个现象：两边都在等对方）。
    /// 独占文件锁在三个平台上语义一致——占用者存活期间，第二次打开必然失败；进程退出（即使崩溃）
    /// 由操作系统释放句柄，不留需要手工清理的陈旧状态。
    /// </remarks>
    internal static bool TryAcquire(string id, out FileStream? lockFile)
    {
        try
        {
            lockFile = new FileStream(
                LockPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 锁被占用：这就是"已有实例"的正常信号，不是错误
            lockFile = null;
            return false;
        }
    }

    /// <summary>首实例侧：创建用于接收通知的管道。只有首实例会创建它，因此不会与谁冲突。</summary>
    internal static NamedPipeServerStream CreateListener(string id)
        => new(
            PipeName(id),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    /// <summary>第二个实例侧：连上首实例并发一条消息。连不上就放弃（不该因此失败或卡住）。</summary>
    internal static void NotifyPrimary(string id, string? payload = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(payload ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 首实例可能在收到通知前刚好退出；第二个实例仍应正常退出，不把这种情况当失败
            Debug.WriteLine($"[OrielWeb] 通知首实例失败（可能它已退出）：{ex.Message}");
        }
    }

    /// <summary>首实例侧：循环等待后续实例的连接，每次收到一条消息就回调一次。</summary>
    internal static async Task ListenAsync(NamedPipeServerStream server, Action<string?> onMessage)
    {
        while (true)
        {
            await server.WaitForConnectionAsync().ConfigureAwait(false);
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                string? payload = await reader.ReadLineAsync().ConfigureAwait(false);
                onMessage(payload);
            }
            finally
            {
                // 处理完一个连接就断开，继续等下一位（Disconnect 后才能再次 WaitForConnection）
                server.Disconnect();
            }
        }
    }

    /// <summary>
    /// 把实例标识映射成管道名。用哈希而不是原样拼接：标识可能含路径分隔符等字符，
    /// 而 Unix 上管道名会变成文件系统里的名字。
    /// </summary>
    private static string PipeName(string id)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return "orielweb-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>锁文件路径（临时目录下，名字同管道名）。</summary>
    private static string LockPath(string id)
        => Path.Combine(Path.GetTempPath(), PipeName(id) + ".lock");
}
