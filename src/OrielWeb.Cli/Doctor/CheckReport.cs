using System.Text;

namespace OrielWeb.Cli.Doctor;

/// <summary>单项体检的结论。</summary>
internal enum CheckStatus
{
    /// <summary>通过。</summary>
    Pass,

    /// <summary>不阻断，但值得看一眼（可选工具缺失之类）。</summary>
    Warn,

    /// <summary>阻断：按这个结论，应用在这台机器上跑不起来，或跑不到预期。</summary>
    Fail,

    /// <summary>不适用（别的平台才有这一项）。不参与统计。</summary>
    NotApplicable,
}

/// <summary>一条体检结论。</summary>
/// <param name="Id">稳定的标识（JSON 里用它，脚本据此判断某一条有没有变）。</param>
/// <param name="Title">人读的标题。</param>
/// <param name="Status">结论。</param>
/// <param name="Detail">
/// 证据或修法。**必须能据此行动**——只写"失败"等于把问题原样丢回给使用者。
/// 带上"实际读到什么"和"该怎么做"。
/// </param>
internal sealed record CheckItem(string Id, string Title, CheckStatus Status, string Detail)
{
    internal static CheckItem Pass(string id, string title, string detail)
        => new(id, title, CheckStatus.Pass, detail);

    internal static CheckItem Warn(string id, string title, string detail)
        => new(id, title, CheckStatus.Warn, detail);

    internal static CheckItem Fail(string id, string title, string detail)
        => new(id, title, CheckStatus.Fail, detail);

    internal static CheckItem NotApplicable(string id, string title, string detail)
        => new(id, title, CheckStatus.NotApplicable, detail);
}

/// <summary>
/// 一节体检：一个标题 + 若干条目。
/// </summary>
/// <remarks>
/// 刻意**不含**总结与"人眼验证指引"：那两样属于整次 doctor 的输出（本机一节 + 项目一节时
/// 只能各出现一次），由 <see cref="DoctorCommand"/> 统一拼——否则两节各打一个结论行，
/// 而 CI 是按最后一行结论判定的，两行会互相覆盖。
/// </remarks>
internal sealed record CheckReport(string Heading)
{
    internal List<CheckItem> Items { get; } = [];

    internal CheckReport Add(CheckItem item)
    {
        Items.Add(item);
        return this;
    }

    internal bool HasBlockingProblem => Items.Any(item => item.Status == CheckStatus.Fail);

    internal int Count(CheckStatus status) => Items.Count(item => item.Status == status);

    internal string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"=== {Heading} ===");
        foreach (CheckItem item in Items)
        {
            text.AppendLine($"  [{Label(item.Status)}] {item.Title}：{item.Detail}");
        }

        return text.ToString();
    }

    internal static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        CheckStatus.Fail => "FAIL",
        _ => "N/A ",
    };

    internal static string JsonLabel(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Warn => "warn",
        CheckStatus.Fail => "fail",
        _ => "not-applicable",
    };
}

/// <summary>
/// 一项**只能人眼判定**的验证，以及该怎么验、该看到什么。
/// </summary>
/// <param name="RoadmapSection">
/// ROADMAP「待真机验证清单」里对应那一节的标题（逐字符，不含 <c>####</c>）。
/// 有单测断言这些名字都真的出现在文档里——否则代码与文档会各自漂移，
/// 而"文档里写的检查项在工具里找不到"正是 ROADMAP 要求避免的那种失配。
/// </param>
/// <param name="Command">在这一步该跑的命令。</param>
/// <param name="Expectation">应当看到什么（看不到时按 ROADMAP 那一节的「若不符」排查）。</param>
internal sealed record ManualStep(string RoadmapSection, string Command, string Expectation);
