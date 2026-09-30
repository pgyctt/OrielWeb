using System.Text;

namespace OrielWeb;

/// <summary>
/// 开机自启配置的文本生成（三平台共用）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成纯函数只有一个目的：<b>能单测</b>。这三段文本各有格式要求（freedesktop 的 desktop entry、
/// macOS 的 plist、Windows Run 键的命令行），而它们的正确性无法靠"写进去没报错"来确认——
/// 自启项写错了不会当场失败，而是等到用户下次开机才发现没起来。所以逐字符断言。
/// </para>
/// <para>
/// 平台只负责"把这些文本放到该放的地方"（Linux 写 <c>~/.config/autostart</c>、
/// macOS 写 <c>~/Library/LaunchAgents</c>、Windows 写 <c>HKCU\...\Run</c>）。
/// </para>
/// </remarks>
internal static class AutoStartContent
{
    /// <summary>
    /// freedesktop 的 autostart 项。<c>Exec</c> 里的可执行路径**总是加引号**：
    /// 规范允许含空格的路径，而解析方对不带引号的空格按"参数分隔"处理。
    /// </summary>
    internal static string LinuxDesktopEntry(string id, string executable, IReadOnlyList<string>? arguments)
    {
        var builder = new StringBuilder();
        builder.Append("[Desktop Entry]\n");
        builder.Append("Type=Application\n");
        builder.Append("Name=").Append(EscapeDesktop(id)).Append('\n');
        builder.Append("Exec=").Append(QuoteExecutable(executable));
        AppendArguments(builder, arguments);
        builder.Append('\n');
        builder.Append("Terminal=false\n");
        // GNOME 会记住"用户曾在启动应用里关掉它"，这个键让自启项在启用时就明确生效
        builder.Append("X-GNOME-Autostart-enabled=true\n");
        return builder.ToString();
    }

    /// <summary>macOS 的 LaunchAgent（<c>RunAtLoad</c> 为 true，即登录后启动一次）。</summary>
    internal static string MacOSLaunchAgent(string id, string executable, IReadOnlyList<string>? arguments)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" ");
        builder.Append("\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\">\n");
        builder.Append("<dict>\n");
        builder.Append("    <key>Label</key>\n");
        builder.Append("    <string>").Append(EscapeXml(id)).Append("</string>\n");
        builder.Append("    <key>ProgramArguments</key>\n");
        builder.Append("    <array>\n");
        builder.Append("        <string>").Append(EscapeXml(executable)).Append("</string>\n");
        if (arguments is not null)
        {
            foreach (string argument in arguments)
            {
                builder.Append("        <string>").Append(EscapeXml(argument)).Append("</string>\n");
            }
        }
        builder.Append("    </array>\n");
        builder.Append("    <key>RunAtLoad</key>\n");
        builder.Append("    <true/>\n");
        builder.Append("</dict>\n");
        builder.Append("</plist>\n");
        return builder.ToString();
    }

    /// <summary>
    /// Windows <c>HKCU\...\Run</c> 的值：带引号的可执行路径，后面跟参数。
    /// 路径**总是加引号**——注册表这条命令由系统直接解析，含空格的路径不加引号会被当成"程序 + 参数"。
    /// </summary>
    internal static string WindowsRunValue(string executable, IReadOnlyList<string>? arguments)
    {
        var builder = new StringBuilder(QuoteExecutable(executable));
        AppendArguments(builder, arguments);
        return builder.ToString();
    }

    private static string QuoteExecutable(string executable) => "\"" + executable + "\"";

    private static void AppendArguments(StringBuilder builder, IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return;
        }

        foreach (string argument in arguments)
        {
            builder.Append(' ').Append(QuoteArgument(argument));
        }
    }

    /// <summary>参数含空格或引号时加引号（引号内的引号用反斜杠转义，Windows 与 Unix 的解析都能接受）。</summary>
    private static string QuoteArgument(string argument)
        => argument.Length == 0 || argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;

    /// <summary>desktop entry 的值里换行与反斜杠要转义（键名与值都不允许裸换行）。</summary>
    private static string EscapeDesktop(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
}
