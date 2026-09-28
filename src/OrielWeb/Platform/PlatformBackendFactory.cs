using System.Runtime.InteropServices;

namespace OrielWeb;

internal static class PlatformBackendFactory
{
    public static IPlatformBackend Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new OrielWeb.Platform.Windows.WindowsPlatformBackend();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new OrielWeb.Platform.MacOS.MacOSPlatformBackend();
        }
        if (OperatingSystem.IsLinux())
        {
            return new OrielWeb.Platform.Linux.LinuxPlatformBackend();
        }
        throw new PlatformNotSupportedException(
            $"OrielWeb 不支持当前平台：{RuntimeInformation.OSDescription}");
    }
}
