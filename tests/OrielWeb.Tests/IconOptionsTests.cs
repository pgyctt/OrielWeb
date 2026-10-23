using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 图标选项的降级语义与相对路径解析（下游反馈 2026-10-14 的 P1-2 / P1-3）。
/// </summary>
/// <remarks>
/// 两条都是"现场才发现"的形态：图标缺失此前在配置期抛 <see cref="FileNotFoundException"/>，
/// 把纯观感问题升级成"双击没反应"；相对路径此前按 cwd 解析，而工控机从快捷方式/计划任务/
/// 开机自启启动时 cwd 常常不是程序目录。这里把它们钉成可回归的契约。
/// </remarks>
public sealed class IconOptionsTests
{
    /// <summary>临时目录里造一个真实存在的文件，返回其绝对路径。</summary>
    private static string CreateTempFile(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"oriel-icon-{Guid.NewGuid():N}", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    [Fact]
    public void Icon_MissingFile_DoesNotThrow_DegradesToNull()
    {
        var options = new OrielWindowOptions();

        // 关键：不抛异常。缺失是观感问题，不该让整个程序起不来
        options.Icon = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.ico");

        Assert.Null(options.Icon);
        Assert.NotNull(options.RequestedIcon); // 原始请求仍可查，排障用
    }

    [Fact]
    public void Icon_MissingFile_ReportsDiagnostic()
    {
        using var diagnostics = new DiagnosticCollector();

        new OrielWindowOptions().Icon = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.ico");

        Assert.True(diagnostics.Contains("窗口图标不存在"));
    }

    [Fact]
    public void Icon_ExistingFile_IsAccepted()
    {
        string path = CreateTempFile("app.ico");
        try
        {
            var options = new OrielWindowOptions { Icon = path };

            Assert.Equal(path, options.Icon);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void RequireIcon_MissingFile_Throws()
    {
        var options = new OrielWindowOptions { RequireIcon = true };

        var ex = Assert.Throws<FileNotFoundException>(
            () => options.Icon = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.ico"));

        Assert.Contains("图标文件不存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIcon_SetAfterIcon_StillThrows()
    {
        var options = new OrielWindowOptions
        {
            Icon = Path.Combine(Path.GetTempPath(), $"oriel-missing-{Guid.NewGuid():N}.ico"),
        };

        // 两种设置顺序都要能重新求值（属性 setter 里都调 EvaluateIcon）
        Assert.Throws<FileNotFoundException>(() => options.RequireIcon = true);
    }

    [Fact]
    public void Icon_RelativePath_ResolvesAgainstBaseDirectory_NotCwd()
    {
        const string relative = "definitely-not-in-cwd.png";
        var options = new OrielWindowOptions { Icon = relative };

        // 解析基准是 exe 所在目录，不是当前工作目录
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)),
            options.RequestedIcon);
    }

    [Fact]
    public void Icon_AbsolutePath_IsUsedAsIs()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "oriel-absolute-check.png");
        var options = new OrielWindowOptions { Icon = absolute };

        Assert.Equal(absolute, options.RequestedIcon);
    }

    [Fact]
    public void Icon_SetToNull_ClearsBothValues()
    {
        var options = new OrielWindowOptions { Icon = Path.Combine(Path.GetTempPath(), "whatever.png") };

        options.Icon = null;

        Assert.Null(options.Icon);
        Assert.Null(options.RequestedIcon);
    }

    [Fact]
    public void Icon_BlankValue_Throws()
    {
        var options = new OrielWindowOptions();

        // 空白串是调用方写错了（不是"文件缺失"），仍然立刻报错
        Assert.Throws<ArgumentException>(() => options.Icon = "   ");
    }

    [Fact]
    public void TrayIconPath_RelativePath_ResolvesAgainstBaseDirectory()
    {
        const string relative = "tray.png";
        var options = new OrielTrayOptions { IconPath = relative };

        // IconPath 读回解析后的绝对路径；**赋值不读盘**（解码推迟到建托盘时，见 ResolveIcon）
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)),
            options.IconPath);
    }

    [Fact]
    public void NotificationIconPath_RelativePath_ResolvesAgainstBaseDirectory()
    {
        const string relative = "notify.png";
        var options = new OrielNotificationOptions { IconPath = relative };

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)),
            options.IconPath);
    }

    [Fact]
    public void OrielPathResolver_AbsoluteInput_IsUnchanged()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\some\dir\file.png" : "/some/dir/file.png";

        Assert.Equal(absolute, OrielPathResolver.Resolve(absolute));
    }

    [Fact]
    public void OrielPathResolver_RelativeInput_CombinesWithBaseDirectory()
    {
        string resolved = OrielPathResolver.Resolve("sub/dir/file.png");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(resolved),
            StringComparison.Ordinal);
    }
}
