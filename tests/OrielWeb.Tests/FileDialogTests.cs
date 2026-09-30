using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 文件对话框"纯逻辑部分"的单测：过滤器在三种平台形状之间的转换，以及原生返回值的解析。
/// </summary>
/// <remarks>
/// 对话框本身弹出后无法在无头环境里断言（README 的验证账里如实标着"需人眼"），
/// 但这些转换是**机器可判定**的，而且正是最容易出错的地方——例如 Win32 的双 null 结尾少一个
/// 就会读越界、多选缓冲区按第一个 <c>\0</c> 截断就只能拿到目录段。
/// 它们全都在 Linux 的 CI 上跑，包括为 Windows 写的那两个。
/// </remarks>
public sealed class FileDialogTests
{
    // ---- OrielFileFilter.Parse：兼容旧的字符串写法 ----

    [Fact]
    public void Parse_StringForm()
    {
        var filters = OrielFileFilter.Parse("文本文件|*.txt;*.md|所有文件|*.*");

        Assert.Equal(2, filters.Count);
        Assert.Equal("文本文件", filters[0].Name);
        Assert.Equal(["*.txt", "*.md"], filters[0].Patterns);
        Assert.Equal("所有文件", filters[1].Name);
        Assert.Equal(["*.*"], filters[1].Patterns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyGivesNoFilters(string? filter)
        => Assert.Empty(OrielFileFilter.Parse(filter));

    [Fact]
    public void Parse_ToleratesTrailingPipe()
    {
        // 末尾多一个 '|' 是老写法的常见笔误，不该产出一条空过滤器
        var filters = OrielFileFilter.Parse("文本文件|*.txt|");

        Assert.Single(filters);
        Assert.Equal("文本文件", filters[0].Name);
    }

    [Fact]
    public void Parse_DropsNameWithoutPattern()
    {
        // 奇数段：最后那个只有名字没有模式。它在界面上不可选，留着只会让人困惑
        var filters = OrielFileFilter.Parse("文本文件|*.txt|所有文件");

        Assert.Single(filters);
        Assert.Equal("文本文件", filters[0].Name);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundSegments()
    {
        var filters = OrielFileFilter.Parse(" 文本文件 | *.txt ; *.md ");

        Assert.Equal("文本文件", filters[0].Name);
        Assert.Equal(["*.txt", "*.md"], filters[0].Patterns);
    }

    // ---- RenderForWin32：双 null 结尾是硬要求 ----

    [Fact]
    public void RenderForWin32_EndsWithDoubleNull()
    {
        string rendered = OrielFileFilter.RenderForWin32([OrielFileFilter.Of("文本", "*.txt")]);

        Assert.Equal("文本\0*.txt\0\0", rendered);
    }

    [Fact]
    public void RenderForWin32_JoinsPatternsWithSemicolon()
    {
        string rendered = OrielFileFilter.RenderForWin32([OrielFileFilter.Of("文本", "*.txt", "*.md")]);

        Assert.Equal("文本\0*.txt;*.md\0\0", rendered);
    }

    [Fact]
    public void RenderForWin32_MultipleFilters()
    {
        string rendered = OrielFileFilter.RenderForWin32(
        [
            OrielFileFilter.Of("文本", "*.txt"),
            OrielFileFilter.Of("图片", "*.png"),
        ]);

        Assert.Equal("文本\0*.txt\0图片\0*.png\0\0", rendered);
    }

    [Fact]
    public void RenderForWin32_EmptyFallsBackToAllFiles()
    {
        // 不喂过滤器时给一份可用的：Windows 没有过滤器会让类型下拉显示不出来
        string rendered = OrielFileFilter.RenderForWin32([]);

        Assert.Equal("所有文件\0*.*\0\0", rendered);
    }

    [Fact]
    public void RenderForWin32_SkipsFilterWithoutPatterns()
    {
        string rendered = OrielFileFilter.RenderForWin32(
        [
            new OrielFileFilter { Name = "空条目", Patterns = [] },
            OrielFileFilter.Of("文本", "*.txt"),
        ]);

        Assert.Equal("文本\0*.txt\0\0", rendered);
    }

    // ---- GtkPatterns：fnmatch 语义下 "*.*" 会漏掉无扩展名文件 ----

    [Fact]
    public void GtkPatterns_NormalizesAllFilesPattern()
    {
        // "*.*" 在 fnmatch 里要求文件名含点，README/Makefile 会被过滤掉——必须归一成 "*"
        Assert.Equal(["*"], OrielFileFilter.GtkPatterns([OrielFileFilter.AllFiles]));
    }

    [Fact]
    public void GtkPatterns_ExpandsEveryFilter()
    {
        var patterns = OrielFileFilter.GtkPatterns(
        [
            OrielFileFilter.Of("文本", "*.txt", "*.md"),
            OrielFileFilter.Of("图片", "*.png"),
        ]);

        Assert.Equal(["*.txt", "*.md", "*.png"], patterns);
    }

    // ---- CocoaExtensions：Cocoa 要的是扩展名，且没有"任意文件"的写法 ----

    [Fact]
    public void CocoaExtensions_StripsWildcardPrefix()
        => Assert.Equal(["txt", "md"], OrielFileFilter.CocoaExtensions([OrielFileFilter.Of("文本", "*.txt", "*.md")]));

    [Fact]
    public void CocoaExtensions_KeepsCompoundExtension()
        => Assert.Equal(["tar.gz"], OrielFileFilter.CocoaExtensions([OrielFileFilter.Of("归档", "*.tar.gz")]));

    [Fact]
    public void CocoaExtensions_DropsMatchEverything()
    {
        // 表示"不限类型"的方式是根本不设 allowedFileTypes，塞 "*" 反而锁死面板
        Assert.Empty(OrielFileFilter.CocoaExtensions([OrielFileFilter.AllFiles]));
        Assert.Empty(OrielFileFilter.CocoaExtensions([OrielFileFilter.Of("任意", "*")]));
    }

    [Fact]
    public void CocoaExtensions_DeduplicatesIgnoringCase()
        => Assert.Equal(["txt"], OrielFileFilter.CocoaExtensions([OrielFileFilter.Of("文本", "*.txt", "*.TXT")]));

    // ---- ParseWin32MultiSelect：单段与多段是两种完全不同的形状 ----

    [Fact]
    public void Win32MultiSelect_SingleFileIsFullPath()
    {
        // 只选一个文件时，缓冲区里就是完整路径（没有分隔符），不能当成"目录 + 文件名"去拼
        string[] paths = OrielFileDialogSupport.ParseWin32MultiSelect(@"C:\dir\file.txt");

        Assert.Equal([@"C:\dir\file.txt"], paths);
    }

    [Fact]
    public void Win32MultiSelect_MultipleFilesJoinWithDirectory()
    {
        // 多选时第一段是目录、之后每段是一个文件名，需要自己拼回来
        string[] paths = OrielFileDialogSupport.ParseWin32MultiSelect("C:\\dir\0a.txt\0b.txt\0\0");

        Assert.Equal([@"C:\dir\a.txt", @"C:\dir\b.txt"], paths);
    }

    [Fact]
    public void Win32MultiSelect_TrailingSeparatorDoesNotDouble()
    {
        // 目录段可能带尾随分隔符（资源管理器风格），拼出来不能是 C:\dir\\a.txt
        string[] paths = OrielFileDialogSupport.ParseWin32MultiSelect("C:\\dir\\\0a.txt\0\0");

        Assert.Equal([@"C:\dir\a.txt"], paths);
    }

    [Fact]
    public void Win32MultiSelect_PosixPathsStayIntact()
    {
        // 这条在 Linux 上跑：解析必须是纯字符串处理，
        // 一旦用了 Path.Combine，C:\dir 会被当成相对路径而拼错（Windows 上则反之）
        string[] paths = OrielFileDialogSupport.ParseWin32MultiSelect("/home/x\0a.txt\0b.txt\0\0");

        Assert.Equal(["/home/x/a.txt", "/home/x/b.txt"], paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0")]
    public void Win32MultiSelect_EmptyGivesNoPaths(string? buffer)
        => Assert.Empty(OrielFileDialogSupport.ParseWin32MultiSelect(buffer));

    // ---- EnsureExtension：GTK/Cocoa 不会替我们补扩展名 ----

    [Fact]
    public void EnsureExtension_Appends()
        => Assert.Equal("/home/x/报告.txt", OrielFileDialogSupport.EnsureExtension("/home/x/报告", "txt"));

    [Fact]
    public void EnsureExtension_AcceptsLeadingDot()
        => Assert.Equal("报告.txt", OrielFileDialogSupport.EnsureExtension("报告", ".txt"));

    [Fact]
    public void EnsureExtension_KeepsExistingExtension()
    {
        // 用户手写了 .md 就尊重它，而不是变成 .md.txt
        Assert.Equal("报告.md", OrielFileDialogSupport.EnsureExtension("报告.md", "txt"));
    }

    [Fact]
    public void EnsureExtension_IgnoresDotInDirectoryName()
    {
        // 目录名里的点不算文件扩展名：/a.b/报告 仍然要补成 /a.b/报告.txt
        Assert.Equal("/a.b/报告.txt", OrielFileDialogSupport.EnsureExtension("/a.b/报告", "txt"));
    }

    [Fact]
    public void EnsureExtension_NoopWithoutExtension()
        => Assert.Equal("/home/x/报告", OrielFileDialogSupport.EnsureExtension("/home/x/报告", null));
}
