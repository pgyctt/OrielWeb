using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 虚拟主机 URL → 本地文件 的解析。它决定"某个 URL 要不要被当作内嵌资源、映射到解压目录"，
/// 判错的两个方向都很难查：
/// <list type="bullet">
///   <item>该映射的没映射：Linux/macOS 会把 <c>https://app.oriel/…</c> 当成真实网络请求，DNS 失败后
///   引擎渲染错误页——窗口一片空白，宿主侧却什么异常都收不到（这正是它被漏掉很久的原因）。</item>
///   <item>不该映射的映射了：外部站点会被悄悄劫持到本地目录。</item>
/// </list>
/// </summary>
public sealed class AssetUrlResolverTests : IDisposable
{
    private const string Host = "app.oriel";
    private readonly string _root;

    public AssetUrlResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "oriel-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_root, "manual-check.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_root, "sub", "page.html"), "<html></html>");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Expected(string relative)
        => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Theory]
    [InlineData("https://app.oriel/manual-check.html", "manual-check.html")]
    [InlineData("https://app.oriel/index.html", "index.html")]
    [InlineData("https://app.oriel/sub/page.html", "sub/page.html")]
    [InlineData("https://APP.ORIEL/manual-check.html", "manual-check.html")]  // host 比对不分大小写
    [InlineData("http://app.oriel/manual-check.html", "manual-check.html")]   // host 是虚拟的，两个 scheme 同义
    [InlineData("https://app.oriel/", "index.html")]                          // 站点根 → 首页
    [InlineData("https://app.oriel", "index.html")]                           // 连斜杠都没有的写法
    [InlineData("https://app.oriel/manual-check.html?v=2#top", "manual-check.html")] // query/fragment 不参与文件定位
    public void HostedUrls_MapToLocalFiles(string url, string relative)
        => Assert.Equal(Expected(relative), AssetUrlResolver.TryResolveLocalFile(url, Host, _root));

    [Theory]
    [InlineData("https://example.com/index.html")]                  // 真正的外部站点，必须原样放行
    [InlineData("https://app.oriel.example.com/index.html")]        // 后缀上像的域名不能命中
    [InlineData("https://notapp.oriel/index.html")]                 // 前缀上像的也不能
    [InlineData("https://app.oriel.evil/manual-check.html")]
    [InlineData("file:///etc/passwd")]                             // 非 http(s) 一律不接管
    [InlineData("custom://app.oriel/index.html")]
    public void ForeignHosts_AreLeftAlone(string url)
        => Assert.Null(AssetUrlResolver.TryResolveLocalFile(url, Host, _root));

    [Theory]
    [InlineData("../outside.html")]
    [InlineData("sub/../../outside.html")]
    [InlineData("..%2Foutside.html")]                              // 百分号编码的斜杠同样要挡
    public void DirectoryTraversal_IsRejected(string path)
        => Assert.Null(AssetUrlResolver.TryResolveLocalFile($"https://{Host}/{path}", Host, _root));

    [Fact]
    public void MissingFile_IsRejected()
    {
        // 资源目录里没有这个文件时按"不是内嵌资源"处理：让引擎去报它自己的 404，
        // 而不是把一个不存在的路径当成本地文件加载。
        Assert.Null(AssetUrlResolver.TryResolveLocalFile($"https://{Host}/missing.html", Host, _root));
    }

    [Fact]
    public void MissingAssetDirectory_IsRejected()
    {
        // 没调用 UseEmbeddedAssets 时没有解压目录，任何 URL 都不该被改写成本地文件。
        Assert.Null(AssetUrlResolver.TryResolveLocalFile($"https://{Host}/index.html", Host, null));
        Assert.Null(AssetUrlResolver.TryResolveLocalFile($"https://{Host}/index.html", Host, string.Empty));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/only.html")]
    [InlineData("")]
    public void MalformedUrls_AreRejected(string url)
        => Assert.Null(AssetUrlResolver.TryResolveLocalFile(url, Host, _root));
}
