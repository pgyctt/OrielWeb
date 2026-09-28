using OrielWeb.Platform.Linux;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 平台状态注册表的清理行为（C-4）。
/// 真实触发点在 GTK 的 destroy 信号回调里（需要 GTK 运行时），CI 环境无法复现，
/// 因此这里只验证注册/注销这一对纯托管操作，确保注销路径确实会移除条目、
/// 且对不存在的键是幂等的。
/// 真机验收仍须在 Linux 上多窗口反复开关并观察注册表计数与内存。
/// </summary>
[Collection("IpcSerial")]
public sealed class PlatformStateRegistryTests
{
    [Fact]
    public void LinuxWebviewAndManagerStates_UnregisterRemovesEntries()
    {
        LinuxSignalHandlers.RegisterWebview(0x1001, null!);
        LinuxSignalHandlers.RegisterManager(0x2001, null!);
        try
        {
            int webviews = LinuxSignalHandlers.RegisteredWebviewCount;
            int managers = LinuxSignalHandlers.RegisteredManagerCount;

            LinuxSignalHandlers.UnregisterWebview(0x1001);
            LinuxSignalHandlers.UnregisterManager(0x2001);

            Assert.Equal(webviews - 1, LinuxSignalHandlers.RegisteredWebviewCount);
            Assert.Equal(managers - 1, LinuxSignalHandlers.RegisteredManagerCount);
        }
        finally
        {
            LinuxSignalHandlers.UnregisterWebview(0x1001);
            LinuxSignalHandlers.UnregisterManager(0x2001);
        }
    }

    [Fact]
    public void LinuxUnregister_IsIdempotent()
    {
        // 不存在的键：不得抛异常（窗口销毁路径可能重复进入）
        LinuxSignalHandlers.UnregisterWindow(0x7FFF);
        LinuxSignalHandlers.UnregisterWebview(0x7FFF);
        LinuxSignalHandlers.UnregisterManager(0x7FFF);
    }
}
