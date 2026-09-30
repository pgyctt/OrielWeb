using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 开机自启配置文本的单测。
/// </summary>
/// <remarks>
/// 这些文本的正确性没法靠"写进去没报错"确认：自启项写错了不会当场失败，
/// 而是等用户下次开机才发现应用没起来——所以逐字符断言。
/// </remarks>
public sealed class AutoStartTests
{
    private const string Exe = "/opt/My App/myapp";

    // ---- Linux：freedesktop autostart ----

    [Fact]
    public void DesktopEntry_HasRequiredKeys()
    {
        string content = AutoStartContent.LinuxDesktopEntry("myapp", Exe, null);

        Assert.StartsWith("[Desktop Entry]\n", content, StringComparison.Ordinal);
        Assert.Contains("Type=Application\n", content, StringComparison.Ordinal);
        Assert.Contains("Name=myapp\n", content, StringComparison.Ordinal);
        Assert.Contains("Terminal=false\n", content, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-enabled=true\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntry_QuotesExecutablePath()
    {
        // 含空格的路径不加引号会被解析成"程序 + 参数"（规范规定 Exec 按引用规则切词）
        string content = AutoStartContent.LinuxDesktopEntry("myapp", Exe, null);
        Assert.Contains($"Exec=\"{Exe}\"\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntry_AppendsArguments()
    {
        string content = AutoStartContent.LinuxDesktopEntry("myapp", Exe, ["--minimized", "--profile=dev"]);
        Assert.Contains($"Exec=\"{Exe}\" --minimized --profile=dev\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntry_QuotesArgumentWithSpace()
    {
        string content = AutoStartContent.LinuxDesktopEntry("myapp", Exe, ["--title=My App"]);
        // 给**整个**参数加引号（而不是只给等号右边的值加）：Exec 按引用规则切词，
        // 解析结果仍是单个参数 --title=My App，且与 Windows 侧的命令行写法一致
        Assert.Contains(" \"--title=My App\"\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntry_NoArgumentsHasNoTrailingSpace()
    {
        string content = AutoStartContent.LinuxDesktopEntry("myapp", Exe, []);
        Assert.Contains($"Exec=\"{Exe}\"\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntry_EscapesNewlineInName()
    {
        string content = AutoStartContent.LinuxDesktopEntry("my\napp", Exe, null);
        Assert.Contains("Name=my\\napp\n", content, StringComparison.Ordinal);
    }

    // ---- macOS：LaunchAgent ----

    [Fact]
    public void LaunchAgent_HasLabelAndRunAtLoad()
    {
        string content = AutoStartContent.MacOSLaunchAgent("com.example.myapp", "/Applications/MyApp.app/Contents/MacOS/myapp", null);

        Assert.Contains("<plist version=\"1.0\">", content, StringComparison.Ordinal);
        Assert.Contains("<key>Label</key>\n    <string>com.example.myapp</string>", content, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchAgent_PutsExecutableAndArgumentsInOneArray()
    {
        string content = AutoStartContent.MacOSLaunchAgent("myapp", "/opt/myapp", ["--minimized"]);
        int arrayStart = content.IndexOf("<array>", StringComparison.Ordinal);
        int exeIndex = content.IndexOf("<string>/opt/myapp</string>", StringComparison.Ordinal);
        int argIndex = content.IndexOf("<string>--minimized</string>", StringComparison.Ordinal);
        int arrayEnd = content.IndexOf("</array>", StringComparison.Ordinal);

        Assert.True(arrayStart >= 0 && exeIndex > arrayStart && argIndex > exeIndex && arrayEnd > argIndex,
            "可执行文件与参数必须按顺序落在同一个 ProgramArguments 数组里");
    }

    [Fact]
    public void LaunchAgent_EscapesXml()
    {
        string content = AutoStartContent.MacOSLaunchAgent("a&b<c>d\"e", "/opt/app", null);

        Assert.Contains("a&amp;b&lt;c&gt;d&quot;e", content, StringComparison.Ordinal);
        Assert.DoesNotContain("a&b<c>d\"e", content, StringComparison.Ordinal);
    }

    // ---- Windows：Run 键值 ----

    [Fact]
    public void WindowsRunValue_QuotesExecutable()
    {
        string value = AutoStartContent.WindowsRunValue(@"C:\Program Files\My App\myapp.exe", null);
        Assert.Equal("\"C:\\Program Files\\My App\\myapp.exe\"", value);
    }

    [Fact]
    public void WindowsRunValue_AppendsArgumentsWithoutTrailingSpace()
    {
        Assert.Equal(
            "\"C:\\app.exe\" --minimized",
            AutoStartContent.WindowsRunValue(@"C:\app.exe", ["--minimized"]));
        Assert.Equal(
            "\"C:\\app.exe\"",
            AutoStartContent.WindowsRunValue(@"C:\app.exe", []));
    }

    [Fact]
    public void WindowsRunValue_QuotesArgumentWithSpace()
    {
        string value = AutoStartContent.WindowsRunValue(@"C:\app.exe", ["--title=My App"]);
        Assert.Equal("\"C:\\app.exe\" \"--title=My App\"", value);
    }

    [Fact]
    public void WindowsRunValue_DoesNotQuotePlainArgument()
    {
        string value = AutoStartContent.WindowsRunValue(@"C:\app.exe", ["--a", "--b"]);
        Assert.Equal("\"C:\\app.exe\" --a --b", value);
    }
}
