using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 拖放载荷解析的单测：把 <c>file://</c> URI 转成本地路径。
/// </summary>
/// <remarks>
/// 拖放要真人操作才验证得了（README 的验证账里如实标着），但这一步是纯字符串处理，
/// 而它有一堆容易写错的边界：百分号编码、Windows 盘符、主机名段、非 file 协议。
/// 这些用例在任意平台上都跑得一样——这正是刻意不用 <c>System.Uri.LocalPath</c> 的原因
/// （那个的结果随平台变，为 Linux 写的断言在 Linux 上测不出 Windows 的形状）。
/// </remarks>
public sealed class FileDropTests
{
    [Fact]
    public void PlainPosixPath()
        => Assert.Equal("/home/x/a.txt", OrielFileDropSupport.UriToPath("file:///home/x/a.txt"));

    [Fact]
    public void SingleSlashForm()
    {
        // "file:/home/x"（只有一个斜杠）也是合法的 file URI
        Assert.Equal("/home/x", OrielFileDropSupport.UriToPath("file:/home/x"));
    }

    [Fact]
    public void PercentEncodedSpace()
        => Assert.Equal("/home/x/a b.txt", OrielFileDropSupport.UriToPath("file:///home/x/a%20b.txt"));

    [Fact]
    public void PercentEncodedNonAscii()
        => Assert.Equal("/home/x/报告.txt", OrielFileDropSupport.UriToPath("file:///home/x/%E6%8A%A5%E5%91%8A.txt"));

    [Fact]
    public void PlusIsNotADecodedSpace()
    {
        // 百分号解码**不**把 '+' 当空格（那是 form-urlencoded 的规则）。
        // 混了就会得到"文件名里明明有加号，读到却没有"这种极难查的错误。
        Assert.Equal("/home/x/a+b.txt", OrielFileDropSupport.UriToPath("file:///home/x/a+b.txt"));
    }

    [Fact]
    public void EncodedSlashBecomesSeparator()
    {
        // %2F 解码后就是 '/'——行为如此，写下来是为了它是"已知"而不是"意外"
        Assert.Equal("/home/x/a/b.txt", OrielFileDropSupport.UriToPath("file:///home/x/a%2Fb.txt"));
    }

    [Fact]
    public void WindowsDriveLetter()
    {
        // file:///C:/x → 去掉路径前导的那个 '/'，否则会得到 /C:/x 这种谁也不认识的路径。
        // 这条在 Linux 上同样要过——解析是纯字符串处理，不该看运行平台。
        Assert.Equal("C:/x/a.txt", OrielFileDropSupport.UriToPath("file:///C:/x/a.txt"));
    }

    [Fact]
    public void LocalhostAuthorityIsIgnored()
        => Assert.Equal("/home/x/a.txt", OrielFileDropSupport.UriToPath("file://localhost/home/x/a.txt"));

    [Fact]
    public void RemoteHostIsRejected()
    {
        // file://server/share 是网络路径，不是本地文件；如实返回 null，
        // 而不是拼出一个看起来像本地路径的串
        Assert.Null(OrielFileDropSupport.UriToPath("file://server/share/a.txt"));
    }

    [Fact]
    public void HostWithoutPathIsRejected()
        => Assert.Null(OrielFileDropSupport.UriToPath("file://server"));

    [Theory]
    [InlineData("http://example.com/a.txt")]
    [InlineData("https://example.com/a.txt")]
    [InlineData("data:text/plain,hello")]
    [InlineData("javascript:alert(1)")]
    public void NonFileSchemesAreRejected(string uri)
        => Assert.Null(OrielFileDropSupport.UriToPath(uri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:")]
    [InlineData("file://")]
    public void EmptyOrPathlessGivesNull(string? uri)
        => Assert.Null(OrielFileDropSupport.UriToPath(uri));

    [Fact]
    public void SchemeIsCaseInsensitive()
        => Assert.Equal("/home/x", OrielFileDropSupport.UriToPath("FILE:///home/x"));

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        // 载荷里的 uri 可能带换行（uri-list 以 CRLF 分隔）
        Assert.Equal("/home/x/a.txt", OrielFileDropSupport.UriToPath("  file:///home/x/a.txt\r\n"));
    }

    [Fact]
    public void BatchKeepsOrderAndDropsNonFiles()
    {
        IReadOnlyList<string> paths = OrielFileDropSupport.UrisToPaths(
        [
            "file:///home/x/a.txt",
            "http://example.com/b.txt",
            "file:///home/x/c.txt",
        ]);

        Assert.Equal(["/home/x/a.txt", "/home/x/c.txt"], paths);
    }

    [Fact]
    public void BatchAllRejectedGivesEmpty()
        => Assert.Empty(OrielFileDropSupport.UrisToPaths(["http://a", "ftp://b"]));
}
