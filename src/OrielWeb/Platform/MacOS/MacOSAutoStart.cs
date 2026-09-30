using System.Diagnostics;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 开机自启：写 <c>~/Library/LaunchAgents/&lt;id&gt;.plist</c>。
/// </summary>
/// <remarks>
/// 只写 plist，<b>不调用 <c>launchctl load</c></b>：位于 LaunchAgents 目录的 plist 会在用户下次登录时
/// 自动被 launchd 拾取，而手动 load 会立刻把应用再拉起一遍（当前这个进程还在跑）——
/// 那是用户没要求的副作用。代价是"启用后要等下次登录才生效"，这一点写进 README。
/// </remarks>
internal static class MacOSAutoStart
{
    internal static bool Enable(string id, IReadOnlyList<string>? arguments)
    {
        try
        {
            Directory.CreateDirectory(LaunchAgentsDirectory);
            File.WriteAllText(
                PathFor(id),
                AutoStartContent.MacOSLaunchAgent(id, ExecutablePath, arguments));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 写 LaunchAgent 失败：{ex.Message}");
            return false;
        }
    }

    internal static bool Disable(string id)
    {
        try
        {
            string path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrielWeb] 删 LaunchAgent 失败：{ex.Message}");
            return false;
        }
    }

    internal static bool IsEnabled(string id) => File.Exists(PathFor(id));

    private static string LaunchAgentsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents");

    private static string PathFor(string id) => Path.Combine(LaunchAgentsDirectory, id + ".plist");

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;
}
