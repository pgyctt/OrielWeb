using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// Shell 集成的单测：scheme 白名单与"动作 → 系统命令"的翻译。
/// </summary>
/// <remarks>
/// 这两件事都必须能离线断言：白名单是**安全边界**（放错一个 scheme 就等于把
/// <c>file:</c> 交给系统默认处理器），而三条系统命令各有各的怪癖
/// （explorer 的逗号、open 的 -R、xdg-open 没有"选中"概念），靠肉眼 review 很容易漏。
/// </remarks>
public sealed class ShellPolicyTests
{
    private static IReadOnlyList<string> Default => OrielShellOptions.DefaultSchemes;

    // ---- URL 白名单 ----

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void AllowedUrls(string url) => Assert.True(OrielShellPolicy.IsAllowedUrl(url, Default));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/etc/passwd")]
    [InlineData("foo/bar")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://example.com\n--flag")]
    [InlineData("cmd:\r\nwhoami")]
    public void RejectedUrls(string? url) => Assert.False(OrielShellPolicy.IsAllowedUrl(url, Default));

    [Fact]
    public void SchemeMatchIsCaseInsensitive()
        => Assert.True(OrielShellPolicy.IsAllowedUrl("MaIlTo:a@b", Default));

    [Fact]
    public void CustomSchemeCanBeAllowed()
    {
        IReadOnlyList<string> extended = new OrielShellOptions().WithScheme("myapp:").AllowedSchemes;

        Assert.True(OrielShellPolicy.IsAllowedUrl("myapp://open?id=1", extended));
        // 扩展白名单不该顺手放行别的 scheme
        Assert.False(OrielShellPolicy.IsAllowedUrl("file:///etc/passwd", extended));
    }

    [Fact]
    public void WithSchemeIsIdempotentAndTrimsColon()
    {
        var options = new OrielShellOptions().WithScheme("HTTPS:").WithScheme("https");
        // xUnit2031：用带谓词的 Assert.Single 重载，而不是先 Where 再断言
        Assert.Single(options.AllowedSchemes, s => s.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 路径校验 ----

    [Fact]
    public void UsablePath_AcceptsExistingFileAndDirectory()
    {
        string file = Path.GetTempFileName();
        try
        {
            Assert.True(OrielShellPolicy.IsUsablePath(file));
            Assert.True(OrielShellPolicy.IsUsablePath(Path.GetDirectoryName(file)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void UsablePath_RejectsMissingAndRelative()
    {
        Assert.False(OrielShellPolicy.IsUsablePath(Path.Combine(Path.GetTempPath(), "oriel-does-not-exist-12345")));
        Assert.False(OrielShellPolicy.IsUsablePath("relative/path"));
        Assert.False(OrielShellPolicy.IsUsablePath(""));
        Assert.False(OrielShellPolicy.IsUsablePath(null));
    }

    // ---- 命令翻译 ----

    [Fact]
    public void Windows_RevealUsesExplorerSelectWithoutSpace()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.RevealPath, @"C:\a\b.txt", isWindows: true, isMacOS: false);

        Assert.Equal("explorer.exe", exe);
        // 逗号后不能有空格：写成 "/select, C:\a\b.txt" 会被 explorer 当成两个参数而静默失败
        Assert.Equal([@"/select,C:\a\b.txt"], args);
    }

    [Fact]
    public void Windows_OpenUrlHasNoArguments()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.OpenUrl, "https://example.com", isWindows: true, isMacOS: false);

        Assert.Equal("https://example.com", exe);
        Assert.Empty(args);
    }

    [Fact]
    public void MacOS_RevealUsesDashR()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.RevealPath, "/Users/x/a.txt", isWindows: false, isMacOS: true);

        Assert.Equal("/usr/bin/open", exe);
        Assert.Equal(["-R", "/Users/x/a.txt"], args);
    }

    [Fact]
    public void MacOS_OpenUrlUsesOpen()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.OpenUrl, "https://example.com", isWindows: false, isMacOS: true);

        Assert.Equal("/usr/bin/open", exe);
        Assert.Equal(["https://example.com"], args);
    }

    [Fact]
    public void Linux_RevealOpensParentDirectory()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.RevealPath, "/home/x/a.txt", isWindows: false, isMacOS: false);

        // xdg-open 没有"选中文件"的入口，只能退一步打开所在目录
        Assert.Equal("xdg-open", exe);
        Assert.Equal(["/home/x"], args);
    }

    [Fact]
    public void Linux_OpenUrlUsesXdgOpen()
    {
        (string exe, IReadOnlyList<string> args) = OrielShellCommand.Build(
            OrielShellAction.OpenUrl, "https://example.com", isWindows: false, isMacOS: false);

        Assert.Equal("xdg-open", exe);
        Assert.Equal(["https://example.com"], args);
    }
}
