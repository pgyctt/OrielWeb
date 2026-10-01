using System.Text.Json;
using OrielWeb;
using OrielWeb.Ipc;

namespace OrielDemo;

/// <summary>
/// 能力模型自检（无人交互）：让**页面真实调用**两条命令，断言一条能通、一条被拒。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不让宿主直接调内部判定函数了事：能力模型的三层门禁（来源 / 令牌 / 命令授权）
/// 分散在桥接脚本、窗口后端与分发器里，"配置写对了"和"消息真的走通了"是两件事——
/// 只有从页面发出真实 <c>oriel.invoke</c> 才能覆盖整条链路。
/// </para>
/// <para>
/// <b>能断言的</b>：允许名单内的命令能往返并拿到返回值；没声明的命令被拒且**没有被执行**
/// （被拒时回显的是错误，而不是探针命令的返回值）；页面侧真的装上了桥接（否则第一条回显收不到）。
/// <b>不能断言的</b>：Debug / Release 两种默认姿态（那由 <c>CapabilityTests</c> 的纯函数单测覆盖，
/// 这里跑的是 demo 自己的显式配置）、以及"远程页面拿不到桥接"（无头环境里没有远程页）。
/// </para>
/// </remarks>
internal static class CapabilitySelfTest
{
    /// <summary>在允许名单内的探针命令。</summary>
    internal const string AllowedCommand = "cap.allowed";

    /// <summary>不在允许名单里的探针命令——它一旦真的被执行，就会返回一个可识别的字符串。</summary>
    internal const string DeniedCommand = "cap.denied";

    /// <summary>探针命令"不该被调用"的返回值（被拒时自检必须看不到它）。</summary>
    internal const string DeniedMarker = "denied-command-executed";

    /// <summary>页面回报结果用的消息名（单向消息没有命令名，不受能力配置约束）。</summary>
    private const string ReportName = "cap-report";

    /// <summary>允许名单内那条命令的返回值（探针命令与断言共用一份，避免两处各写一个字面量）。</summary>
    internal const string AllowedValue = "allowed-command-ok";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _window;
    private static Timer? _watchdog;
    private static bool _finished;
    private static JsonElement? _allowed;
    private static JsonElement? _denied;

    /// <summary>自检是否失败——demo 的 Main 据此设置进程退出码。</summary>
    internal static bool Failed
    {
        get
        {
            lock (Gate)
            {
                return Failures.Count > 0;
            }
        }
    }

    internal static void Attach(WebviewWindow window)
    {
        _window = window;

        window.MessageReceived += args =>
        {
            Console.WriteLine($"[capability-selftest] message {args.Name} {args.Json}");
            if (args.Name != ReportName)
            {
                return;
            }

            JsonElement report;
            try
            {
                report = JsonDocument.Parse(args.Json).RootElement;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[capability-selftest] 回报不是合法 JSON：{ex.Message}");
                return;
            }

            if (!report.TryGetProperty("which", out JsonElement which) || which.ValueKind != JsonValueKind.String)
            {
                return;
            }

            if (which.GetString() == "allowed")
            {
                _allowed ??= report;
            }
            else if (which.GetString() == "denied")
            {
                _denied ??= report;
            }

            TryFinish();
        };

        window.Loaded += Begin;
    }

    private static void Begin()
    {
        _watchdog = new Timer(
            _ =>
            {
                lock (Gate)
                {
                    if (_finished)
                    {
                        return;
                    }
                    Failures.Add(
                        $"能力自检超时：30 秒内没等到两条页面回显（allowed={_allowed is not null}, " +
                        $"denied={_denied is not null}）");
                }
                _window?.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(30),
            Timeout.InfiniteTimeSpan);

        // 页面侧把两条命令的结果都回收到宿主。用 postMessage 而不是再 invoke：
        // 单向消息不带命令名，因此这条回显通道本身不受能力配置影响（否则被拒的命令连"我被拒了"都报不出来）。
        string pageScript = ProbeScript(AllowedCommand, "allowed") + ProbeScript(DeniedCommand, "denied");
        _ = _window!.EvaluateJs(pageScript);
    }

    private static string ProbeScript(string command, string which)
        => $"window.oriel.invoke('{command}').then("
           + $"function (value) {{ window.oriel.postMessage('{ReportName}', "
           + $"{{ which: '{which}', ok: true, value: String(value) }}); }}, "
           + $"function (error) {{ window.oriel.postMessage('{ReportName}', "
           + $"{{ which: '{which}', ok: false, error: String(error && error.message) }}); }});";

    private static void TryFinish()
    {
        if (_finished || _allowed is null || _denied is null)
        {
            return;
        }

        JsonElement allowed = _allowed.Value;
        JsonElement denied = _denied.Value;

        Check(
            allowed.TryGetProperty("ok", out JsonElement allowedOk) && allowedOk.GetBoolean(),
            $"允许名单内的命令 {AllowedCommand} 应当调通，实际：{allowed.GetRawText()}");
        Check(
            allowed.TryGetProperty("value", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == AllowedValue,
            $"允许名单内的命令应返回 {AllowedValue}，实际：{allowed.GetRawText()}");

        Check(
            denied.TryGetProperty("ok", out JsonElement deniedOk) && !deniedOk.GetBoolean(),
            $"未声明的命令 {DeniedCommand} 应当被拒，实际：{denied.GetRawText()}");

        string? deniedError = denied.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        Check(
            deniedError is not null && deniedError.Contains(DeniedCommand, StringComparison.Ordinal),
            $"拒绝理由里应点名该命令，实际：\"{deniedError}\"");
        // 拒绝路径的文案分两种（落进 Deny 名单 / 不在 Allow 名单）。这里断言的是后者，
        // 因为 demo 只声明了 Allow：两句话不能混成一句，否则"我明明加进 Allow 了却不生效"无从排查。
        Check(
            deniedError is not null && deniedError.Contains("Allow", StringComparison.Ordinal),
            $"拒绝理由应说明它不在 Allow 名单里，实际：\"{deniedError}\"");
        Check(
            deniedError is null || !deniedError.Contains(DeniedMarker, StringComparison.Ordinal),
            $"被拒的命令不该被执行到（拒绝发生在分发之前）：\"{deniedError}\"");

        Finish();
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        lock (Gate)
        {
            Failures.Add(description);
        }
    }

    private static void Finish()
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
        }

        _watchdog?.Dispose();

        int failureCount;
        lock (Gate)
        {
            failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "CAPABILITY-SELFTEST: PASS"
                : $"CAPABILITY-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        _window?.Close();
    }
}

/// <summary>
/// 能力自检用的两个探针命令：一个在 demo 的允许名单里、一个不在。
/// </summary>
/// <remarks>
/// 被拒的那条**故意有副作用可识别**：它若真的被执行，返回的 <see cref="CapabilitySelfTest.DeniedMarker"/>
/// 会让自检失败——"命令被拒"与"命令压根没跑到"是两件事，前者才是能力模型要保证的。
/// </remarks>
public sealed partial class CapabilityProbeCommands
{
    [OrielCommand(CapabilitySelfTest.AllowedCommand)]
    public static string Allowed() => CapabilitySelfTest.AllowedValue;

    [OrielCommand(CapabilitySelfTest.DeniedCommand)]
    public static string Denied() => CapabilitySelfTest.DeniedMarker;
}
