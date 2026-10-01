namespace OrielWeb.Cli;

/// <summary>
/// 极简命令行解析：<c>子命令 [--名字 值] [--标志]</c>。
/// </summary>
/// <remarks>
/// <para>
/// 不引 <c>System.CommandLine</c>：本工具的选项集固定且小，多一个依赖不划算。而"选项名写错"、
/// "值被当成子命令"这类静默故障在工具里代价很大（打包参数错了会打出个错的包），所以解析做成
/// 纯函数，好让它被逐例单测。
/// </para>
/// <para>
/// 特别地：**不在已知标志集合里的选项缺少值时报错**，而不是顺手当成标志——
/// 后者会让 <c>--dir --json</c> 变成"dir 没给值但继续往下跑"。
/// </para>
/// </remarks>
internal sealed class ParsedCommandLine
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    private ParsedCommandLine()
    {
    }

    /// <summary>子命令（第一个非 <c>--</c> 参数）；没给时为 null。</summary>
    internal string? Subcommand { get; private set; }

    /// <summary>解析失败的原因；成功时为 null。</summary>
    internal string? Error { get; private set; }

    /// <summary>子命令之后的位置参数。</summary>
    internal IReadOnlyList<string> Positionals => _positionals;

    /// <summary>选项的值；该选项没出现或没给值时返回 null。</summary>
    internal string? Value(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    /// <summary>该选项出现过（无论带值还是作为标志）。</summary>
    internal bool Has(string name) => _options.ContainsKey(name) || _flags.Contains(name);

    /// <param name="args">原始命令行参数（不含可执行文件名）。</param>
    /// <param name="knownFlags">不需要值的选项名，如 <c>--json</c>。</param>
    internal static ParsedCommandLine Parse(string[] args, IReadOnlySet<string> knownFlags)
    {
        var parsed = new ParsedCommandLine();

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument.Length == 0)
            {
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (parsed.Subcommand is null)
                {
                    parsed.Subcommand = argument;
                }
                else
                {
                    parsed._positionals.Add(argument);
                }

                continue;
            }

            // --name=value 形式：值里可以有空格，脚本里更安全
            string name = argument;
            string? inlineValue = null;
            int equals = argument.IndexOf('=');
            if (equals > 2)
            {
                name = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }

            if (knownFlags.Contains(name))
            {
                parsed._flags.Add(name);
                continue;
            }

            if (inlineValue is not null)
            {
                parsed._options[name] = inlineValue;
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail($"选项 {name} 缺少值。");
            }

            parsed._options[name] = args[++i];
        }

        return parsed;
    }

    private static ParsedCommandLine Fail(string reason) => new() { Error = reason };
}
