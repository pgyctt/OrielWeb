using System.Diagnostics;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 开机自启：写 <c>$XDG_CONFIG_HOME/autostart/&lt;id&gt;.desktop</c>（freedesktop 规范的 autostart 目录）。
/// </summary>
/// <remarks>
/// 只写文件，不碰任何桌面环境专有的接口：autostart 目录是各主流桌面（GNOME/KDE/Xfce…）
/// 共同遵守的约定，因此这一条在三平台上也是**最容易机器验证**的——
/// 文件在不在、内容对不对都能直接断言（见 tools/verify-linux-shell.sh）。
/// </remarks>
internal static class LinuxAutoStart
{
    internal static bool Enable(string id, IReadOnlyList<string>? arguments)
    {
        try
        {
            Directory.CreateDirectory(AutoStartDirectory);
            File.WriteAllText(
                PathFor(id),
                AutoStartContent.LinuxDesktopEntry(id, ExecutablePath, arguments));
            return true;
        }
        catch (Exception ex)
        {
            // 没写权限时如实返回失败，让调用方决定是否告知用户
            Debug.WriteLine($"[OrielWeb] 写 autostart 项失败：{ex.Message}");
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
            Debug.WriteLine($"[OrielWeb] 删 autostart 项失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>看文件在不在——用户可能自己删掉它，那时应当如实反映未启用。</summary>
    internal static bool IsEnabled(string id) => File.Exists(PathFor(id));

    private static string AutoStartDirectory
    {
        get
        {
            // XDG_CONFIG_HOME 优先（规范如此），否则退回 ~/.config
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return !string.IsNullOrEmpty(configHome)
                ? Path.Combine(configHome, "autostart")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config",
                    "autostart");
        }
    }

    private static string PathFor(string id) => Path.Combine(AutoStartDirectory, id + ".desktop");

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;
}
