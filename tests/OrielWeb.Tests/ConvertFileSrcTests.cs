using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>convertFileSrc</c>（F2）：URL 形态、往返对称性、以及**服务端的 scope 强制**。
/// </summary>
/// <remarks>
/// <para>
/// 这组的核心是最后一条：<c>convertFileSrc</c> 是同步的、不查任何权限，
/// 所以"能不能读这个文件"完全靠 <see cref="OrielAssetFileServer"/>。
/// 若那里漏了 scope 检查，它就成了一个**绕过 <c>fs.*</c> scope 的读取通道**——
/// 页面只要拿到 URL 就能把任意文件读进内存。
/// </para>
/// <para>
/// 第二组重要的是"拼与解必须对称"：URL 由宿主侧的 C# 拼、页面侧的 JS 也能拼，
/// 而服务端解出来的路径必须与拼进去的一致。不对称的症状是"某些路径就是取不到"，
/// 而那些路径往往只在特定平台/特定字符下出问题。
/// </para>
/// </remarks>
public sealed class ConvertFileSrcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oriel-src-" + Guid.NewGuid().ToString("N"));

    public ConvertFileSrcTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不该让测试变红
        }

        GC.SuppressFinalize(this);
    }

    // ---- URL 形态 ----

    [Fact]
    public void UrlUsesTheDedicatedFileHost()
    {
        string url = AssetUrl.FileUrl("/tmp/a.png");

        // host 必须是专用的那个：文件内容与页面**不同源**，所以磁盘上的 HTML
        // 被导航到也拿不到桥接。用应用的 host 会让"打开一个下载来的 .html"变成提权路径。
        Assert.StartsWith("oriel://file.oriel/", url, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlDoesNotStartWithTheTrustedAppPrefix()
    {
        string url = AssetUrl.FileUrl("/tmp/a.png");

        // 这条是上一句的"为什么"：桥接只注入到可信前缀（应用的 host），
        // 所以只要文件 URL 不落在那个前缀里，文件内容就天然拿不到 IPC 能力。
        Assert.False(
            url.StartsWith(AssetUrl.TrustedPrefix("app.oriel"), StringComparison.Ordinal),
            "文件 URL 不能落在应用的可信来源前缀里");
    }

    [Theory]
    [InlineData("/tmp/a b.png")]
    [InlineData("/tmp/中文 名.png")]
    [InlineData(@"C:\Program Files\a b\x.png")]
    [InlineData("/tmp/a+b&c=d.png")]
    [InlineData("/tmp/a#b?c.png")]
    public void UrlRoundTripsBackToTheSamePath(string path)
    {
        string url = AssetUrl.FileUrl(path);

        Assert.True(AssetUrl.TryResolveFile(url, out string decoded), $"解析失败：{url}");

        // **原样往返**：路径一个字符都不该变（前导斜杠、反斜杠、空格、非 ASCII、`#?&=` 全部保持）。
        // 这里断言"完全相等"而不是"归一后相等"——归一化的期望值会把"实现偷偷改了路径"
        // 这类缺陷掩盖掉，而那正是这条测试要抓的。
        Assert.Equal(path, decoded);
    }

    [Fact]
    public void UrlKeepsSeparatorsAsSeparators()
    {
        // **整段编码**：分隔符被编进同一个路径段（`%2F`），所以 URL 里只有 URI 自带的
        // 那一个 `/`。这样解析时才不会把"绝对路径的前导斜杠"与"拼 URL 加的那个"搞混
        // ——逐段编码正是栽在这里：`/tmp/a.png` 会解成 `tmp/a.png`，POSIX 上就从绝对变相对。
        string url = AssetUrl.FileUrl("/a/b.png");

        Assert.Equal("oriel://file.oriel/" + Uri.EscapeDataString("/a/b.png"), url);
        Assert.Contains("%2F", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherHostsAreNotFileRequests()
    {
        // 应用自己的 host 不能走文件这条分支，否则内嵌资源会被当成磁盘路径
        Assert.False(AssetUrl.TryResolveFile("oriel://app.oriel/index.html", out _));
        Assert.False(AssetUrl.TryResolveFile("https://example.com/a.png", out _));
    }

    [Fact]
    public void FileHostIsNotResolvedAsAnEmbeddedAsset()
    {
        // 反方向也要成立：文件 host 不能被当成内嵌资源（否则会去资源表里找一个叫
        // "C:/..." 的资源，得到一个 404 而看不出是走错了分支）
        Assert.False(AssetUrl.TryResolve("oriel://file.oriel/C:/a.png", "app.oriel", out _));
    }

    // ---- 服务端：scope 强制 ----

    private static string WriteFile(string root, string name, string content)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AllowedPathIsServed()
    {
        string file = WriteFile(_root, "a.txt", "内容");

        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(file, _ => true);

        Assert.Equal(200, result.Status);
        Assert.Null(result.Reason);

        using var reader = new StreamReader(result.Payload);
        Assert.Equal("内容", reader.ReadToEnd());
    }

    [Fact]
    public void DeniedPathGets403AndNoPayload()
    {
        string file = WriteFile(_root, "a.txt", "秘密");

        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(file, _ => false);

        Assert.Equal(403, result.Status);
        Assert.NotNull(result.Reason);

        // **正文必须是空的**：把内容放进 403 的响应体等于没拦住
        using var reader = new StreamReader(result.Payload);
        Assert.Equal(string.Empty, reader.ReadToEnd());
    }

    [Fact]
    public void DenyReasonNamesTheScopeCommand()
    {
        // 拒绝理由要能指了指"该配哪个 scope"——这条只进日志，页面看不到
        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(_root, _ => false);

        Assert.Contains(OrielAssetFileServer.ScopeCommand, result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeCheckSeesTheNormalizedAbsolutePath()
    {
        // **顺序要求**：先归一、再判 scope。反过来的话
        // "$APPDATA/../../etc/passwd" 会先被判成"在 $APPDATA 之内"（字面量确实在内），
        // 然后归一后指到别处——经典的 scope 绕过。
        //
        // 这里用一个"逃逸"路径，断言授权器看到的已经是归一后的结果。
        string? seen = null;
        string escaping = Path.Combine(_root, "..", "outside.txt");

        OrielAssetFileServer.Serve(escaping, p => { seen = p; return false; });

        Assert.NotNull(seen);
        Assert.DoesNotContain("..", seen!, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(escaping), seen);
    }

    [Fact]
    public void DirectoryIsNotListed()
    {
        // 列目录是 fs.readDir 的职责：这条通道只服务文件。
        // 若它顺手列了目录，页面就多了一条"探测这台机器上有什么"的路子，
        // 而那条路本该有自己的授权。
        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(_root, _ => true);

        Assert.Equal(403, result.Status);
        Assert.Contains("目录", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileGets403NotALeak()
    {
        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(
            Path.Combine(_root, "nope.txt"), _ => true);

        // 不存在也回 403（而不是 404）：把"文件不存在"与"路径不在 scope 内"
        // 分开会让页面能枚举出这台机器上有哪些路径。
        Assert.Equal(403, result.Status);
    }

    [Fact]
    public void ContentTypeComesFromTheExtension()
    {
        string file = Path.Combine(_root, "a.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(file, _ => true);

        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public void UnknownExtensionFallsBackToOctetStream()
    {
        string file = WriteFile(_root, "a.zzz", "x");

        OrielAssetFileServer.Result result = OrielAssetFileServer.Serve(file, _ => true);

        Assert.Contains("octet-stream", result.ContentType, StringComparison.Ordinal);
    }

    // ---- 与 scope 的接线：asset.read 是独立的一条 ----

    [Fact]
    public void AssetReadIsNotCoveredByFsScope()
    {
        // **照 Tauri 的设计**：assetProtocol.scope 与 fs 的 scope 是两套配置。
        // "把图片显示出来"与"以程序的方式读写文件"是两种意图，共用一个 scope 会让
        // 只想显示一张图的应用顺带获得读任意文本的能力。
        OrielCapabilityOptions options = new();
        options.Allow("fs.*", s => s.AllowPath(_root + "/**"));

        Assert.False(
            options.IsPathAllowed(OrielAssetFileServer.ScopeCommand, Path.Combine(_root, "a.png"), "app", out _),
            "fs 的 scope 不该覆盖 asset.read");
    }

    [Fact]
    public void AssetReadScopeWorksWhenDeclared()
    {
        OrielCapabilityOptions options = new();
        options.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath(_root.Replace('\\', '/') + "/**"));

        Assert.True(options.IsPathAllowed(
            OrielAssetFileServer.ScopeCommand, Path.Combine(_root, "a.png"), "app", out _));

        // **归一要先做**：`IsPathAllowed` 的契约是"收一个已归一的绝对路径"
        // （见 OrielAssetFileServer.Serve——它先归一、再判 scope）。
        // 直接拿带 `..` 的字符串进来会被判成"仍在根之内"（字面量确实在），
        // 那正是经典的 scope 绕过，也正是为什么归一必须在判定之前。
        string escaping = Path.GetFullPath(Path.Combine(_root, "..", "outside.png"));

        Assert.False(options.IsPathAllowed(OrielAssetFileServer.ScopeCommand, escaping, "app", out _));
    }

    [Fact]
    public void ScopeCheckIsWhyNormalizationMustComeFirst()
    {
        // 上面那条的"反面教材"，显式钉住这个顺序约束不是可选的：
        // 同一个逃逸路径，**没归一**时会被判成允许。
        OrielCapabilityOptions options = new();
        options.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath(_root.Replace('\\', '/') + "/**"));

        string notNormalized = Path.Combine(_root, "..", "outside.png");

        Assert.True(
            options.IsPathAllowed(OrielAssetFileServer.ScopeCommand, notNormalized, "app", out _),
            "这条断言描述的是**不归一会发生什么**——它通过才是对的，因为它证明了顺序的必要性");

        // 而归一之后就被拒了（上面那条）
        Assert.False(options.IsPathAllowed(
            OrielAssetFileServer.ScopeCommand, Path.GetFullPath(notNormalized), "app", out _));
    }
}
