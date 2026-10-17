using OrielWeb;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 托盘选项与多托盘注册表（对标 Tauri 2 的 <c>TrayIconOptions</c> / <c>getById</c> / <c>removeById</c>）。
/// </summary>
/// <remarks>
/// 需要真实原生托盘的用例（真的出现在通知区/菜单栏）只能在真机上人眼验证；
/// 这里钉住的是**不依赖原生资源的契约**：图标解码入口、模板标记默认值、id 分配与冲突。
/// </remarks>
public sealed class TrayOptionsTests
{
    [Fact]
    public void IconAsTemplate_DefaultsToTrue()
    {
        // 单色剪影是托盘的常态，模板图会自动跟随菜单栏深浅色反色
        Assert.True(new OrielTrayOptions().IconAsTemplate);
    }

    [Fact]
    public void IconAsTemplate_CanBeTurnedOff_ForColorIcons()
    {
        // 彩色品牌图必须能显式关掉——此前它被无条件设 true，调用方无从表达
        var options = new OrielTrayOptions { IconAsTemplate = false };
        Assert.False(options.IconAsTemplate);
    }

    [Fact]
    public void IconPath_Assignment_DoesNotReadTheFile()
    {
        // 关键：赋值**不读盘**（解码推迟到 ResolveIcon）。否则 AddTray 这个配置期回调
        // 会因为一个缺失的图标文件而抛异常——那正是 0.3.5 修掉的缺陷类别。
        var options = new OrielTrayOptions { IconPath = "no-such-file-anywhere.png" };

        Assert.NotNull(options.IconPath);          // 路径记住了
        Assert.Null(options.ResolveIcon());        // 但解不出图标（回落平台默认）
    }

    [Fact]
    public void ResolveIcon_MissingFile_ReportsDiagnosticAndReturnsNull()
    {
        var seen = new List<string>();
        Action<OrielDiagnosticEventArgs> collector = e => seen.Add(e.Message);

        OrielDiagnostics.Reported += collector;
        OrielImage? resolved;
        try
        {
            resolved = new OrielTrayOptions { IconPath = "missing.png" }.ResolveIcon();
        }
        finally
        {
            OrielDiagnostics.Reported -= collector;
        }

        Assert.Null(resolved);
        Assert.Contains(seen, m => m.Contains("托盘图标无法使用", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveIcon_ExplicitIcon_WinsOverPath()
    {
        var image = new OrielImage(new byte[] { 1, 2, 3, 255 }, 1, 1);
        var options = new OrielTrayOptions { IconPath = "missing.png", Icon = image };

        Assert.Same(image, options.ResolveIcon());
        Assert.Null(options.IconPath); // 设 Icon 会清掉路径（后赋值的生效）
    }

    [Fact]
    public void IconPathOrNull_MissingFile_ReturnsNull()
    {
        var options = new OrielTrayOptions { IconPath = "missing.png" };
        Assert.Null(options.IconPathOrNull);
    }

    [Fact]
    public void IconPath_RelativePath_IsResolvedAgainstBaseDirectory()
    {
        var options = new OrielTrayOptions { IconPath = "tray.png" };

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tray.png")),
            options.IconPath);
    }

    [Fact]
    public void ResolveIcon_RealFile_DecodesIntoImage()
    {
        // 用仓库里真实的 app.png（若产物不在则跳过）
        string? path = FindDemoAsset("app.png");
        if (path is null)
        {
            return;
        }

        var options = new OrielTrayOptions { IconPath = path };
        OrielImage? image = options.ResolveIcon();

        Assert.NotNull(image);
        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
    }

    [Fact]
    public void Title_DefaultsToNull()
    {
        Assert.Null(new OrielTrayOptions().Title);
    }

    [Fact]
    public void Tooltip_DefaultsToAppName()
    {
        Assert.Equal("OrielWeb", new OrielTrayOptions().Tooltip);
    }

    [Fact]
    public void MenuOnClick_DefaultsToFalse()
    {
        // Windows 上左键默认只回调、右键才弹菜单（macOS/Linux 上此项无效）
        Assert.False(new OrielTrayOptions().MenuOnClick);
    }

    [Fact]
    public void Builder_AddTray_CanBeCalledMultipleTimes_ForMultipleTrays()
    {
        var builder = Oriel.CreateBuilder();
        builder.AddTray(o => o.Id = "a");
        builder.AddTray(o => o.Id = "b");

        Assert.Equal(2, builder.TrayOptionsList.Count);
        // 兼容既有单托盘代码：TrayOptions 是第一个
        Assert.Equal("a", builder.TrayOptions!.Id);
    }

    private static string? FindDemoAsset(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "OrielDemo", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
