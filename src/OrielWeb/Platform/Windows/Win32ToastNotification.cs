using System.Diagnostics;
using System.Text;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 通知：经一个短命的 <b>Windows PowerShell 5.1</b> 进程调用 WinRT 的 Toast。
/// </summary>
/// <remarks>
/// <para>
/// 思路来自参考实现 Ryn（它的注释写明这条路径 "reliable for any app"）：NativeAOT 下没有 WinRT 投影，
/// 但 <c>ToastNotificationManager</c> 通过 PowerShell 调用照样可用，而且**不依赖托盘图标存在**。
/// 早先走的是托盘气球（<c>NIF_INFO</c>）：既要求托盘在、实测里又根本没显示出来——
/// 换来的是能回传点击，但看不见的通知谈不上"能用"。
/// </para>
/// <para>
/// <b>两处与 Ryn 的差异，都是真机上踩出来的</b>：
/// </para>
/// <list type="number">
/// <item>
/// <b>必须用 5.1 的绝对路径，不能用裸名 <c>powershell</c></b>。WinRT 类型投影只存在于
/// .NET Framework 版的 Windows PowerShell 里，而装了 PowerShell 7 的机器上，PATH 里的
/// <c>powershell</c> 会解析到 pwsh（Core）——那里连
/// <c>[Windows.UI.Notifications.X, …, ContentType=WindowsRuntime]</c> 这种语法都不支持，
/// 报"找不到类型"，通知静默失败（本机实测：路径里就是 PS 7.6）。
/// </item>
/// <item>
/// <b><c>XmlDocument</c> 也要带 <c>ContentType = WindowsRuntime</c> 限定符</b>（或者干脆用
/// <c>New-Object</c>，它按已加载的投影解析）。只给 <c>ToastNotificationManager</c> 加限定符、
/// 让 <c>XmlDocument</c> 走普通类型引用，在 5.1 里会报"找不到类型"。
/// </item>
/// </list>
/// <para>
/// 代价是**拿不到点击激活**：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID
/// 并注册 COM 激活器，那是打包器的职责（Ryn 也把这一项列为已知缺口）。因此
/// <c>NotificationClicked</c> 在 Windows 上也改成显式空实现——三平台如实一致（都拿不到），
/// 而不是让某一个平台看起来支持。
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
    /// AUMID。未打包运行时通知会归到这个标识下（在系统"通知"设置里按它出现）。
    /// 实测未注册的 AUMID 也能投递（<c>CreateToastNotifier</c> 不拒绝）。
    /// </summary>
    private const string AppUserModelId = "OrielWeb";

    /// <summary>Windows PowerShell 5.1 的位置（WinRT 投影只在它上面可用）。</summary>
    private static readonly Lazy<string> s_powerShell = new(FindWindowsPowerShell);

    /// <summary>投递一条 toast。</summary>
    /// <returns>脚本是否成功执行（PowerShell 退出码为 0）。</returns>
    internal static bool Send(OrielNotificationOptions notification)
    {
        try
        {
            string toastXml = string.Concat(
                "<toast><visual><binding template=\"ToastText02\">",
                "<text id=\"1\">", EscapeForXmlAndScript(notification.Title), "</text>",
                "<text id=\"2\">", EscapeForXmlAndScript(notification.Body ?? string.Empty), "</text>",
                "</binding></visual></toast>");

            // Tag 只有非空才设：空字符串不是一个合法的 tag
            string tagPart = string.IsNullOrWhiteSpace(notification.Id)
                ? string.Empty
                : $"$toast.Tag = '{EscapeForXmlAndScript(notification.Id)}'; ";

            string script = string.Concat(
                // 两行限定符引用**缺一不可**：WinRT 类型投影要先被"带 ContentType 限定符的类型引用"
                // 加载，之后的 New-Object 才解析得到。只加载 ToastNotificationManager 而不加载
                // XmlDocument 时，下一行的 New-Object 会报
                // "Cannot find type [Windows.Data.Xml.Dom.XmlDocument]"，整条通知静默失败
                //（真机实测：三种传参方式一起失败，错误都指向这一行）。
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; ",
                "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; ",
                "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; ",
                "$xml.LoadXml('", toastXml, "'); ",
                "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml; ",
                tagPart,
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('", AppUserModelId, "').Show($toast)");

            // 用 -EncodedCommand（UTF-16LE 的 Base64）而不是 -Command：脚本里带着应用给的标题与正文，
            // 走命令行参数时非 ASCII 内容会按本地代码页解释而乱码（实测中文标题直接变成乱码，
            // 通知随之失败）。编码后整条命令只剩 ASCII，彻底绕开这一层。
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var startInfo = new ProcessStartInfo(s_powerShell.Value)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);

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
                // 把脚本自己的报错记下来：否则"通知没出现"就只剩"没反应"这一种说法
                Debug.WriteLine($"[OrielWeb] 通知投递失败（退出码 {process.ExitCode}）：{process.StandardError.ReadToEnd()}");
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
    /// 定位 Windows PowerShell 5.1。
    /// </summary>
    /// <remarks>
    /// 找 5.1 的固定安装位置，而不是 PATH 里的 <c>powershell</c>：在装了 PowerShell 7 的机器上，
    /// 后者是 pwsh（Core），而 WinRT 类型投影只在 .NET Framework 版上可用（实测报"找不到类型"）。
    /// 找不到 5.1（理论上不存在，它是系统组件）时退回裸名，让失败停在"能诊断"的位置。
    /// </remarks>
    private static string FindWindowsPowerShell()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(path))
        {
            return path;
        }

        Debug.WriteLine("[OrielWeb] 未找到 Windows PowerShell 5.1，退回 PATH 中的 powershell（WinRT 可能不可用）。");
        return "powershell";
    }

    /// <summary>
    /// 同时为 XML 文本节点与单引号字符串字面量转义。
    /// </summary>
    /// <remarks>
    /// 五个替换各有分工：前三个挡住"内容提前闭合 XML 标签"，第四个挡住属性里的引号，
    /// 第五个挡住"内容提前闭合 PowerShell 单引号字符串"。顺序也有讲究——
    /// <c>&amp;</c> 必须最先替换，否则会把后面替换出来的实体再转义一遍。
    /// </remarks>
    private static string EscapeForXmlAndScript(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal);
}
