using System.Text;
using System.Text.Json;

namespace OrielWeb.Cli.Doctor;

/// <summary><c>oriel doctor</c> 的结果（映射到进程退出码）。</summary>
internal enum DoctorOutcome
{
    /// <summary>没有阻断项。</summary>
    Ok,

    /// <summary>有阻断项（退出码 1）。</summary>
    HasBlockingProblem,

    /// <summary>用法错误（退出码 2）。</summary>
    UsageError,
}

/// <summary>
/// <c>oriel doctor</c>：体检本机（可选：再体检一个项目）。
/// </summary>
/// <remarks>
/// 汇总与输出都在这里，而不是让每节自己打印：一次 doctor 可能有两节（本机 + 项目），
/// 而**结论行只能有一行**——CI 与脚本是按最后一行判定的，两行会互相覆盖。
/// </remarks>
internal static class DoctorCommand
{
    internal static DoctorOutcome Run(ParsedCommandLine parsed)
    {
        var reports = new List<CheckReport> { SystemChecks.Run() };

        if (parsed.Value("--project") is { Length: > 0 } project)
        {
            if (!Directory.Exists(project) && !File.Exists(project))
            {
                Console.Error.WriteLine($"oriel doctor：--project 指向的路径不存在：{project}");
                return DoctorOutcome.UsageError;
            }

            reports.Add(ProjectChecks.Run(project));
        }

        List<ManualStep> manual = [.. ManualChecks.ForCurrentPlatform()];
        bool blocking = reports.Any(report => report.HasBlockingProblem);

        Console.WriteLine(parsed.Has("--json")
            ? BuildJson(reports, manual, blocking)
            : BuildText(reports, manual, blocking));

        return blocking ? DoctorOutcome.HasBlockingProblem : DoctorOutcome.Ok;
    }

    private static string BuildText(List<CheckReport> reports, List<ManualStep> manual, bool blocking)
    {
        var text = new StringBuilder();
        foreach (CheckReport report in reports)
        {
            text.Append(report.ToText());
        }

        int fail = reports.Sum(report => report.Count(CheckStatus.Fail));
        int warn = reports.Sum(report => report.Count(CheckStatus.Warn));
        text.AppendLine();
        text.AppendLine(blocking
            ? $"体检结论：FAIL（{fail} 项阻断、{warn} 项提示）"
            : $"体检结论：PASS（0 项阻断、{warn} 项提示）");

        if (manual.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("=== 接下来需要人眼验证的 ===");
            text.AppendLine("（本工具判不了这些——它们要么是「看到才算数」，要么需要一块屏幕；这里只告诉你验什么、该看到什么）");
            foreach (ManualStep step in manual)
            {
                text.AppendLine();
                text.AppendLine($"· {step.RoadmapSection}");
                text.AppendLine($"    命令：{step.Command}");
                text.AppendLine($"    预期：{step.Expectation}");
            }

            text.AppendLine();
            text.AppendLine("清单全文与「若不符」的排查入口：docs/ROADMAP.md");
        }

        return text.ToString();
    }

    private static string BuildJson(List<CheckReport> reports, List<ManualStep> manual, bool blocking)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", "oriel doctor");
            writer.WriteString("version", CliVersion.Value);
            writer.WriteString("platform", SystemChecks.PlatformName());
            writer.WriteBoolean("ok", !blocking);

            writer.WriteStartObject("summary");
            writer.WriteNumber("pass", reports.Sum(report => report.Count(CheckStatus.Pass)));
            writer.WriteNumber("warn", reports.Sum(report => report.Count(CheckStatus.Warn)));
            writer.WriteNumber("fail", reports.Sum(report => report.Count(CheckStatus.Fail)));
            writer.WriteEndObject();

            writer.WriteStartArray("checks");
            foreach (CheckReport report in reports)
            {
                foreach (CheckItem item in report.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("section", report.Heading);
                    writer.WriteString("id", item.Id);
                    writer.WriteString("title", item.Title);
                    writer.WriteString("status", CheckReport.JsonLabel(item.Status));
                    writer.WriteString("detail", item.Detail);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();

            writer.WriteStartArray("manualChecks");
            foreach (ManualStep step in manual)
            {
                writer.WriteStartObject();
                writer.WriteString("roadmapSection", step.RoadmapSection);
                writer.WriteString("command", step.Command);
                writer.WriteString("expectation", step.Expectation);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
