using OrielWeb.Platform.Linux;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 通知参数的位置化处理（<see cref="LinuxNotificationSender.AsPositional"/>）。
/// </summary>
/// <remarks>
/// 这段逻辑是纯字符串处理，因此能在任意平台上单测——而它要防的问题（标题/正文被 GLib 的
/// 选项解析器吃掉）只在 Linux 真机上才看得见，正是最该被钉住的那类。
/// </remarks>
public sealed class NotificationSenderTests
{
    [Fact]
    public void LeadingDash_IsNeutralized()
    {
        // "-u critical" 会被 g_option_context_parse 当成 --urgency 选项，
        // 位置参数随之错位（摘要变成正文、正文丢失）。前置一个空格即可让整串不再像选项。
        Assert.Equal(" -u critical", LinuxNotificationSender.AsPositional("-u critical"));
    }

    [Fact]
    public void DoubleDashPrefix_IsNeutralized()
    {
        Assert.Equal(" --urgency=critical", LinuxNotificationSender.AsPositional("--urgency=critical"));
    }

    [Fact]
    public void PlainText_IsUntouched()
    {
        Assert.Equal("下载完成", LinuxNotificationSender.AsPositional("下载完成"));
        Assert.Equal("50% 完成", LinuxNotificationSender.AsPositional("50% 完成"));
    }

    [Fact]
    public void DashInsideText_IsUntouched()
    {
        // 只有**首字符**是 '-' 才有被当选项的风险
        Assert.Equal("构建 - 失败", LinuxNotificationSender.AsPositional("构建 - 失败"));
    }

    [Fact]
    public void EmptyString_IsUntouched()
    {
        Assert.Equal(string.Empty, LinuxNotificationSender.AsPositional(string.Empty));
    }

    // ---- 通知的应用标识（Windows AUMID / Linux --app-name） ----

    [Fact]
    public void AppId_KeepsSafeCharacters()
    {
        Assert.Equal("OrielDemo", OrielNotificationAppId.Sanitize("OrielDemo"));
        Assert.Equal("my-app_v1.2+x", OrielNotificationAppId.Sanitize("my-app_v1.2+x"));
    }

    [Fact]
    public void AppId_ReplacesSpacesAndUnsafeCharacters()
    {
        // AUMID 不能含空格；路径分隔符、冒号、引号之类一并换成下划线
        Assert.Equal("My_App", OrielNotificationAppId.Sanitize("My App"));
        Assert.Equal("a_b_c", OrielNotificationAppId.Sanitize(@"a/b\c"));
        Assert.Equal("app_name_", OrielNotificationAppId.Sanitize("app:name*"));
    }

    [Fact]
    public void AppId_KeepsNonAsciiLetters()
    {
        // 程序集名可能是中文；char.IsLetterOrDigit 对非 ASCII 字母为真，因此保留
        Assert.Equal("我的应用", OrielNotificationAppId.Sanitize("我的应用"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppId_FallsBackWhenUnusable(string? raw)
    {
        Assert.Equal(OrielNotificationAppId.Fallback, OrielNotificationAppId.Sanitize(raw));
    }

    [Fact]
    public void AppId_TruncatesToAumidLimit()
    {
        // Windows 规定 AUMID 不超过 128 个字符
        string sanitized = OrielNotificationAppId.Sanitize(new string('a', 500));

        Assert.Equal(OrielNotificationAppId.MaxLength, sanitized.Length);
        Assert.Equal(new string('a', OrielNotificationAppId.MaxLength), sanitized);
    }

    [Fact]
    public void AppId_DefaultIsNonEmptyAndAlreadySafe()
    {
        // 默认值取入口程序集名（测试宿主下是 runner 的名字，不写死具体值）；
        // 这里断言的是"规范化后不再需要二次处理"——即再规范化一次结果不变。
        string appId = OrielApp.DefaultNotificationAppId();

        Assert.False(string.IsNullOrWhiteSpace(appId));
        Assert.Equal(appId, OrielNotificationAppId.Sanitize(appId));
    }
}
