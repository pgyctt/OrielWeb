using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// OrielApp 的跨平台行为测试。
/// 重点：Run() 的 STA 前置检查不得在 Unix 上阻断启动——
/// Linux/macOS 的 <see cref="Thread.GetApartmentState"/> 恒为 Unknown，[STAThread] 亦被忽略。
/// </summary>
public sealed class OrielAppTests
{
    [Fact]
    public void EnsureApartment_OnUnix_DoesNotThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 由下面的 Windows 用例覆盖
        }

        Assert.Null(Record.Exception(OrielApp.EnsureApartment));
    }

    [Fact]
    public void EnsureApartment_OnWindowsNonStaThread_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Exception? captured = null;
        var thread = new Thread(() => captured = Record.Exception(OrielApp.EnsureApartment));
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(captured);
    }
}
