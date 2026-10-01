using OrielWeb.Cli.Bundle;
using OrielWeb.Cli.Doctor;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OrielWeb.Tests")]

namespace OrielWeb.Cli;

/// <summary>
/// <c>oriel</c> 的入口：只负责分发子命令与把结果映射到退出码。
/// </summary>
/// <remarks>
/// 退出码语义（CI 与脚本按它判断，所以要稳定）：
/// <list type="bullet">
///   <item><description><c>0</c>：成功（doctor 的体检没有阻断项）。</description></item>
///   <item><description><c>1</c>：检查做了但结论是坏的（体检有阻断项、打包失败）。</description></item>
///   <item><description><c>2</c>：**用法错误**（不认识子命令、选项缺值、必填项没给）——
///     与 1 分开是刻意的：脚本里"我传错了参数"和"这台机器有问题"要能分开处理。</description></item>
/// </list>
/// </remarks>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitCheckFailed = 1;
    private const int ExitUsageError = 2;

    /// <summary>不需要值的选项。没列在这里的 <c>--xxx</c> 一律按"后面跟一个值"解析。</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--json", "--verbose", "--help",
    };

    private static int Main(string[] args)
    {
        ParsedCommandLine parsed = ParsedCommandLine.Parse(args, Flags);

        if (parsed.Error is { } error)
        {
            Console.Error.WriteLine($"oriel：{error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return ExitUsageError;
        }

        return parsed.Subcommand switch
        {
            "doctor" => RunDoctor(parsed),
            "bundle" => RunBundle(parsed),
            "version" or null => PrintVersion(),
            "help" => PrintHelp(),
            _ => UnknownSubcommand(parsed.Subcommand),
        };
    }

    private static int RunDoctor(ParsedCommandLine parsed)
        => DoctorCommand.Run(parsed) switch
        {
            DoctorOutcome.Ok => ExitSuccess,
            DoctorOutcome.HasBlockingProblem => ExitCheckFailed,
            _ => ExitUsageError,
        };

    private static int RunBundle(ParsedCommandLine parsed)
        => BundleCommand.Run(parsed) switch
        {
            BundleOutcome.Ok => ExitSuccess,
            BundleOutcome.Failed => ExitCheckFailed,
            _ => ExitUsageError,
        };

    private static int PrintVersion()
    {
        Console.WriteLine($"oriel {CliVersion.Value}");
        Console.WriteLine($"运行时 {Environment.Version}（{Environment.OSVersion.Platform}）");
        return ExitSuccess;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(Usage);
        return ExitSuccess;
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"oriel：未知子命令「{subcommand}」。");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return ExitUsageError;
    }

    private static readonly string Usage = string.Join(
        Environment.NewLine,
        "用法：",
        "  oriel doctor [--project <目录>] [--json]",
        "      体检本机环境；给了 --project 再体检那个应用项目的配置。",
        "      --json 输出机器可读的结论（CI 用），否则输出人读报告。",
        "",
        "  oriel bundle --dir <发布目录> --rid <RID> --name <应用名> --id <反向域名标识> --version <版本>",
        "               [--icon <图标文件>] [--out <目录>] [--manifest <json 文件>]",
        "      把一个已发布的 AOT 目录打成该平台的安装产物：",
        "        win-*   → .msi（WiX v5，per-user 安装）",
        "        osx-*   → .app + .dmg（ad-hoc 签名）",
        "        linux-* → .AppImage",
        "",
        "  oriel version        打印版本",
        "  oriel help           打印本说明",
        "",
        $"退出码：0 成功 / 1 检查未通过或打包失败 / {ExitUsageError} 用法错误");
}
