using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OrielWeb.Cli.Doctor;

/// <summary>
/// 从 csproj 与源码里读出来的事实（<see cref="ProjectChecks"/> 的判定输入）。
/// </summary>
/// <remarks>
/// 单独一个 record 是为了让判定成为纯函数：测试可以摆出各种"事实组合"直接断言结论，
/// 不必真的造一个工程目录出来。而"读文件"那一半薄到不值得测。
/// </remarks>
internal sealed record ProjectFacts(
    string? TargetFrameworks,
    bool PublishAot,
    string? ApplicationIcon,
    bool HasWwwrootDirectory,
    bool HasDottedFileNameInWwwroot,
    bool DeclaresWwwrootResource,
    bool DeclaredWwwrootResourceHasLogicalName,
    string? AssetHost,
    bool DeclaresCapabilities,
    bool CallsWithIcon,
    string? ParseError);

/// <summary>
/// 一个应用项目的配置体检：按这份配置发布出去，页面能不能加载、命令能不能调。
/// </summary>
/// <remarks>
/// 这些都是**发布后才暴露**的坑，而且症状是"白屏"或"点了没反应"：
/// <list type="bullet">
///   <item><description>手写了 <c>&lt;EmbeddedResource Include="wwwroot\**\*" /&gt;</c> 却漏了
///     <c>LogicalName</c>：目录分隔符被压成 <c>.</c>，含点文件名（<c>app.min.js</c>）必然解压错，
///     页面 404 白屏（阶段 D 第 1 项消灭的就是它）。</description></item>
///   <item><description>没调用 <c>UseCapabilities</c>：Release 构建下所有 IPC 命令被拒
///     （安全模型的 fail-closed），表现为"按钮全没反应"。</description></item>
///   <item><description>没给图标文件：Linux/macOS 上是通用图标——那两个平台不会从 exe 取图标。</description></item>
/// </list>
/// 在构建之前检查最省事，这也是它值得做成工具而不是写在文档里的原因。
/// </remarks>
internal static class ProjectChecks
{
    internal static CheckReport Run(string projectPath)
    {
        string directory = Path.GetFullPath(projectPath);
        var report = new CheckReport($"项目配置（{directory}）");

        string? csprojPath = ResolveCsproj(directory);
        if (csprojPath is null)
        {
            return report.Add(CheckItem.Fail(
                "csproj", "项目文件", $"{directory} 下没有 .csproj（--project 要给应用项目所在目录）"));
        }

        ProjectFacts facts = Read(csprojPath, directory);

        // TargetFramework 常常不在 csproj 里而是继承来的（本仓库自己就是这么做的），
        // 所以读不到时再沿目录树向上找 Directory.Build.props——否则"读到空"会被报成阻断项，
        // 而那是**工具的误报**：项目本身完全正常。
        if (facts.TargetFrameworks is null && InheritedProperty(directory, "TargetFramework") is { } inherited)
        {
            facts = facts with { TargetFrameworks = inherited };
        }

        foreach (CheckItem item in Evaluate(facts, Path.GetFileNameWithoutExtension(csprojPath)))
        {
            report.Add(item);
        }

        return report;
    }

    internal static string? ResolveCsproj(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // 目录里直接给 csproj 路径的情况也接受
        if (File.Exists(directory) && directory.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return directory;
        }

        return Directory.EnumerateFiles(directory, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    private static ProjectFacts Read(string csprojPath, string directory)
    {
        string csproj = File.ReadAllText(csprojPath);

        var sources = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            // obj/bin 里有生成代码（含中间产物），扫进去会得到一堆假阳性
            if (IsBuildOutput(file))
            {
                continue;
            }

            sources.Add(File.ReadAllText(file));
        }

        string wwwroot = Path.Combine(directory, "wwwroot");
        return Parse(csproj, sources, Directory.Exists(wwwroot), HasDottedFileName(wwwroot));
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// wwwroot 里有没有"文件名主干含点"的文件（如 <c>app.min.js</c>）。
    /// </summary>
    /// <remarks>
    /// 它是那条静默缺陷的触发条件：目录分隔符被压成 <c>.</c> 之后，库只能靠"最后一个点是扩展名"反推目录，
    /// 而文件名主干里还有点时必然推错。所以"手写资源缺 LogicalName"这件事：
    /// **有这类文件 = 已经坏了**（页面 404），**没有 = 还没坏但迟早会**——两种结论不该同等级。
    /// </remarks>
    private static bool HasDottedFileName(string wwwrootDirectory)
    {
        if (!Directory.Exists(wwwrootDirectory))
        {
            return false;
        }

        return Directory
            .EnumerateFiles(wwwrootDirectory, "*", SearchOption.AllDirectories)
            .Any(file => Path.GetFileNameWithoutExtension(file).Contains('.', StringComparison.Ordinal));
    }

    /// <summary>
    /// 沿目录树向上找 <c>Directory.Build.props</c> 里的某个属性（csproj 没显式声明时用）。
    /// </summary>
    private static string? InheritedProperty(string directory, string propertyName)
    {
        DirectoryInfo? current = new(directory);
        while (current is not null)
        {
            string propsPath = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(propsPath) && ReadSingleProperty(propsPath, propertyName) is { Length: > 0 } value)
            {
                return value;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ReadSingleProperty(string propsPath, string propertyName)
    {
        try
        {
            return XDocument
                .Parse(File.ReadAllText(propsPath))
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == propertyName)
                ?.Value.Trim();
        }
        catch (Exception)
        {
            // 读不动就当没有：这是"补一条信息"的路径，不该让体检失败在这里
            return null;
        }
    }

    /// <summary>纯函数：csproj 文本 + 源码文本 → 事实。</summary>
    internal static ProjectFacts Parse(
        string csprojText, IReadOnlyList<string> sourceTexts, bool hasWwwrootDirectory, bool hasDottedFileName = false)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(csprojText);
        }
        catch (System.Xml.XmlException ex)
        {
            // 解析不了就**如实说**，不要返回一堆"全都没有"的假事实：那样体检会给出
            // "没有 PublishAot、没有图标、没有能力声明"之类的误导性结论，而真正的问题在别处。
            return new ProjectFacts(
                null, false, null, hasWwwrootDirectory, hasDottedFileName,
                false, false, null, false, false, ex.Message);
        }

        string? Property(string name) => document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == name)
            ?.Value.Trim();

        XElement[] wwwrootResources = document
            .Descendants()
            .Where(element => element.Name.LocalName == "EmbeddedResource")
            .Where(element =>
                (element.Attribute("Include")?.Value ?? string.Empty).Contains("wwwroot", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new ProjectFacts(
            TargetFrameworks: Property("TargetFrameworks") ?? Property("TargetFramework"),
            PublishAot: string.Equals(Property("PublishAot"), "true", StringComparison.OrdinalIgnoreCase),
            ApplicationIcon: Property("ApplicationIcon"),
            HasWwwrootDirectory: hasWwwrootDirectory,
            HasDottedFileNameInWwwroot: hasDottedFileName,
            DeclaresWwwrootResource: wwwrootResources.Length > 0,
            // 判定要严到"每一条 wwwroot 资源都带非空 LogicalName"：只要漏了一条，
            // 那一批文件就会以压平后的名字嵌进去，而症状是页面 404（静默）。
            DeclaredWwwrootResourceHasLogicalName: wwwrootResources.Length > 0
                && wwwrootResources.All(element => !string.IsNullOrWhiteSpace(element.Attribute("LogicalName")?.Value)),
            AssetHost: FindFirstGroup(sourceTexts, @"UseEmbeddedAssets\s*\(\s*""([^""]+)"""),
            DeclaresCapabilities: sourceTexts.Any(text => text.Contains("UseCapabilities(", StringComparison.Ordinal)),
            CallsWithIcon: sourceTexts.Any(text => text.Contains("WithIcon(", StringComparison.Ordinal)),
            ParseError: null);
    }

    private static string? FindFirstGroup(IReadOnlyList<string> texts, string pattern)
    {
        foreach (string text in texts)
        {
            Match match = Regex.Match(text, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>纯函数：事实 → 结论。</summary>
    internal static IEnumerable<CheckItem> Evaluate(ProjectFacts facts, string projectName)
    {
        // csproj 读不出来时，其余结论都不可信——只报这一条，别给一堆"全都没有"的误导
        if (facts.ParseError is { Length: > 0 } parseError)
        {
            yield return CheckItem.Fail("csproj-xml", "csproj 可解析性",
                $"这份 .csproj 不是合法 XML，其余检查都无法进行：{parseError}。" +
                "常见原因：XML 注释里出现了 '--'（XML 注释的内容不能含双连字符，中文破折号没事）。");
            yield break;
        }

        // 目标框架：读不到时**不判阻断**——多目标或动态取值（$([MSBuild]::…)）都读不出来，
        // 而"工具读不出来"与"项目有问题"是两件事，混在一起会产生让人白忙的误报。
        if (facts.TargetFrameworks is not { Length: > 0 } targetFrameworks)
        {
            yield return CheckItem.Warn("target-framework", "目标框架",
                "csproj 与上级 Directory.Build.props 里都没读到 TargetFramework（可能是多目标或动态取值）——" +
                "本库只提供 net10.0 目标，请自行确认");
        }
        else if (targetFrameworks.Split(';').Any(single => single.Trim().Equals("net10.0", StringComparison.OrdinalIgnoreCase)))
        {
            yield return CheckItem.Pass("target-framework", "目标框架", targetFrameworks);
        }
        else
        {
            yield return CheckItem.Fail("target-framework", "目标框架",
                $"读到「{targetFrameworks}」——本库只提供 net10.0 目标");
        }

        // AOT
        yield return facts.PublishAot
            ? CheckItem.Pass("publish-aot", "Native AOT 发布", "PublishAot=true")
            : CheckItem.Warn("publish-aot", "Native AOT 发布",
                "没有 PublishAot=true：不 AOT 也能跑，但仓库文档、示例与打包路径都按 AOT 单文件描述");

        // wwwroot
        yield return facts.HasWwwrootDirectory
            ? CheckItem.Pass("wwwroot", "前端资源目录", "wwwroot 存在")
            : CheckItem.Warn("wwwroot", "前端资源目录",
                "没有 wwwroot 目录：若页面来自远程 URL 可忽略；否则资源不会被内嵌（用 UseEmbeddedAssets 时页面会 404）");

        // 手写资源与 LogicalName（阶段 D 第 1 项的那条静默缺陷）
        if (!facts.DeclaresWwwrootResource)
        {
            yield return CheckItem.Pass("wwwroot-resource", "wwwroot 资源内嵌方式",
                "由包内 buildTransitive/OrielWeb.targets 自动内嵌（带分隔符的 LogicalName）");
        }
        else if (facts.DeclaredWwwrootResourceHasLogicalName)
        {
            yield return CheckItem.Pass("wwwroot-resource", "wwwroot 资源内嵌方式", "手写资源且带 LogicalName（旧写法，行为不变）");
        }
        else if (facts.HasDottedFileNameInWwwroot)
        {
            yield return CheckItem.Fail("wwwroot-resource", "wwwroot 资源内嵌方式",
                "手写了指向 wwwroot 的 EmbeddedResource 却没写 LogicalName，而 wwwroot 里**已经有**含点的文件名" +
                "（如 app.min.js）：MSBuild 会把目录分隔符压成 '.'，库只能靠「最后一个点是扩展名」反推目录，" +
                "这类文件必然解压到错误路径 → 页面按原 URL 请求就是 404 白屏（而且不报错）。" +
                "两条修法：删掉那一行交给包内 targets，或补上 " +
                "LogicalName=\"$(RootNamespace).wwwroot/%(RecursiveDir)%(Filename)%(Extension)\"");
        }
        else
        {
            yield return CheckItem.Warn("wwwroot-resource", "wwwroot 资源内嵌方式",
                "手写了指向 wwwroot 的 EmbeddedResource 却没写 LogicalName。目前还**不会**坏" +
                "（wwwroot 里没有 app.min.js 这类含点文件名），但只要将来加一个，那批文件就会被解压到错误路径" +
                "（页面 404、且不报错）。建议现在就删掉那一行交给包内 targets，或补上 LogicalName。");
        }

        // 图标：Windows 从 exe 取，另外两个平台必须显式给文件
        bool hasIconSource = facts.ApplicationIcon is { Length: > 0 } || facts.CallsWithIcon;
        yield return hasIconSource
            ? CheckItem.Pass("icon", "应用图标",
                facts.CallsWithIcon ? "源码里有 WithIcon(...)" : $"csproj 的 ApplicationIcon={facts.ApplicationIcon}")
            : CheckItem.Warn("icon", "应用图标",
                "既没有 ApplicationIcon、源码里也没有 WithIcon(...)：Windows 会退回 exe 里的图标，" +
                "而 Linux/macOS 上是通用图标——那两个平台没有「从 exe 取图标」这回事。见 README「应用图标」");

        // 能力声明：Release 下不声明即全部拒绝
        yield return facts.DeclaresCapabilities
            ? CheckItem.Pass("capabilities", "IPC 能力声明", "源码里有 UseCapabilities(...)")
            : CheckItem.Warn("capabilities", "IPC 能力声明",
                $"没看到 UseCapabilities(...)：Release 构建下**所有** oriel.invoke 都会被拒绝" +
                $"（安全模型的 fail-closed），表现为「按钮点了没反应」。" +
                $"{projectName} 若是示例或调试用可忽略。见 README「安全与能力模型」");

        // 内嵌资源的虚拟主机名：Linux/macOS 上会被改写成本地文件，但页面里的 URL 要能对上
        if (facts.AssetHost is { } host)
        {
            yield return CheckItem.Pass("asset-host", "内嵌资源虚拟主机", $"UseEmbeddedAssets(\"{host}\")");
        }
    }
}
