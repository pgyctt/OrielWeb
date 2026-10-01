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
/// 标题/正文里的引号、空格、分号都不会变成命令注入。注意"不经 shell"**不等于**"不会被当成选项"：
/// 以 <c>-</c> 开头的位置参数仍会被 GLib 的选项解析器吃掉，见 <see cref="AsPositional"/>。
/// </para>
/// </remarks>
internal static class LinuxNotificationSender
{
    private static readonly Lazy<string?> s_executable = new(FindExecutable);

    /// <summary>环境里是否有 notify-send（决定 <see cref="OrielApp.NotificationsSupported"/>）。</summary>
    internal static bool IsAvailable => s_executable.Value is not null;

    /// <summary>
    /// 投递一条通知。
    /// </summary>
    /// <param name="notification">通知内容与标识。</param>
    /// <param name="appId">
    /// 应用标识，作为 <c>--app-name</c> 传给 notify-send（通知守护据此给应用分组、让用户按应用静音）。
    /// 已由 <see cref="OrielNotificationAppId.Sanitize"/> 规范化，且整个 token 形如 <c>--app-name=x</c>，
    /// 因此不存在"被当成选项"的问题。
    /// </param>
    /// <returns>
    /// <c>notify-send</c> 是否**成功退出（退出码 0）**。环境里没有通知守护时它会以非 0 退出，
    /// 此时通知不会出现——这个返回值就是"没看到横幅"与"没发出去"的分界。
    /// </returns>
    internal static bool Send(OrielNotificationOptions notification, string appId)
    {
        string? executable = s_executable.Value;
        if (executable is null)
        {
            Debug.WriteLine("[OrielWeb] 未找到 notify-send：通知被忽略（安装 libnotify-bin 即可）。");
            return false;
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

            startInfo.ArgumentList.Add("--app-name=" + appId);
            if (!string.IsNullOrEmpty(notification.IconPath))
            {
                startInfo.ArgumentList.Add("--icon=" + notification.IconPath);
            }
            startInfo.ArgumentList.Add(AsPositional(notification.Title));
            startInfo.ArgumentList.Add(AsPositional(notification.Body ?? string.Empty));

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
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

                return false; // 超时未退出：不能当作已投递
            }

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[OrielWeb] notify-send 退出码 {process.ExitCode}：通知未投递（通常是没有通知守护）。");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 通知失败不该影响业务：记一笔就走
            Debug.WriteLine($"[OrielWeb] notify-send 调用失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把标题/正文做成"绝不会被 GLib 当成选项"的位置参数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// notify-send 的摘要与正文是**位置参数**，而 GLib 的选项解析器（<c>g_option_context_parse</c>）
    /// 在遇到以 <c>-</c> 开头的参数时会先当选项解析。标题/正文来自应用（可能是页面数据），
    /// 内容形如 <c>-u critical</c> 时会被当作 <c>--urgency</c> 吃掉，位置参数随之错位
    /// （摘要变成正文、正文丢失）。这是**参数注入**，与 macOS/Windows 通知路径要防的转义问题是同一类。
    /// </para>
    /// <para>
    /// 这里**刻意不用 <c>--</c> 分隔符**：GLib 只在 <c>separator_pos &gt; 0</c> 时才把 <c>--</c>
    /// 从 argv 里剥掉，而"<c>--</c> 之后的某个参数以 <c>-</c> 开头"这条分支会把
    /// <c>separator_pos</c> 重置为 0（见 goption.c 中普通参数分支里的
    /// <c>if (!parsed &amp;&amp; (has_unknown || (*argv)[i][0] == '-'))</c>）——那时 <c>--</c> 会留在
    /// argv 里变成摘要，反而把错位固定下来。所以从根上处理：让位置参数不再像选项，前置一个空格。
    /// </para>
    /// <para>
    /// 代价是这类标题/正文会多一个前导空格——比通知内容整体错位轻微得多，且只在首字符是 <c>-</c>
    /// 时发生。不改动其余内容，也不依赖 GLib 的内部行为。
    /// </para>
    /// </remarks>
    internal static string AsPositional(string value)
        => value.Length > 0 && value[0] == '-' ? " " + value : value;

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
