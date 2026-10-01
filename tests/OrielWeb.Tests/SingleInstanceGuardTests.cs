using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 单实例守卫的锁判定（<see cref="SingleInstanceGuard.TryAcquire"/>）。
/// </summary>
/// <remarks>
/// 这里钉住的是"锁被占用"与"锁创建不出来"必须分开这件事。早先两者都返回"已有实例"，
/// 于是锁文件创建失败时应用会**不建窗、不进消息循环、以退出码 0 结束**——表现为"点了没反应"。
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    /// <summary>每个用例用独立的标识，避免并行执行时互相抢锁。</summary>
    private static string NewId() => "orielweb-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_FirstCallAcquires()
    {
        string id = NewId();

        Assert.Equal(SingleInstanceGuard.AcquireResult.Acquired, SingleInstanceGuard.TryAcquire(id, out FileStream? lockFile));

        lockFile!.Dispose();
    }

    [Fact]
    public void TryAcquire_WhileHeld_ReportsAlreadyRunning()
    {
        string id = NewId();
        Assert.Equal(SingleInstanceGuard.AcquireResult.Acquired, SingleInstanceGuard.TryAcquire(id, out FileStream? first));

        try
        {
            // 锁被占着 → 判定为已有实例（不是 Unavailable：目录是可写的）
            Assert.Equal(SingleInstanceGuard.AcquireResult.AlreadyRunning, SingleInstanceGuard.TryAcquire(id, out _));
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void TryAcquire_AfterRelease_AcquiresAgain()
    {
        string id = NewId();

        Assert.Equal(SingleInstanceGuard.AcquireResult.Acquired, SingleInstanceGuard.TryAcquire(id, out FileStream? first));
        first!.Dispose();

        // 句柄一释放（进程崩溃时由操作系统做同样的事），锁就不再占用，不该留下需要手工清理的陈旧状态
        Assert.Equal(SingleInstanceGuard.AcquireResult.Acquired, SingleInstanceGuard.TryAcquire(id, out FileStream? second));
        second!.Dispose();
    }

    [Fact]
    public void TryAcquire_DoesNotThrowForUnknownId()
    {
        // 标识任意字符串都应被接受（内部哈希成文件名，不当路径用）
        Assert.Equal(
            SingleInstanceGuard.AcquireResult.Acquired,
            SingleInstanceGuard.TryAcquire("含中文/与\\分隔符 的 id", out FileStream? lockFile));

        lockFile!.Dispose();
    }

    [Fact]
    public void CreateListener_CanBeCreatedAndDisposed()
    {
        // 首实例侧的管道：只有首实例创建它，因此不应与任何东西冲突
        string id = NewId();
        using var server = SingleInstanceGuard.CreateListener(id);
        Assert.NotNull(server);
    }
}
