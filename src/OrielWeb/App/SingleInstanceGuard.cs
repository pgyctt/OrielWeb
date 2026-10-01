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

    /// <summary>锁文件所在的每用户私有目录（见 <see cref="CreateRuntimeDirectory"/>）。</summary>
    private static readonly Lazy<string> s_runtimeDirectory =
        new(CreateRuntimeDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// <see cref="TryAcquire"/> 的判定结果。三种情形**必须分开**——把它们混成一个 bool 是曾经的缺陷来源。
    /// </summary>
    internal enum AcquireResult
    {
        /// <summary>本进程取得锁，是首实例。</summary>
        Acquired,

        /// <summary>已有实例持有锁（正常信号，不是错误）。</summary>
        AlreadyRunning,

        /// <summary>
        /// 锁文件创建不出来（目录只读、磁盘满、杀软/沙箱拦截……），**无法判定**有没有别的实例。
        /// 调用方应倾向于照常启动，而不是当作第二实例退出。
        /// </summary>
        Unavailable,
    }

    /// <summary>
    /// 尝试成为首实例：用一个独占打开的文件当锁。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **不用"同名命名管道能否创建成功"来判定**：在 Unix 上 .NET 会先把已存在的 socket 文件删掉再绑定，
    /// 于是第二个实例也能"成功"创建，两个进程都以为自己是首实例（实测就是这个现象：两边都在等对方）。
    /// 独占文件锁在三个平台上语义一致——占用者存活期间，第二次打开必然失败；进程退出（即使崩溃）
    /// 由操作系统释放句柄，不留需要手工清理的陈旧状态。
    /// </para>
    /// <para>
    /// <b>判定为 <see cref="AcquireResult.AlreadyRunning"/> 时，本方法已经把"激活"请求发给首实例了</b>
    /// ——那个探活连接本身就是判定手段（见 <see cref="TryNotifyPrimary"/>）。调用方因此**不要**再调一次
    /// 通知，否则首实例会收到两次激活。
    /// </para>
    /// </remarks>
    internal static AcquireResult TryAcquire(string id, out FileStream? lockFile)
    {
        lockFile = null;
        string path = LockPath(id);
        try
        {
            lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return AcquireResult.Acquired;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 走到这里有两种完全不同的原因，**必须分开对待**：
            //   ① 锁被另一个实例占着 —— 这是"已有实例"的正常信号；
            //   ② 锁文件根本创建不出来 —— 目录只读、磁盘满、TMPDIR 指向不可写位置、杀软/沙箱拦截……
            // 早先两者都返回"已有实例"，于是 ② 会让应用**不建窗、不进消息循环、以退出码 0 结束**：
            // 用户看到的是"点了没反应"，而退出码是成功的、诊断信息还写在 WinExe 看不见的控制台上。
            //
            // 不靠异常类型或 HResult 区分（各平台抛出来的值并不一致），而是用两个可观测的事实来判断：
            // 先探首实例的管道，再探目录是否真的写得进去。
            if (TryNotifyPrimary(id))
            {
                // 首实例在监听 —— 它确实在跑，而且激活请求已经发出去了
                return AcquireResult.AlreadyRunning;
            }

            if (CanWriteToDirectory(s_runtimeDirectory.Value))
            {
                // 目录写得进去，说明锁文件是"被占用"而不是"创建不出来"。
                // 这一支覆盖"首实例正在启动、管道还没来得及监听"的窗口期：
                // 此时通知发不出去，但按既有语义仍应判为已有实例。
                return AcquireResult.AlreadyRunning;
            }

            Debug.WriteLine(
                $"[OrielWeb] 单实例锁无法创建（{path}）：{ex.Message}。" +
                "按未检测到其他实例处理并继续启动——静默不启动比多开一个窗口难排查得多。");
            return AcquireResult.Unavailable;
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

    /// <summary>
    /// 第二个实例侧：连上首实例并发一条消息。
    /// </summary>
    /// <returns>
    /// 是否确实把消息送到了首实例。连不上时返回 false——这个返回值同时被
    /// <see cref="TryAcquire"/> 用来区分"锁被占用"与"锁创建不出来"。
    /// </returns>
    private static bool TryNotifyPrimary(string id, string? payload = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(payload ?? string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 首实例可能在收到通知前刚好退出；第二个实例仍应正常退出，不把这种情况当失败
            Debug.WriteLine($"[OrielWeb] 未能连上首实例的管道（可能它没在跑，或已退出）：{ex.Message}");
            return false;
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
    /// <remarks>
    /// 名字里**带上当前用户**：.NET 把 Unix 上的套接字文件放在共享临时目录里（框架行为，无法按实例
    /// 指定路径），不加用户隔离时同机另一个用户可以预先占住这个名字。用"用户 + 标识"一起哈希，
    /// 跨用户就不会撞名；哈希本身也不泄露用户名。
    /// </remarks>
    private static string PipeName(string id)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + id));
        return "orielweb-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>锁文件路径（每用户私有目录下，名字同管道名）。</summary>
    private static string LockPath(string id)
        => Path.Combine(s_runtimeDirectory.Value, PipeName(id) + ".lock");

    /// <summary>
    /// 每用户私有的运行期目录：锁文件放这里。
    /// </summary>
    /// <remarks>
    /// 不直接把共享的 <see cref="Path.GetTempPath"/> 当目录：Unix 上它是全局可写的 <c>/tmp</c>，
    /// 而锁文件的名字是可预测的（<c>orielweb-&lt;hash&gt;.lock</c>），同机其他用户可以抢先创建它，
    /// 让本应用**永远认为自己不是首实例**（拒绝服务）。
    /// 优先用 <c>XDG_RUNTIME_DIR</c>——freedesktop 就是为"每用户运行期套接字/锁"准备这个目录的；
    /// 没有时退回"临时目录 + 用户名"子目录（Windows 的 <c>%TEMP%</c> 本身已按用户隔离）。
    /// </remarks>
    private static string CreateRuntimeDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string baseDirectory = !string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)
            ? xdg
            : Path.Combine(Path.GetTempPath(), "orielweb-" + Environment.UserName);

        string directory = Path.Combine(baseDirectory, "orielweb");
        Directory.CreateDirectory(directory);

        // 权限收到 0700：即便退回的是共享临时目录，"别的用户连目录都进不去"这一点仍然成立。
        // Windows 上没有这个概念（该调用会抛 PlatformNotSupportedException），因此只在 Unix 上做。
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 收紧权限失败不该阻断启动：目录里也没有敏感内容
                Debug.WriteLine($"[OrielWeb] 收紧运行期目录权限失败（{directory}）：{ex.Message}");
            }
        }

        return directory;
    }

    /// <summary>
    /// 探测目录是否可写：真的写一个临时文件，而不是看权限位——ACL、只读挂载、磁盘配额
    /// 都只有写一次才知道。用来把"锁被占用"与"锁创建不出来"区分开。
    /// </summary>
    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $"orielweb-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            // DeleteOnClose 保证退出时清掉，不留垃圾
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[OrielWeb] 运行期目录不可写（{directory}）：{ex.Message}");
            return false;
        }
    }
}
