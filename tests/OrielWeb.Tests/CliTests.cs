using System.Xml;
using System.Xml.Linq;
using OrielWeb.Cli;
using OrielWeb.Cli.Bundle;
using OrielWeb.Cli.Doctor;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>oriel doctor</c> 的单测：命令行解析、项目体检的判定、以及"人眼验证指引"与文档的绑定。
/// </summary>
/// <remarks>
/// 项目体检的判定做成纯函数（<see cref="ProjectChecks.Evaluate"/>）就是为了这里能摆出各种事实组合，
/// 不必真的造工程目录。这几条判定的错都会以"工具说了假话"的形式出现——比没工具更糟。
/// </remarks>
public sealed class CliDoctorTests
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--json" };

    // ---- 命令行解析 ----

    [Fact]
    public void ParsesSubcommandOptionsAndPositionals()
    {
        ParsedCommandLine parsed = ParsedCommandLine.Parse(
            ["bundle", "--dir", "publish/win-x64", "--json", "extra"], Flags);

        Assert.Null(parsed.Error);
        Assert.Equal("bundle", parsed.Subcommand);
        Assert.Equal("publish/win-x64", parsed.Value("--dir"));
        Assert.True(parsed.Has("--json"));
        Assert.Equal(["extra"], parsed.Positionals);
    }

    [Fact]
    public void InlineValueKeepsSpaces()
    {
        // --name="Oriel Demo" 写成 --name=Oriel Demo 时值里可以有空格，脚本里不必再操心引号
        ParsedCommandLine parsed = ParsedCommandLine.Parse(["bundle", "--name=Oriel Demo"], Flags);

        Assert.Equal("Oriel Demo", parsed.Value("--name"));
    }

    [Fact]
    public void OptionWithoutValueIsAnError()
    {
        // 这是最要命的一类静默故障：--dir 没给值却继续跑，会打出一个错的包
        ParsedCommandLine parsed = ParsedCommandLine.Parse(["bundle", "--dir", "--json"], Flags);

        Assert.NotNull(parsed.Error);
        Assert.Contains("--dir", parsed.Error);
    }

    [Fact]
    public void FlagNeedsNoValue() => Assert.True(ParsedCommandLine.Parse(["doctor", "--json"], Flags).Has("--json"));

    // ---- 报告模型 ----

    [Fact]
    public void ReportAggregatesAndFlagsBlocking()
    {
        var report = new CheckReport("测试");
        report.Add(CheckItem.Pass("a", "甲", "ok"));
        report.Add(CheckItem.Warn("b", "乙", "小心"));
        report.Add(CheckItem.NotApplicable("c", "丙", "别的平台"));

        Assert.False(report.HasBlockingProblem);
        Assert.Equal(1, report.Count(CheckStatus.Pass));
        Assert.Equal(1, report.Count(CheckStatus.Warn));

        report.Add(CheckItem.Fail("d", "丁", "坏了"));
        Assert.True(report.HasBlockingProblem);
        Assert.Contains("[FAIL]", report.ToText());
    }

    // ---- 项目体检 ----

    private const string CsprojWithEverything = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PublishAot>true</PublishAot>
            <ApplicationIcon>app.ico</ApplicationIcon>
          </PropertyGroup>
          <ItemGroup>
            <EmbeddedResource Include="wwwroot\**\*" LogicalName="App.wwwroot/%(RecursiveDir)%(Filename)%(Extension)" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void ReadsFactsFromCsprojAndSources()
    {
        ProjectFacts facts = ProjectChecks.Parse(
            CsprojWithEverything,
            ["Oriel.CreateBuilder().UseEmbeddedAssets(\"app.oriel\").UseCapabilities(c => c.Allow(\"*\")).Run();"],
            hasWwwrootDirectory: true,
            hasDottedFileName: true);

        Assert.Equal("net10.0", facts.TargetFrameworks);
        Assert.True(facts.PublishAot);
        Assert.Equal("app.ico", facts.ApplicationIcon);
        Assert.True(facts.DeclaresWwwrootResource);
        Assert.True(facts.DeclaredWwwrootResourceHasLogicalName);
        Assert.Equal("app.oriel", facts.AssetHost);
        Assert.True(facts.DeclaresCapabilities);
        Assert.Null(facts.ParseError);
    }

    [Fact]
    public void DottedFileNameWithoutLogicalNameIsBlocking()
    {
        // 已经坏了：含点文件名配上压平的资源名必然解压错位
        ProjectFacts facts = ProjectChecks.Parse(
            """<Project><ItemGroup><EmbeddedResource Include="wwwroot\**\*" /></ItemGroup></Project>""",
            [],
            hasWwwrootDirectory: true,
            hasDottedFileName: true);

        CheckItem item = Assert.Single(Evaluate(facts), i => i.Id == "wwwroot-resource");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    [Fact]
    public void PlainFileNamesWithoutLogicalNameAreOnlyAWarning()
    {
        // 还没坏，但迟早会——所以是 WARN 而不是 FAIL，也不是 PASS
        ProjectFacts facts = ProjectChecks.Parse(
            """<Project><ItemGroup><EmbeddedResource Include="wwwroot\**\*" /></ItemGroup></Project>""",
            [],
            hasWwwrootDirectory: true,
            hasDottedFileName: false);

        CheckItem item = Assert.Single(Evaluate(facts), i => i.Id == "wwwroot-resource");
        Assert.Equal(CheckStatus.Warn, item.Status);
    }

    [Fact]
    public void DeclaringNothingIsFine()
    {
        // 交给包内 targets 嵌是推荐写法，必须判 PASS 而不是"没声明就报问题"
        ProjectFacts facts = ProjectChecks.Parse(
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            [],
            hasWwwrootDirectory: true);

        CheckItem item = Assert.Single(Evaluate(facts), i => i.Id == "wwwroot-resource");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    [Fact]
    public void MissingCapabilitiesIsAWarningNotAFailure()
    {
        // Release 下会被全部拒绝，很值得提醒；但示例工程与调试项目不该因此被判"阻断"
        ProjectFacts facts = ProjectChecks.Parse(
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            ["// 没有 UseCapabilities"],
            hasWwwrootDirectory: false);

        CheckItem item = Assert.Single(Evaluate(facts), i => i.Id == "capabilities");
        Assert.Equal(CheckStatus.Warn, item.Status);
        Assert.Contains("Release", item.Detail);
    }

    [Fact]
    public void BrokenCsprojReportsOnlyItself()
    {
        // 读不出 csproj 时给一堆"全都没有"的假结论，比什么都不说更糟（这条是踩出来的：
        // XML 注释里出现 '--' 会让整份 csproj 解析失败，而当时的输出是一串误导性 WARN）
        ProjectFacts facts = ProjectChecks.Parse(
            "<Project><!-- 注释里不能有 -- 双连字符 --></Project>",
            [],
            hasWwwrootDirectory: true);

        List<CheckItem> items = [.. Evaluate(facts)];

        CheckItem only = Assert.Single(items);
        Assert.Equal("csproj-xml", only.Id);
        Assert.Equal(CheckStatus.Fail, only.Status);
    }

    [Fact]
    public void UnknownTargetFrameworkIsOnlyAWarning()
    {
        // 多目标或动态取值时读不出来，那是"工具读不到"，不是"项目有问题"
        ProjectFacts facts = ProjectChecks.Parse("<Project><PropertyGroup /></Project>", [], hasWwwrootDirectory: false);

        CheckItem item = Assert.Single(Evaluate(facts), i => i.Id == "target-framework");
        Assert.Equal(CheckStatus.Warn, item.Status);
    }

    private static IEnumerable<CheckItem> Evaluate(ProjectFacts facts) => ProjectChecks.Evaluate(facts, "TestApp");

    // ---- 人眼指引与文档的绑定 ----

    [Fact]
    public void EveryDeclaredManualCheckNameExistsInRoadmap()
    {
        // 双向漂移的防线：代码里引用的清单项名必须逐字符出现在 ROADMAP 的 #### 小节里。
        // 文档改名而代码没跟上（或反过来）都会让使用者照着工具输出翻不到那一节。
        string[] headings = RoadmapHeadings();
        string[] declared =
        [
            ManualChecks.WindowIcon, ManualChecks.ClipboardInterop, ManualChecks.ThemeRealtime,
            ManualChecks.DevTools, ManualChecks.MacHiddenStart, ManualChecks.LinuxHiDpiDrag,
            ManualChecks.DragBehavior, ManualChecks.MultiWindow,
            ManualChecks.TrayIcon, ManualChecks.Notifications, ManualChecks.ContextMenu,
            ManualChecks.Dialogs, ManualChecks.FileDrop, ManualChecks.BuiltInContextMenu,
        ];

        foreach (string section in declared)
        {
            Assert.True(
                headings.Contains(section, StringComparer.Ordinal),
                $"docs/ROADMAP.md 里没有标题为「{section}」的待真机验证小节。");
        }
    }

    [Fact]
    public void ManualChecksAreNotEmptyForCurrentPlatform()
    {
        List<ManualStep> steps = [.. ManualChecks.ForCurrentPlatform()];

        Assert.True(steps.Count >= 6, $"当前平台只有 {steps.Count} 条人眼验证指引，看起来漏了平台分支。");
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Command)));
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Expectation)));
    }

    private static string[] RoadmapHeadings()
        => File
            .ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "ROADMAP.md"))
            .Where(line => line.StartsWith("#### ", StringComparison.Ordinal))
            .Select(line => line[5..].Trim())
            .ToArray();

    internal static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "docs", "ROADMAP.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（含 docs/ROADMAP.md 的目录）。");
    }
}

/// <summary>
/// <c>oriel bundle</c> 的单测：元数据校验与三平台产物文本。
/// </summary>
/// <remarks>
/// 这些文本里的错在开发机上基本看不出来：MSI 的 UpgradeCode 变了要等用户升级时才暴露、
/// Info.plist 少一个键会让 WKWebView 直接 __builtin_trap、.desktop 的 Exec 写错只是"点不开"。
/// 所以至少钉住两件事：**XML 能被解析**（转义没写坏）与关键字段在不在。
/// </remarks>
public sealed class CliBundleTests
{
    private static BundleOptions SampleOptions(string? icon = null, string? publisher = null)
        => new(
            PublishDirectory: Path.Combine(Path.GetTempPath(), "oriel-publish"),
            Rid: "win-x64",
            Platform: BundlePlatform.Windows,
            Name: "Oriel Demo",
            Id: "com.orielweb.demo",
            Version: "0.1.2",
            Icon: icon,
            Publisher: publisher,
            OutputDirectory: Path.Combine(Path.GetTempPath(), "oriel-out"));

    [Fact]
    public void StableGuidIsDeterministic()
    {
        // UpgradeCode 必须稳定：每次打包随机生成的话，升级会变成并列安装、旧版本卸不掉
        Assert.Equal(StableGuid.From("com.orielweb.demo"), StableGuid.From("com.orielweb.demo"));
        Assert.NotEqual(StableGuid.From("com.orielweb.demo"), StableGuid.From("com.orielweb.other"));
    }

    [Fact]
    public void UpgradeCodeFollowsTheApplicationId()
    {
        Assert.Equal(SampleOptions().UpgradeCode, SampleOptions().UpgradeCode);
        Assert.NotEqual(SampleOptions().UpgradeCode, (SampleOptions() with { Id = "com.example.other" }).UpgradeCode);
    }

    // 断言用平台名而不是枚举：xUnit 要求测试方法本身是 public，而 public 方法的签名里
    // 不能出现 internal 类型（CS0051）——CLI 的枚举本来就是内部的。
    [Theory]
    [InlineData("win-x64", "Windows")]
    [InlineData("win-arm64", "Windows")]
    [InlineData("osx-arm64", "MacOS")]
    [InlineData("linux-x64", "Linux")]
    public void PlatformComesFromRid(string rid, string expected)
        => Assert.Equal(expected, BundleOptionsReader.ResolvePlatform(rid)?.ToString());

    [Theory]
    [InlineData("freebsd-x64")]
    [InlineData("win")]
    [InlineData("")]
    public void UnknownRidIsRejected(string rid) => Assert.Null(BundleOptionsReader.ResolvePlatform(rid));

    [Theory]
    [InlineData("com.orielweb.demo", true)]
    [InlineData("io.github.me.my-app", true)]
    [InlineData("nodots", false)]
    [InlineData(".leading", false)]
    [InlineData("trailing.", false)]
    [InlineData("has space", false)]
    [InlineData("有中文", false)]
    public void ReverseDomainIdValidation(string id, bool expected)
        => Assert.Equal(expected, BundleOptionsReader.LooksLikeReverseDomainId(id));

    [Fact]
    public void ManifestParsesAndRejectsBadShapes()
    {
        (Dictionary<string, string> values, string? error) = BundleOptionsReader.ParseManifest(
            """{ "dir": "publish/win-x64", "name": "Oriel Demo" }""");

        Assert.Null(error);
        Assert.Equal("publish/win-x64", values["dir"]);
        Assert.Equal("Oriel Demo", values["name"]);

        // 非字符串值要报错而不是静默转成字符串：manifest 是给人写的，写错了应当立刻知道
        Assert.NotNull(BundleOptionsReader.ParseManifest("""{ "version": 1 }""").Error);
        Assert.NotNull(BundleOptionsReader.ParseManifest("""[1, 2]""").Error);
        Assert.NotNull(BundleOptionsReader.ParseManifest("""{ "broken":""").Error);
    }

    [Fact]
    public void WixSourceIsValidXmlAndCarriesTheEssentials()
    {
        BundleOptions options = SampleOptions(publisher: "Example Ltd");
        XDocument document = ParseXml(Packagers.WixSource(options, "OrielDemo.exe"));

        string text = document.ToString();
        Assert.Contains(options.UpgradeCode.ToString("B"), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scope=\"perUser\"", text);
        Assert.Contains("LocalAppDataFolder", text);
        // 发布目录里的文件要全进包（WiX v4+ 的批量收集）
        Assert.Contains("**", text);
        // 开始菜单快捷方式的落点必须指向安装目录里的 exe
        Assert.Contains("[INSTALLFOLDER]OrielDemo.exe", text);
        // 反注册表键用的是应用标识，而不是显示名（改名不该变成另一个应用）
        Assert.Contains("com.orielweb.demo", text);
    }

    [Fact]
    public void WixSourceEscapesXmlHostileNames()
    {
        // 应用名里出现 & 或 < 时若没转义，wix build 会报一个与真正原因无关的解析错误。
        // 断言的是**解析回来**的值——那才证明转义写对了（直接比文本比的是转义后的样子）。
        BundleOptions options = SampleOptions() with { Name = "A & B <Studio>" };
        XDocument document = ParseXml(Packagers.WixSource(options, "app.exe"));

        XElement package = document.Descendants().Single(element => element.Name.LocalName == "Package");
        Assert.Equal("A & B <Studio>", package.Attribute("Name")?.Value);
    }

    [Fact]
    public void InfoPlistCarriesTheBundleIdentifier()
    {
        // CFBundleIdentifier 缺失时 WKWebView 会直接 __builtin_trap（见 oriel bundle 头部的记录）
        XDocument document = ParseXml(Packagers.InfoPlist(SampleOptions(), "OrielDemo", hasIcon: false));

        Assert.Contains("com.orielweb.demo", document.ToString());
        Assert.Contains("OrielDemo", document.ToString());
        Assert.Contains("NSHighResolutionCapable", document.ToString());
        Assert.DoesNotContain("CFBundleIconFile", document.ToString());
    }

    [Fact]
    public void InfoPlistMentionsTheIconWhenPresent()
    {
        XDocument document = ParseXml(Packagers.InfoPlist(SampleOptions(), "OrielDemo", hasIcon: true));

        // 键名与 BundleCommand 放进 Contents/Resources 的文件名必须一致
        Assert.Contains(Packagers.AppIconName, document.ToString());
    }

    [Fact]
    public void DesktopEntryAndAppRunHaveWorkingEntryPoints()
    {
        BundleOptions options = SampleOptions();

        string desktop = Packagers.DesktopEntry(options, "OrielDemo");
        Assert.Contains("Exec=OrielDemo", desktop);
        Assert.Contains($"Icon={Packagers.IconName(options)}", desktop);
        Assert.Contains("X-AppImage-Version=0.1.2", desktop);

        string appRun = Packagers.AppRun("OrielDemo");
        Assert.StartsWith("#!/bin/sh", appRun, StringComparison.Ordinal);
        Assert.Contains("usr/bin/OrielDemo", appRun);
        // 必须转发参数，否则 --selftest 之类的开关到不了应用
        Assert.Contains("\"$@\"", appRun);
    }

    [Fact]
    public void ArtifactNamesAreSanitized()
    {
        BundleOptions options = SampleOptions() with { Name = "My App / 中文" };

        Assert.DoesNotContain("/", options.ArtifactBaseName);
        Assert.DoesNotContain(" ", options.ArtifactBaseName);
        Assert.EndsWith("win-x64", options.ArtifactBaseName, StringComparison.Ordinal);
        Assert.Equal("my-app", Packagers.IconName(options));
    }

    /// <summary>解析产物文本。忽略 DTD：Info.plist 里有 DOCTYPE，而默认设置禁止 DTD。</summary>
    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }
}
