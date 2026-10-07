using System.Reflection;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内嵌资源名 → 相对路径的映射，以及请求路径的归一（<see cref="EmbeddedAssetStore"/>）。
/// </summary>
/// <remarks>
/// 这段映射以前是"静默写错路径"的源头：含点的文件名（<c>app.min.js</c>）被当成目录分隔，
/// 页面按原 URL 请求就是 404 白屏，而宿主侧没有任何异常。2026-10-03 起只认一种约定
/// （显式 <c>/</c> 分隔符），旧写法直接报错——这里把"只认哪一种""旧形式会怎样"
/// 以及"请求路径的边界在哪"都钉住。
/// </remarks>
public sealed class EmbeddedAssetTests
{
    private static string Normalize(string relative)
        => relative.Replace('/', Path.DirectorySeparatorChar);

    // ---- 约定判定：只有显式分隔符这一种 ----

    [Fact]
    public void Convention_ExplicitPrefix_WhenResourcesUseSlash()
    {
        // LogicalName 写法：资源名形如 MyApp.wwwroot/assets/img/logo.svg
        Assert.Equal(
            "MyApp.wwwroot/",
            EmbeddedAssetStore.ResolveConvention(
                ["MyApp.wwwroot/index.html", "MyApp.wwwroot/assets/img/logo.svg"], "MyApp", null));
    }

    [Fact]
    public void Convention_ExplicitWinsEvenForFlatLayout()
    {
        // 平铺目录下显式写法**看不出分隔符**（名字里只有 MyApp.wwwroot/app.min.js 这种）——
        // 这正是旧实现漏掉的一类：它按"资源名里有没有分隔符"判断，于是把 app.min.js 反推成 app/min.js。
        // 现在判定只看前缀，前缀自己就说明了约定。
        Assert.Equal(
            "MyApp.wwwroot/",
            EmbeddedAssetStore.ResolveConvention(
                ["MyApp.wwwroot/index.html", "MyApp.wwwroot/app.min.js"], "MyApp", null));
    }

    [Fact]
    public void Convention_OverrideMustEndWithSeparator()
    {
        Assert.Equal(
            "assets/",
            EmbeddedAssetStore.ResolveConvention(["assets/img/logo.svg"], "MyApp", "assets/"));

        // 以 '.' 结尾的自定义前缀意味着"资源名里的 '.' 是目录分隔"——那正是猜不准的那件事，直接拒绝。
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedAssetStore.ResolveConvention(["assets.img.logo.svg"], "MyApp", "assets."));
        Assert.Contains("resourcePrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convention_LegacyResources_AreRejectedWithGuidance()
    {
        // 只剩旧写法时，报的是"你用了一个已删除的写法"，而不是笼统的"没找到资源"——
        // 后者的排查方向会跑到资源名上，而问题在 csproj 那一行上。
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedAssetStore.ResolveConvention(["MyApp.wwwroot.index.html"], "MyApp", null));

        Assert.Contains("不再支持", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LogicalName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convention_NoResources_ReturnsTheExpectedPrefix()
    {
        // 一条资源都没有时不在这里抛：措辞统一的"没找到资源"由 Create 负责（它对使用者更有用，
        // 因为那条消息会说清"什么都不用写，targets 会嵌"）。
        Assert.Equal("MyApp.wwwroot/", EmbeddedAssetStore.ResolveConvention([], "MyApp", null));
    }

    // ---- 映射：只翻译分隔符，'.' 永远属于文件名 ----

    [Theory]
    [InlineData("index.html", "index.html")]
    [InlineData("app.min.js", "app.min.js")]                  // 关键回归：含点文件名不再被拆成目录
    [InlineData("vendor.bundle.js", "vendor.bundle.js")]
    [InlineData("logo.dark.svg", "logo.dark.svg")]
    [InlineData("assets/img/logo.svg", "assets/img/logo.svg")]
    [InlineData("a.b/c.d.js", "a.b/c.d.js")]
    [InlineData("deep/nested/dir/file.txt", "deep/nested/dir/file.txt")]
    [InlineData("readme", "readme")]
    public void DotsStayInFileNames(string suffix, string expected)
        => Assert.Equal(Normalize(expected), EmbeddedAssetStore.MapResourceToPath(suffix));

    [Fact]
    public void BackslashSeparatorsAreTranslated()
    {
        // MSBuild 的 %(RecursiveDir) 在 Windows 上给的是反斜杠，混合分隔符也要能处理
        Assert.Equal(Normalize("assets/img/logo.svg"), EmbeddedAssetStore.MapResourceToPath(@"assets\img/logo.svg"));
    }

    // ---- 请求路径归一：真正的边界在这里，不在 URI 解析 ----

    [Theory]
    [InlineData("../outside.html")]
    [InlineData("sub/../../outside.html")]
    [InlineData("..")]
    [InlineData("sub/..")]
    public void NormalizeRequestPath_RejectsDotSegments(string path)
        => Assert.Null(EmbeddedAssetStore.NormalizeRequestPath(path));

    [Theory]
    [InlineData(null, "index.html")]
    [InlineData("", "index.html")]
    [InlineData("/", "index.html")]
    [InlineData("/sub/", "sub/index.html")]
    [InlineData("/sub/page.html", "sub/page.html")]
    [InlineData("a%20b/c.js", "a b/c.js")]
    public void NormalizeRequestPath_ResolvesTheUsualForms(string? path, string expected)
        => Assert.Equal(expected, EmbeddedAssetStore.NormalizeRequestPath(path));

    // ---- 多程序集合并：入口与类库的 wwwroot 合并成一棵站点，入口优先 ----

    private static readonly Assembly TestAssembly = typeof(EmbeddedAssetTests).Assembly;

    [Fact]
    public void Collect_MergesLibraryResourcesIntoTheSameSite()
    {
        var table = new Dictionary<string, (Assembly Source, string ResourceName)>(StringComparer.OrdinalIgnoreCase);
        EmbeddedAssetStore.CollectResources(TestAssembly, ["App.wwwroot/index.html"], "App.wwwroot/", table);
        EmbeddedAssetStore.CollectResources(TestAssembly, ["MyLib.wwwroot/css/site.css", "MyLib.wwwroot/logo.svg"], "MyLib.wwwroot/", table);

        Assert.Equal(3, table.Count);
        Assert.Equal("MyLib.wwwroot/css/site.css", table["css/site.css"].ResourceName);
        Assert.Equal("MyLib.wwwroot/logo.svg", table["logo.svg"].ResourceName);
    }

    [Fact]
    public void Collect_EntryWinsWhenLibraryShipsTheSamePath()
    {
        // 类库与入口都提供同一路径：入口先收集、TryAdd 先到先得——类库不能劫持应用的首页。
        var table = new Dictionary<string, (Assembly Source, string ResourceName)>(StringComparer.OrdinalIgnoreCase);
        EmbeddedAssetStore.CollectResources(TestAssembly, ["App.wwwroot/index.html"], "App.wwwroot/", table);
        EmbeddedAssetStore.CollectResources(TestAssembly, ["MyLib.wwwroot/index.html"], "MyLib.wwwroot/", table);

        Assert.Equal("App.wwwroot/index.html", table["index.html"].ResourceName);
    }

    [Fact]
    public void Collect_IgnoresResourcesOutsideTheConvention()
    {
        // 只收 "<程序集名>.wwwroot/" 前缀：别的用途的内嵌资源、旧写法（点分隔）一律不进表——
        // 后者是 fail closed：不被服务，也不会把路径猜错。
        var table = new Dictionary<string, (Assembly Source, string ResourceName)>(StringComparer.OrdinalIgnoreCase);
        EmbeddedAssetStore.CollectResources(TestAssembly,
            ["Unrelated.Resource", "MyLib.wwwroot.old/legacy.html", "MyLib.wwwroot/js/app.js"],
            "MyLib.wwwroot/", table);

        var hit = Assert.Single(table);
        Assert.Equal("MyLib.wwwroot/js/app.js", hit.Value.ResourceName);
    }
}
