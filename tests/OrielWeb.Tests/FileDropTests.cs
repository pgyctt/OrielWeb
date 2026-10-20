using System.Text.Json;
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

    // ------------------------------------------------------------------
    // 推到页面的 payload（B8）
    //
    // 这一段与上面的 URI 解析是两件事：上面回答"拖进来的是什么路径"，这里回答
    // "那条路径怎么变成页面的事件载荷"。它值得单独测，因为**手写 JSON 的转义错了
    // 不会在 C# 侧报错**——页面那边 `JSON.parse` 才炸，而且炸在很远的地方。
    // ------------------------------------------------------------------

    [Fact]
    public void PayloadEventNameIsHostSide()
    {
        // 宿主侧用本库的命名法（`drag.drop`），页面侧看到的是 Tauri 的
        // `tauri://drag-drop`——翻译在 event.js 的 HOST_EVENT_MAP 里。
        // 两边各存一份字面量，这条钉住 C# 这一份，window.test.mjs 钉住 JS 那一份。
        Assert.Equal("drag.drop", OrielApp.DragDropEventName);
    }

    [Fact]
    public void EnterPayloadCarriesPathsAndPosition()
    {
        var args = new OrielDragEventArgs(OrielDragEventType.Enter, ["/home/x/a.txt"], 12, 34);

        using var doc = JsonDocument.Parse(OrielApp.DragDropPayloadJson(args));

        Assert.Equal("enter", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("/home/x/a.txt", doc.RootElement.GetProperty("paths")[0].GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("position").GetProperty("x").GetInt32());
        Assert.Equal(34, doc.RootElement.GetProperty("position").GetProperty("y").GetInt32());
    }

    [Fact]
    public void OverPayloadHasPositionButNoPaths()
    {
        // Tauri 的 drag-over 不带 paths：拖动过程中还没解析（也不需要）载荷
        var args = new OrielDragEventArgs(OrielDragEventType.Over, [], 5, 6);

        using var doc = JsonDocument.Parse(OrielApp.DragDropPayloadJson(args));

        Assert.Equal("over", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("paths", out _));
        Assert.Equal(5, doc.RootElement.GetProperty("position").GetProperty("x").GetInt32());
    }

    [Fact]
    public void LeavePayloadHasNeitherPathsNorPosition()
    {
        // leave 的坐标是占位 0,0，不是"拖到了左上角"——所以整个字段都不发
        var args = new OrielDragEventArgs(OrielDragEventType.Leave, [], 0, 0);

        using var doc = JsonDocument.Parse(OrielApp.DragDropPayloadJson(args));

        Assert.Equal("leave", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("position", out _));
        Assert.False(doc.RootElement.TryGetProperty("paths", out _));
    }

    [Fact]
    public void DropPayloadKeepsOrderAndCount()
    {
        var args = new OrielDragEventArgs(OrielDragEventType.Drop, ["/a", "/b", "/c"], 0, 0);

        using var doc = JsonDocument.Parse(OrielApp.DragDropPayloadJson(args));
        string[] paths = doc.RootElement.GetProperty("paths")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(["/a", "/b", "/c"], paths);
    }

    [Fact]
    public void EmptyDropIsEmptyArrayNotNull()
    {
        // `paths: null` 会让页面侧 `.map()` 当场抛；空数组才是"拖进来 0 个"的正确形状
        var args = new OrielDragEventArgs(OrielDragEventType.Drop, [], 0, 0);

        Assert.Equal(
            "{\"type\":\"drop\",\"paths\":[],\"position\":{\"x\":0,\"y\":0}}",
            OrielApp.DragDropPayloadJson(args));
    }

    [Fact]
    public void PayloadEscapesQuotesAndBackslashes()
    {
        // Windows 路径本身就带反斜杠；文件名里带引号也合法（POSIX 上）。
        // 手写 JSON 最容易在这里出错：漏转义 → 页面 JSON.parse 失败。
        const string tricky = @"C:\tmp\a""b\报告.txt";
        var args = new OrielDragEventArgs(OrielDragEventType.Drop, [tricky], 0, 0);

        using var doc = JsonDocument.Parse(OrielApp.DragDropPayloadJson(args));
        Assert.Equal(tricky, doc.RootElement.GetProperty("paths")[0].GetString());
    }

    [Fact]
    public void FourStageEventNamesMatchHostConvention()
    {
        // 宿主侧用本库命名法（drag.*），页面侧看到 Tauri 的名字（tauri://drag-*），
        // 翻译在 event.js 的 HOST_EVENT_MAP。两边各存一份字面量，各钉各的。
        Assert.Equal("drag.enter", OrielApp.DragEnterEventName);
        Assert.Equal("drag.over", OrielApp.DragOverEventName);
        Assert.Equal("drag.drop", OrielApp.DragDropEventName);
        Assert.Equal("drag.leave", OrielApp.DragLeaveEventName);
    }
}
