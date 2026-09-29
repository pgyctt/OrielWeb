using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 剪贴板自检：文本与 HTML 的写→读回，以及两种类型互不干扰。
/// </summary>
/// <remarks>
/// 由 <c>--clipboard-selftest</c> 启用。剪贴板 API 是同步的，所以断言在 Loaded 回调（UI 线程）里
/// 一次跑完，不需要 NavSelfTest / IpcSelfTest 那种事件驱动状态机。
///
/// 两点要说明：
/// ① 自检会**覆盖系统剪贴板内容**（这正是它的目的），不要在用户正在使用剪贴板时跑；
/// ② 写入与读回之间可能有**外部进程插手**（剪贴板管理器、正在粘贴的应用）。实测本机出现过一次
///    "两个格式都写入成功、读回却是空串"，重跑即通过。所以每次读回不符时允许**重写一次再读**，
///    仍不符才判失败——既不把外因算成库缺陷，也保留失败时的**码点诊断**（肉眼看不见的差异只能靠码点）。
/// </remarks>
internal static class ClipboardSelfTest
{
    private const string SampleText = "OrielWeb 剪贴板自检：纯文本 ✓";
    private const string SampleHtml = "<b>OrielWeb</b> 剪贴板自检";
    private const string SampleHtmlFallback = "OrielWeb 剪贴板自检";

    /// <summary>自检是否失败——demo 的 Main 据此设置进程退出码。</summary>
    internal static bool Failed { get; private set; }

    internal static void Attach(WebviewWindow window) => window.Loaded += () => Run(window);

    private static void Run(WebviewWindow window)
    {
        var failures = new List<string>();

        try
        {
            // 1. 文本往返
            string? textRead = SetAndReadText(window, SampleText);
            Report("文本", SampleText, textRead);
            Check(textRead == SampleText, $"文本往返不一致：读到 \"{textRead}\"", failures);

            // 2. HTML 往返，并确认同时写了纯文本回退
            void WriteHtml() => window.SetClipboardHtml(SampleHtml, SampleHtmlFallback);

            WriteHtml();
            string? htmlRead = ReadWithRetry(() => window.ClipboardHtml, SampleHtml, WriteHtml);
            Report("HTML", SampleHtml, htmlRead);
            Check(htmlRead == SampleHtml, $"HTML 往返不一致：读到 \"{htmlRead}\"", failures);

            string? fallbackRead = ReadWithRetry(() => window.ClipboardText, SampleHtmlFallback, WriteHtml);
            Report("HTML 的纯文本回退", SampleHtmlFallback, fallbackRead);
            Check(fallbackRead == SampleHtmlFallback,
                $"写 HTML 时应同时给出纯文本回退，实际读到 \"{fallbackRead}\"", failures);

            // 3. 只写文本时不应读到 HTML（两种类型互不干扰）
            window.SetClipboardText(SampleText);
            string? htmlAfterText = window.ClipboardHtml;
            Check(htmlAfterText is null,
                $"只写文本后不应读到 HTML，实际读到 \"{htmlAfterText}\"", failures);
        }
        catch (Exception ex)
        {
            failures.Add($"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
        }

        Failed = failures.Count > 0;
        Console.WriteLine(Failed
            ? $"CLIPBOARD-SELFTEST: FAIL（{failures.Count} 项）"
            : "CLIPBOARD-SELFTEST: PASS");
        foreach (string failure in failures)
        {
            Console.WriteLine("  - " + failure);
        }
        Console.Out.Flush();

        // 与另两个自检一致：关窗 → 消息循环结束 → Run() 返回 → Main 按 Failed 设置退出码
        window.Close();
    }

    private static string? SetAndReadText(WebviewWindow window, string text)
        => ReadWithRetry(() => window.ClipboardText, text, () => window.SetClipboardText(text));

    /// <summary>读一次；与期望不符时重写再读一次（原因见类注释）。</summary>
    private static string? ReadWithRetry(Func<string?> read, string expected, Action rewrite)
    {
        string? value = read();
        if (value == expected)
        {
            return value;
        }

        rewrite();
        return read();
    }

    /// <summary>不符时打印码点——"看着一样但不相等"只能这样定位。</summary>
    private static void Report(string what, string expected, string? actual)
    {
        if (actual == expected)
        {
            return;
        }

        Console.WriteLine($"[clipboard] {what} 不符：期望 {Describe(expected)}；实际 {Describe(actual)}");
    }

    /// <summary>渲染成"长度 + 每个字符的码点"。</summary>
    private static string Describe(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("len=").Append(value.Length).Append(" [");
        foreach (char c in value)
        {
            builder.Append(((int)c).ToString("X4")).Append(' ');
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static void Check(bool ok, string message, List<string> failures)
    {
        if (!ok)
        {
            failures.Add(message);
        }
    }
}
