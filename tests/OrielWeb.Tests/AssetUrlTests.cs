using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>oriel://&lt;host&gt;/…</c> 的解析与内容类型。它决定"某个 URL 要不要被当作内嵌资源、
/// 交给 scheme 处理器"，判错的两个方向都很难查：
/// <list type="bullet">
///   <item>该接管的没接管：页面变成一次真实的网络请求，DNS 失败后引擎渲染错误页——
///   窗口一片空白，宿主侧却什么异常都收不到。</item>
///   <item>不该接管的接管了：外部站点会被悄悄劫持到内嵌资源上（读什么都是自己那份）。</item>
/// </list>
/// </summary>
public sealed class AssetUrlTests
{
    private const string Host = "app.oriel";

    [Theory]
    [InlineData("oriel://app.oriel/manual-check.html", "manual-check.html")]
    [InlineData("oriel://app.oriel/index.html", "index.html")]
    [InlineData("oriel://app.oriel/sub/page.html", "sub/page.html")]
    [InlineData("oriel://APP.ORIEL/manual-check.html", "manual-check.html")]        // host 比对不分大小写
    [InlineData("ORIEL://app.oriel/index.html", "index.html")]                       // scheme 也不分大小写
    [InlineData("oriel://app.oriel/", "index.html")]                                 // 站点根 → 首页
    [InlineData("oriel://app.oriel", "index.html")]                                  // 连斜杠都没有的写法
    [InlineData("oriel://app.oriel/index.html?v=2#top", "index.html")]               // query/fragment 不参与定位
    // 兼容别名：0.2.0 及以前的文档/示例写的是 https://<host>/…，直接不认会让已有项目白屏
    [InlineData("https://app.oriel/manual-check.html", "manual-check.html")]
    [InlineData("http://app.oriel/manual-check.html", "manual-check.html")]
    public void AssetUrls_ResolveToRelativePaths(string url, string expected)
    {
        Assert.True(AssetUrl.TryResolve(url, Host, out string relative));
        Assert.Equal(expected, relative);
    }

    [Theory]
    [InlineData("https://example.com/index.html")]              // 真正的外部站点，必须原样放行
    [InlineData("https://app.oriel.example.com/index.html")]    // 后缀上像的域名不能命中
    [InlineData("https://notapp.oriel/index.html")]             // 前缀上像的也不能
    [InlineData("oriel://app.oriel.example.com/index.html")]
    [InlineData("file:///etc/passwd")]                          // 别的 scheme 一律不接管
    [InlineData("custom://app.oriel/index.html")]
    [InlineData("not a url")]
    [InlineData("/relative/only.html")]
    [InlineData("")]
    public void ForeignOrMalformedUrls_AreNotOurs(string url)
        => Assert.False(AssetUrl.TryResolve(url, Host, out _));

    [Theory]
    [InlineData("../outside.html", "outside.html")]
    [InlineData("sub/../../outside.html", "outside.html")]
    public void DotSegments_AreFoldedByTheUriParser(string path, string expected)
    {
        // 陷阱在这里：`oriel://host/../x` 在**解析阶段**就被折成 `/x`（RFC 3986 的 dot-segment 移除），
        // 于是"穿越"根本没机会到达资源表——写这条是为了让后来者知道**这不是**防线所在，
        // 真正的边界是 EmbeddedAssetStore 的字典查表（见 EmbeddedAssetTests）。
        Assert.True(AssetUrl.TryResolve($"oriel://{Host}/{path}", Host, out string relative));
        Assert.Equal(expected, relative);
    }

    [Theory]
    [InlineData("..%2Foutside.html")]                            // 编码的斜杠不会被折叠 → 必须显式拒绝
    [InlineData("%2F..%2Foutside.html")]
    public void EncodedSlashTraversal_IsRejected(string path)
        => Assert.False(AssetUrl.TryResolve($"oriel://{Host}/{path}", Host, out _));

    [Fact]
    public void MissingHost_IsNotOurs()
    {
        // 没启用内嵌资源（host 为空）时任何 URL 都不该被接管。
        Assert.False(AssetUrl.TryResolve("oriel://app.oriel/index.html", string.Empty, out _));
    }

    [Fact]
    public void TrustedPrefix_IsThePagesRealOrigin()
    {
        // 页面真实来源就是 oriel://<host>/，可信前缀必须由同一个地方生成——
        // 手拼一次就够在"空格 vs %20"这类差异上翻车（2026-10-02 的 macOS 冒烟就是这么红的）。
        Assert.Equal("oriel://app.oriel/", AssetUrl.TrustedPrefix(Host));
        Assert.StartsWith(AssetUrl.TrustedPrefix(Host), AssetUrl.DefaultDocument(Host), StringComparison.Ordinal);
        Assert.StartsWith(AssetUrl.TrustedPrefix(Host), AssetUrl.ForHost(Host, "sub/page.html"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("styles.css", "text/css; charset=utf-8")]
    [InlineData("data.json", "application/json; charset=utf-8")]
    [InlineData("logo.svg", "image/svg+xml; charset=utf-8")]
    [InlineData("pic.PNG", "image/png")]                          // 扩展名比对不分大小写
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("app.wasm", "application/wasm")]
    public void KnownExtensions_GetConcreteContentTypes(string path, string expected)
        => Assert.Equal(expected, MimeTypes.ForPath(path));

    [Fact]
    public void UnknownExtension_FallsBackToOctetStream()
    {
        // 不猜 text/html：猜错的代价是浏览器把二进制当页面渲染，比"当成未知文件"糟得多。
        Assert.Equal("application/octet-stream", MimeTypes.ForPath("archive.bin"));
        Assert.Equal("application/octet-stream", MimeTypes.ForPath("noextension"));
    }
}
