using System.Diagnostics;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 通知投递：调用系统自带的 <c>osascript</c> 执行一段 <c>display notification</c>。
/// </summary>
/// <remarks>
/// <para>
/// 未打包运行（没有 <c>CFBundleIdentifier</c>）时这是唯一可用路径：<c>UNUserNotificationCenter</c>
/// 对无 bundle 的进程直接拒绝（Ryn 在同一处分了"打包 → UNUserNotificationCenter / 未打包 → osascript"
/// 两条路，本库这一批先交付后者）。
/// 代价是拿不到点击回调，因此 <see cref="OrielApp.NotificationClicked"/> 在 macOS 上不触发
/// （见 <see cref="OrielNotificationOptions.Id"/> 的说明）。放进 .app bundle 后用 UN 的那条路
/// 才能上报点击，已记入 ROADMAP。
/// </para>
/// <para>
/// <b>转义是安全关键</b>：标题与正文来自应用（可能是页面传来的数据），它们会被拼进一段
/// AppleScript 源码里。反斜杠与双引号必须转义，否则内容可以提前闭合字符串、执行任意 AppleScript。
/// 参数用 <see cref="ProcessStartInfo.ArgumentList"/> 传递，不经过 shell，杜绝了另一层注入。
/// </para>
/// </remarks>
internal static class MacOSNotificationSender
{
    private const string Executable = "/usr/bin/osascript";

    /// <summary>macOS 自带 osascript，因此通知总是可用。</summary>
    internal static bool IsAvailable => File.Exists(Executable);

    internal static void Send(OrielNotificationOptions notification)
    {
        try
        {
            string script =
                "display notification \"" + Escape(notification.Body ?? string.Empty) + "\"" +
                " with title \"" + Escape(notification.Title) + "\"";

            var startInfo = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OrielWeb] 结束 osascript 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // 通知失败不影响业务
            Debug.WriteLine($"[OrielWeb] osascript 调用失败：{ex.Message}");
        }
    }

    /// <summary>把文本安全地放进 AppleScript 字符串字面量（转义反斜杠与双引号；换行照原样，AppleScript 接受）。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("\"", "\\\"", StringComparison.Ordinal);
}
