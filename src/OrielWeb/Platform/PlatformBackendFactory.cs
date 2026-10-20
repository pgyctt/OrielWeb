using System.Runtime.InteropServices;

namespace OrielWeb;

internal static class PlatformBackendFactory
{
    /// <param name="webView2UserDataFolder">
    /// WebView2 用户数据目录的覆盖值（来自 <see cref="OrielAppBuilder.UseUserDataFolder"/>）。
    /// 只有 Windows 用得着——另两个平台的引擎没有这个概念，收了也不用。
    /// </param>
    /// <param name="appIdentifier">
    /// 应用标识符，只在上面那个覆盖值为空时用来拼默认目录。同样只有 Windows 用得着。
    /// </param>
    public static IPlatformBackend Create(string? webView2UserDataFolder = null, string? appIdentifier = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return new OrielWeb.Platform.Windows.WindowsPlatformBackend(webView2UserDataFolder, appIdentifier);
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
