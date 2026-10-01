using System.Diagnostics;

namespace OrielWeb.Cli;

/// <summary>
/// 跑一个外部命令并取回输出——工具的所有探测都靠它。
/// </summary>
/// <remarks>
/// <b>探测失败不是异常</b>："这台机器没有 dpkg"、"没有装 wix" 都是正常结果，要对使用者报告成
/// 一条 WARN/FAIL，而不是让工具自己崩掉。所以这里不抛异常、不区分"命令不存在"与"命令失败"，
/// 只回一个 bool——调用方关心的是"这个能力能不能用"，不是"为什么不能用"。
/// </remarks>
internal static class ProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>跑命令；退出码为 0 时算成功。stdout 与 stderr 合并返回（探测常把信息写在其中一边）。</summary>
    internal static (bool Ok, string Output) TryRun(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, string.Empty);
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(Timeout))
            {
                // 探测命令挂住（等输入、等网络）不能让 doctor 一起挂住
                process.Kill(entireProcessTree: true);
                return (false, $"（命令超过 {Timeout.TotalSeconds:0} 秒未返回，已中止）");
            }

            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception)
        {
            // 命令不存在（Win32Exception）、没有执行权限……对探测来说都是"没有"
            return (false, string.Empty);
        }
    }

    /// <summary>输出里第一行非空内容（用于取版本号这类单行结果）。</summary>
    internal static string? FirstLine(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
}
