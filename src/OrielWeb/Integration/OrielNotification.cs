using System.Text;

namespace OrielWeb;

/// <summary>系统通知的内容与标识。</summary>
public sealed class OrielNotificationOptions
{
    /// <summary>标题（多数平台会加粗显示；不可为空）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文（可省略）。</summary>
    public string? Body { get; set; }

    /// <summary>附加图标文件路径（部分平台忽略）。</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// 通知标识：用户点击该通知时，<see cref="OrielApp.NotificationClicked"/> 回传的就是它。
    /// 平台间差异较大（见 README 平台矩阵）：<b>三平台当前都拿不到点击</b>，
    /// 因此这个字段现在只作为"这条通知的身份"传给系统（Windows 上是 toast 的 <c>Tag</c>）。
    /// </summary>
    public string? Id { get; set; }
}

/// <summary>
/// 系统通知的"应用标识"规范化（Windows 上即 AUMID，Linux 上是 <c>notify-send --app-name</c>）。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能单测：这段规范化的输出会直接出现在系统的"通知"设置里，
/// 写错了不会报错，只会让用户在设置里看到一个奇怪的名字（甚至几条通知归不到一起）。
/// <para>
/// 约束来自 Windows 对 AUMID 的规定：**不超过 128 个字符、不能含空格**。
/// 其余非法字符一并换成 <c>_</c>，避免把路径分隔符、引号之类带进注册表与命令行。
/// </para>
/// </remarks>
internal static class OrielNotificationAppId
{
    /// <summary>取不到任何可用名字时的兜底（也是本库在 0.1.x 上的历史值）。</summary>
    internal const string Fallback = "OrielWeb";

    /// <summary>AUMID 的长度上限（Windows 规定）。</summary>
    internal const int MaxLength = 128;

    /// <summary>
    /// 把任意字符串规范化成可用的应用标识；空白或全部非法时返回 <see cref="Fallback"/>。
    /// </summary>
    internal static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (char c in raw)
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            // 只留"标识符安全"的字符：字母数字与 . - _ +（含非 ASCII 字母，程序集名可能是中文）
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' ? c : '_');
        }

        return builder.Length == 0 ? Fallback : builder.ToString();
    }
}
