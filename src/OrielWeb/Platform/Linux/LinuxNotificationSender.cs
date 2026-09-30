using System.Diagnostics;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 通知投递：调用 <c>notify-send</c>（freedesktop 通知规范的命令行客户端）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是子进程而不是 libnotify 的 P/Invoke：libnotify 能拿到"点击/关闭"回调，
/// 但那要把 libnotify 与 glib 的引用计数、GLib 信号一起管起来；本库这一批先只交付
/// "把通知确实发出去"这一件事（点击上报记入 ROADMAP 作为后续增强，见
/// <see cref="OrielNotificationOptions.Id"/> 的说明）。
/// 用 <c>notify-send</c> 的另一个好处是零原生依赖：环境里没有通知守护时它只是个非 0 退出码，
/// 不会带崩宿主进程。
/// </para>
/// <para>
/// 参数经 <see cref="ProcessStartInfo.ArgumentList"/> 逐项传入（不经 shell），
/// 标题/正文里的引号、空格、分号都不会变成命令注入。
/// </para>
/// </remarks>
internal static class LinuxNotificationSender
{
    private static readonly Lazy<string?> s_executable = new(FindExecutable);

    /// <summary>环境里是否有 notify-send（决定 <see cref="OrielApp.NotificationsSupported"/>）。</summary>
    internal static bool IsAvailable => s_executable.Value is not null;

    internal static void Send(OrielNotificationOptions notification)
    {
        string? executable = s_executable.Value;
        if (executable is null)
        {
            Debug.WriteLine("[OrielWeb] 未找到 notify-send：通知被忽略（安装 libnotify-bin 即可）。");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("--app-name=OrielWeb");
            if (!string.IsNullOrEmpty(notification.IconPath))
            {
                startInfo.ArgumentList.Add("--icon=" + notification.IconPath);
            }
            startInfo.ArgumentList.Add(notification.Title);
            startInfo.ArgumentList.Add(notification.Body ?? string.Empty);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            // 有守护进程时 notify-send 立即返回；没有时也立即失败退出。给个上限防止极端情况下挂住。
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OrielWeb] 结束 notify-send 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // 通知失败不该影响业务：记一笔就走
            Debug.WriteLine($"[OrielWeb] notify-send 调用失败：{ex.Message}");
        }
    }

    private static string? FindExecutable()
    {
        // 先看常见安装位置，再扫 PATH：有的发行版装在 /usr/bin，有的在 /usr/local/bin。
        // 不调用 `which`：多一个子进程、多一层解析，而这一步只是为了拿一个路径。
        ReadOnlySpan<string> candidates = ["/usr/bin/notify-send", "/usr/local/bin/notify-send", "/bin/notify-send"];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string full = Path.Combine(directory, "notify-send");
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}
