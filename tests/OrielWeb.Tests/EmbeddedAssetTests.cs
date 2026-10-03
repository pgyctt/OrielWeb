using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内嵌资源名 → 相对路径的映射（<see cref="EmbeddedAssetStore"/>）。
/// </summary>
/// <remarks>
/// 这段映射以前是"静默写错路径"的源头：含点的文件名（<c>app.min.js</c>）被当成目录分隔，
/// 解压到 <c>app/min.js</c>，页面按原 URL 请求就是 404 白屏，而宿主侧没有任何异常。
/// 两套约定（显式分隔符 / 兼容反推）与它们的判定方式都在这里钉住。
/// </remarks>
public sealed class EmbeddedAssetTests
{
    private static string Normalize(string relative)
        => relative.Replace('/', Path.DirectorySeparatorChar);

    // ---- 约定判定：前缀自己说明用的是哪一种 ----

    [Fact]
    public void Convention_ExplicitPrefixWhenResourcesUseSlash()
    {
        // LogicalName 写法：资源名形如 MyApp.wwwroot/assets/img/logo.svg
        var (prefix, explicitSeparators) = EmbeddedAssetStore.ResolveConvention(
            ["MyApp.wwwroot/index.html", "MyApp.wwwroot/assets/img/logo.svg"], "MyApp", null);

        Assert.Equal("MyApp.wwwroot/", prefix);
        Assert.True(explicitSeparators);
    }

    [Fact]
    public void Convention_LegacyPrefixWhenResourcesUseDots()
    {
        var (prefix, explicitSeparators) = EmbeddedAssetStore.ResolveConvention(
            ["MyApp.wwwroot.index.html", "MyApp.wwwroot.assets.img.logo.svg"], "MyApp", null);

        Assert.Equal("MyApp.wwwroot.", prefix);
        Assert.False(explicitSeparators);
    }

    [Fact]
    public void Convention_ExplicitWinsEvenForFlatLayout()
    {
        // 平铺目录下显式写法**看不出分隔符**（只有 MyApp.wwwroot/app.min.js 这种名字），
        // 这正是以前漏掉的一类：只按"资源名里有没有分隔符"判断，会把它当成兼容写法反推成 app/min.js。
        var (prefix, explicitSeparators) = EmbeddedAssetStore.ResolveConvention(
            ["MyApp.wwwroot/index.html", "MyApp.wwwroot/app.min.js"], "MyApp", null);

        Assert.Equal("MyApp.wwwroot/", prefix);
        Assert.True(explicitSeparators);
    }

    [Fact]
    public void Convention_OverridePrefixDecidesByItsOwnShape()
    {
        var explicitOverride = EmbeddedAssetStore.ResolveConvention(
            ["assets/img/logo.svg"], "MyApp", "assets/");
        Assert.Equal("assets/", explicitOverride.Prefix);
        Assert.True(explicitOverride.ExplicitSeparators);

        var legacyOverride = EmbeddedAssetStore.ResolveConvention(
            ["assets.img.logo.svg"], "MyApp", "assets.");
        Assert.Equal("assets.", legacyOverride.Prefix);
        Assert.False(legacyOverride.ExplicitSeparators);
    }

    // ---- 映射：显式约定 ----

    [Theory]
    [InlineData("index.html", "index.html")]
    // 关键回归：平铺目录下的含点文件名不再被拆成目录
    [InlineData("app.min.js", "app.min.js")]
    [InlineData("vendor.bundle.js", "vendor.bundle.js")]
    [InlineData("logo.dark.svg", "logo.dark.svg")]
    [InlineData("assets/img/logo.svg", "assets/img/logo.svg")]
    [InlineData("a.b/c.d.js", "a.b/c.d.js")]
    [InlineData("deep/nested/dir/file.txt", "deep/nested/dir/file.txt")]
    public void ExplicitConvention_KeepsDotsInFileNames(string suffix, string expected)
    {
        Assert.Equal(Normalize(expected), EmbeddedAssetStore.MapResourceToPath(suffix, explicitSeparators: true));
    }

    [Fact]
    public void ExplicitConvention_AcceptsBackslashToo()
    {
        // MSBuild 的 %(RecursiveDir) 在 Windows 上给的是反斜杠，混合分隔符也要能处理
        Assert.Equal(
            Normalize("assets/img/logo.svg"),
            EmbeddedAssetStore.MapResourceToPath(@"assets\img/logo.svg", explicitSeparators: true));
    }

    // ---- 映射：兼容约定（保留既有行为，但把已知限制写死） ----

    [Theory]
    [InlineData("index.html", "index.html")]
    [InlineData("assets.img.logo.svg", "assets/img/logo.svg")]
    [InlineData("sub.page.html", "sub/page.html")]
    public void LegacyConvention_TreatsDotsAsDirectories(string suffix, string expected)
    {
        Assert.Equal(Normalize(expected), EmbeddedAssetStore.MapResourceToPath(suffix, explicitSeparators: false));
    }

    [Fact]
    public void LegacyConvention_IsAmbiguousForDottedFileNames()
    {
        // 已知限制：兼容写法下 app.min.js 与 app/min.js 的资源名完全一样，无法从名字区分。
        // 这个用例把限制钉死，避免以后有人以为"反推逻辑还能修"——要用含点文件名就换显式写法。
        Assert.Equal(
            Normalize("app/min.js"),
            EmbeddedAssetStore.MapResourceToPath("app.min.js", explicitSeparators: false));
    }

    [Fact]
    public void NoDotAtAll_ReturnsAsIs()
    {
        Assert.Equal("readme", EmbeddedAssetStore.MapResourceToPath("readme", explicitSeparators: false));
        Assert.Equal("readme", EmbeddedAssetStore.MapResourceToPath("readme", explicitSeparators: true));
    }
}
