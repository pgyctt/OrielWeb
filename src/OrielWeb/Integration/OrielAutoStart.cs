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
/// <para>
/// <b>Windows 与 Linux 的引号规则不一样，不能共用一个函数</b>：
/// Windows 走 <c>CommandLineToArgvW</c> 的规则（引号前的反斜杠要成对加倍），
/// Linux 走 freedesktop 的 Exec 规则（引号内 <c>"</c> <c>`</c> <c>$</c> <c>\</c> 四个字符都必须转义）。
/// 共用一套的后果是"在某一个平台上悄悄解析错"，所以这里按平台分成两个函数。
/// </para>
/// </remarks>
internal static class AutoStartContent
{
    /// <summary>Windows 命令行里需要加引号的字符（空白与双引号）。</summary>
    private static readonly char[] WindowsQuoteTriggers = [' ', '\t', '"'];

    /// <summary>
    /// freedesktop Exec 的保留字符：出现任一就必须给参数加引号（规范 "Exec key" 一节）。
    /// </summary>
    private static readonly char[] DesktopReserved =
        [' ', '\t', '\n', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`'];

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
        builder.Append("Exec=").Append(QuoteForDesktop(executable, force: true));
        AppendArguments(builder, arguments, static argument => QuoteForDesktop(argument));
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
        var builder = new StringBuilder(QuoteForWindows(executable, force: true));
        AppendArguments(builder, arguments, static argument => QuoteForWindows(argument));
        return builder.ToString();
    }

    private static void AppendArguments(
        StringBuilder builder, IReadOnlyList<string>? arguments, Func<string, string> quote)
    {
        if (arguments is null)
        {
            return;
        }

        foreach (string argument in arguments)
        {
            builder.Append(' ').Append(quote(argument));
        }
    }

    /// <summary>
    /// 按 <c>CommandLineToArgvW</c> 的规则给参数加引号（Windows）。
    /// </summary>
    /// <remarks>
    /// 规则的关键有两条，只写"把引号换成 <c>\"</c>"是不够的：
    /// <list type="number">
    ///   <item>
    ///     反斜杠只在**紧邻引号**时才有转义含义：<c>n</c> 个反斜杠后跟 <c>"</c> 表示
    ///     <c>n/2</c> 个反斜杠加一个引号切换；n 为奇数时最后那个反斜杠被吞掉。
    ///     因此要输出一个字面引号，必须写 <c>2n+1</c> 个反斜杠。
    ///   </item>
    ///   <item>
    ///     参数**以反斜杠结尾**时，收尾引号会被前面的反斜杠转义掉，引号失衡、后续参数被吞。
    ///     所以结尾的反斜杠必须加倍。这正是"<c>--path=C:\dir\</c>"这类参数以前会写坏的原因。
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="argument">要写入的参数。</param>
    /// <param name="force">
    /// 是否无条件加引号。可执行路径用 <c>true</c>——它的值由系统直接解析，不加引号时含空格的路径
    /// 会被切成"程序 + 参数"；普通参数用默认的 <c>false</c>（不加引号更接近用户手写的形态）。
    /// </param>
    internal static string QuoteForWindows(string argument, bool force = false)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!force && argument.IndexOfAny(WindowsQuoteTriggers) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 2n+1：n 对表示 n 个字面反斜杠，多出来的一个用来转义这个引号
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // 收尾：结尾的反斜杠要加倍，否则它会转义掉下面那个收尾引号
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// 按 freedesktop Desktop Entry 的规则给 <c>Exec</c> 的参数加引号（Linux）。
    /// </summary>
    /// <remarks>
    /// 规范要求：参数含保留字符时用双引号包起来，且引号内的 <c>"</c>、<c>`</c>、<c>$</c>、<c>\</c>
    /// 四个字符**都必须**用反斜杠转义。只转义引号的话，含反斜杠的参数（例如 <c>--path=C:\temp</c>）
    /// 会被遵循规范的解析器当成转义序列，<c>\t</c> 变成制表符、参数随之损坏。
    /// </remarks>
    /// <param name="argument">要写入的参数。</param>
    /// <param name="force">是否无条件加引号（可执行路径用 <c>true</c>，理由同 <see cref="QuoteForWindows"/>）。</param>
    internal static string QuoteForDesktop(string argument, bool force = false)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!force && argument.IndexOfAny(DesktopReserved) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        foreach (char c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// desktop entry 的值转义：反斜杠与换行/回车/制表符（键名与值都不允许裸换行）。
    /// </summary>
    /// <remarks>
    /// 反斜杠必须**最先**替换，否则会把后面替换出来的转义序列再转义一遍。
    /// </remarks>
    private static string EscapeDesktop(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
}
