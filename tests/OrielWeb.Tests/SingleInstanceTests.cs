using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 单实例判定必须在 <c>Build()</c> 之后就能问（下游反馈 2026-10-11 的 P0-2：判定在 <c>Run()</c> 里时，
/// 第二实例已经先把数据库打开、设备连上了）。
/// </summary>
/// <remarks>
/// 真机上的双进程行为由 demo 的 <c>--selftest single-instance</c> 取证；这里在同一进程里建两个应用
/// （第二个必然抢不到锁）钉住"判定时机"与 AlreadyRunning 语义。
/// </remarks>
public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondAppWithSameId_IsSecondaryInstance_BeforeRun()
    {
        string id = "orielweb-tests-" + Guid.NewGuid().ToString("N");

        using var primary = Oriel.CreateBuilder().SingleInstance(id).Build();
        using var secondary = Oriel.CreateBuilder(["--open", "page-2"]).SingleInstance(id).Build();

        Assert.False(primary.IsSecondaryInstance);
        Assert.True(secondary.IsSecondaryInstance);
    }

    [Fact]
    public void WithoutSingleInstance_IsNeverSecondary()
    {
        using var app = Oriel.CreateBuilder().Build();
        Assert.False(app.IsSecondaryInstance);
    }

    [Fact]
    public void ActivationArgs_RoundTripThroughThePipeProtocol()
    {
        // 参数经一行文本传输：空格/换行/中文都必须原样回来（编码用 Uri 转义）
        string[] args = ["--open", "页面 2", "带\n换行", "a%20b", ""];
        IReadOnlyList<string> decoded = SingleInstanceGuard.DecodeActivationArgs(
            SingleInstanceGuard.EncodeActivationArgs(args));

        Assert.Equal(args, decoded);
    }

    [Fact]
    public void EmptyActivationArgs_EncodeToEmpty_AndDecodeToNothing()
    {
        Assert.Equal(string.Empty, SingleInstanceGuard.EncodeActivationArgs(null));
        Assert.Equal(string.Empty, SingleInstanceGuard.EncodeActivationArgs([]));
        Assert.Empty(SingleInstanceGuard.DecodeActivationArgs(string.Empty));
        Assert.Empty(SingleInstanceGuard.DecodeActivationArgs(null));
    }
}
