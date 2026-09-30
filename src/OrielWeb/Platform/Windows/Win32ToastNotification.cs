using System.Diagnostics;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 通知：经一个短命的 PowerShell 进程调用 WinRT 的 Toast。
/// </summary>
/// <remarks>
/// <para>
/// 照参考实现 Ryn 的做法（它的注释写明这条路径 "reliable for any app"）：NativeAOT 下没有 WinRT 投影，
/// 但 <c>ToastNotificationManager</c> 通过 PowerShell 调用照样可用，而且**不依赖托盘图标存在**。
/// 早先走的是托盘气球（<c>NIF_INFO</c>）：既要求托盘在、又在实测里根本没显示出来——
/// 换来的是能回传点击，但看不见的通知谈不上"能用"。
/// </para>
/// <para>
/// 代价是**拿不到点击激活**：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID
/// 并注册 COM 激活器，那是打包器的职责。所以 <c>NotificationClicked</c> 在 Windows 上也改成
/// 显式空实现——三平台在这件事上如实一致（都拿不到），而不是让某一个平台看起来支持。
/// </para>
/// <para>
/// <b>转义是安全关键</b>：标题与正文来自应用（可能是页面数据），会被拼进一段 PowerShell 脚本
/// 与一段 XML 里。XML 元字符与单引号都必须转义；脚本经
/// <see cref="ProcessStartInfo.ArgumentList"/> 传入，不经过 cmd/shell。
/// </para>
/// </remarks>
internal static class Win32ToastNotification
{
    /// <summary>
    /// AUMID。未打包运行时的通知会归到这个标识下（在系统"通知"设置里按它出现）。
    /// </summary>
    private const string AppUserModelId = "OrielWeb";

    /// <summary>投递一条 toast。</summary>
    /// <returns>PowerShell 是否以退出码 0 结束（脚本自身出错时为非 0）。</returns>
    internal static bool Send(OrielNotificationOptions notification)
    {
        try
        {
            string title = EscapeForXmlAndScript(notification.Title);
            string body = EscapeForXmlAndScript(notification.Body ?? string.Empty);
            string tag = EscapeForXmlAndScript(notification.Id ?? string.Empty);

            string script = string.Concat(
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; ",
                "$t = '<toast><visual><binding template=\"ToastText02\">",
                $"<text id=\"1\">{title}</text><text id=\"2\">{body}</text>",
                "</binding></visual></toast>'; ",
                "$xml = [Windows.Data.Xml.Dom.XmlDocument]::new(); $xml.LoadXml($t); ",
                "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); ",
                $"$toast.Tag = '{tag}'; ",
                $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppUserModelId}').Show($toast)");

            var startInfo = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // 上限给得宽一点：冷启 PowerShell 加 WinRT 反射，实测在半秒到两秒之间
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OrielWeb] 结束 PowerShell 失败：{ex.Message}");
                }

                return false;
            }

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[OrielWeb] 通知投递失败（PowerShell 退出码 {process.ExitCode}）。");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 通知失败不该影响业务：记一笔就走
            Debug.WriteLine($"[OrielWeb] 调用 PowerShell 投递通知失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 同时为 XML 文本节点与单引号字符串字面量转义。
    /// </summary>
    /// <remarks>
    /// 六个替换缺一不可：前三个挡住"内容提前闭合 XML 标签"，
    /// 后两个挡住"内容提前闭合 PowerShell 单引号字符串"。顺序也有讲究——
    /// <c>&amp;</c> 必须最先替换，否则会把后面替换出来的实体再转义一遍。
    /// </remarks>
    private static string EscapeForXmlAndScript(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal);
}
