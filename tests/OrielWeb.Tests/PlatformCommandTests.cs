using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 平台集成命令族（F5）：<c>opener.*</c> / <c>notification.*</c> / <c>autostart.*</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试的重点是**参数校验与"不静默降级"**，而不是"真的把浏览器拉起来了没有"。
/// 后者做不到（也不该在单测里做——它会真的弹窗口），所以这里用的应用**没有后端**，
/// 那几个 <c>App.*</c> 方法会返回 <c>false</c> 而不产生副作用。
/// </para>
/// <para>
/// 最值得钉的是 <c>openWith</c>：Tauri 支持它，而本库不支持。
/// **静默忽略**会让调用方以为"我指定了用某个程序打开"，实际是按系统默认打开——
/// 一次静默的行为降级，而调用方不会发现。
/// </para>
/// </remarks>
public sealed class PlatformCommandTests
{
    // ---- 注册与放行姿态 ----

    [Theory]
    [InlineData("opener.openUrl")]
    [InlineData("opener.openPath")]
    [InlineData("opener.revealItemInDir")]
    [InlineData("notification.isPermissionGranted")]
    [InlineData("notification.requestPermission")]
    [InlineData("notification.sendNotification")]
    [InlineData("notification.active")]
    [InlineData("notification.pending")]
    [InlineData("notification.cancel")]
    [InlineData("notification.cancelAll")]
    [InlineData("notification.removeActive")]
    [InlineData("notification.removeAllActive")]
    [InlineData("notification.actionSupport")]
    [InlineData("clipboard.readText")]
    [InlineData("clipboard.writeText")]
    [InlineData("clipboard.clear")]
    [InlineData("clipboard.readImage")]
    [InlineData("clipboard.writeImage")]
    [InlineData("clipboard.writeHtml")]
    [InlineData("clipboard.writeSecret")]
    [InlineData("autostart.isEnabled")]
    [InlineData("autostart.enable")]
    [InlineData("autostart.disable")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInPlatformCommands.TryGet(name, out _), $"未注册：{name}");

    [Fact]
    public void UnknownCommandIsNotHandled()
        => Assert.False(OrielBuiltInPlatformCommands.TryGet("opener.nonexistent", out _));

    [Theory]
    [InlineData("opener.openUrl")]
    [InlineData("opener.openPath")]
    [InlineData("opener.revealItemInDir")]
    [InlineData("notification.sendNotification")]
    [InlineData("notification.active")]
    [InlineData("notification.pending")]
    [InlineData("notification.cancel")]
    [InlineData("notification.cancelAll")]
    [InlineData("notification.removeActive")]
    [InlineData("notification.removeAllActive")]
    [InlineData("clipboard.readImage")]
    [InlineData("clipboard.writeImage")]
    [InlineData("clipboard.writeHtml")]
    [InlineData("clipboard.writeSecret")]
    [InlineData("autostart.enable")]
    [InlineData("autostart.disable")]
    public void PlatformCommandsAreNotAlwaysAllowed(string name)
    {
        // **三族都不整族放行**：它们改系统状态、能拉起外部程序，
        // 不是"只操作自己那个窗口"那一类。若哪天有人把它们加进某个放行名单，这条会红。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    // ---- openWith：明确拒绝，不静默降级 ----

    [Fact]
    public void OpenUrlRejectsOpenWith()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("opener.openUrl", new { url = "https://example.com", with = "firefox" }));

        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
        Assert.Contains("openWith", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenPathRejectsOpenWith()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("opener.openPath", new { path = "/tmp/a.txt", with = "notepad" }));

        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
    }

    [Fact]
    public void OpenWithNullIsTreatedAsAbsent()
    {
        // 页面侧的可能写法：参数从别处透传，值为 null。那等于"没给"，不该报错。
        // （Tauri 的签名里 openWith 是可选的，透传 null 很常见。）
        Invoke("opener.openUrl", new { url = "https://example.com", with = (string?)null });
    }

    // ---- openUrl 的参数校验 ----

    [Theory]
    [InlineData("not a url")]
    [InlineData("/tmp/a.txt")]
    [InlineData("example.com")]
    public void OpenUrlRequiresAnAbsoluteUrl(string url)
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("opener.openUrl", new { url }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("mailto:a@b.c")]
    [InlineData("tel:+1234")]
    [InlineData("myapp://open?id=1")]
    public void OpenUrlAcceptsAnyAbsoluteScheme(string url)
    {
        // **不限制协议**：mailto/tel/自定义协议处理器都是"打开外部东西"的正常用法，
        // 而这条命令的语义就是"打开你给的这个 URL"。应用要在意的话自己判。
        Invoke("opener.openUrl", new { url });
    }

    [Fact]
    public void OpenUrlMissingArgumentIsMissingArgument()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("opener.openUrl", new { }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    // ---- revealItemInDir ----

    [Fact]
    public void RevealAcceptsStringOrArray()
    {
        Invoke("opener.revealItemInDir", new { paths = new[] { "/tmp/a.txt" } });
        Invoke("opener.revealItemInDir", new { paths = new[] { "/tmp/a.txt", "/tmp/b.txt" } });
    }

    [Fact]
    public void RevealRejectsEmptyArray()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("opener.revealItemInDir", new { paths = Array.Empty<string>() }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void RevealRejectsNonStringElement()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("opener.revealItemInDir", new { paths = new object[] { "/tmp/a.txt", 42 } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void RevealRejectsNonArray()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("opener.revealItemInDir", new { paths = "/tmp/a.txt" }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    // ---- notification ----

    [Fact]
    public void PermissionGrantedReflectsHostSupport()
    {
        // 桌面上没有"通知权限"这个概念，所以这条回的是"宿主是否支持通知"。
        // 测试里的应用没有后端 → false。
        Assert.False(Invoke<bool>("notification.isPermissionGranted", new { }));
    }

    [Fact]
    public void RequestPermissionReturnsTheSpecString()
    {
        // 返回字符串（`granted` / `denied`）而不是布尔：与 Tauri 以及
        // Web Notification 规范一致，迁移代码里的 `=== 'granted'` 判断不用改。
        string result = Invoke<string>("notification.requestPermission", new { });

        Assert.Contains(result, new[] { "granted", "denied" });
    }

    [Fact]
    public void SendNotificationRequiresATitle()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("notification.sendNotification", new { body = "没有标题" }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void SendNotificationReturnsFalseWithoutABackendInsteadOfThrowing()
    {
        // 返回 false 而不是抛错，用于"用户关掉了这个应用的通知"这类**正常状态**。
        // 测试里的应用没有后端 → false（而不是异常）。
        Assert.False(Invoke<bool>("notification.sendNotification", new { title = "标题", body = "正文" }));
    }

    [Fact]
    public void SendNotificationBodyIsOptional()
    {
        Assert.False(Invoke<bool>("notification.sendNotification", new { title = "只有标题" }));
    }

    [Fact]
    public void SendNotificationIgnoresNonStringBody()
    {
        // 页面传了 body: 42 时当"没给"，而不是报类型错——
        // 一条通知的正文类型不对不该让整条通知发不出去。
        Assert.False(Invoke<bool>("notification.sendNotification", new { title = "t", body = 42 }));
    }

    // ---- notification 的登记表（B18）----

    [Fact]
    public void ActiveAndPendingAreEmptyWithoutABackend()
    {
        // 登记表挂在 OrielApp 上（不是后端），所以"宿主还没起来"时它们是空列表而不是抛错——
        // 页面在启动早期问一次不该炸。
        Assert.Equal(0, Invoke<JsonElement>("notification.active", new { }).GetArrayLength());
        Assert.Equal(0, Invoke<JsonElement>("notification.pending", new { }).GetArrayLength());
    }

    [Fact]
    public void ActionSupportIsFalseOnEveryDesktopPlatform()
    {
        // 三平台都拿不到通知点击（见 OrielApp 里同处的说明）；页面侧 onAction 据此抛 not_supported。
        Assert.False(Invoke<bool>("notification.actionSupport", new { }));
    }

    [Fact]
    public void SendNotificationWithPastScheduleIsInvalidArgument()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("notification.sendNotification", new
        {
            title = "t",
            schedule = new { at = new { date = DateTimeOffset.Now.AddMinutes(-1).ToString("O") } },
        }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("将来", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendNotificationRejectsScheduleWithTwoTriggers()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("notification.sendNotification", new
        {
            title = "t",
            schedule = new
            {
                at = new { date = DateTimeOffset.Now.AddHours(1).ToString("O") },
                every = new { interval = "hour", count = 1 },
            },
        }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("三者之一", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendNotificationWithScheduleReturnsFalseWithoutABackend()
    {
        // 没有后端 → 排期不会真的发生（返回 false，与立即投递同一姿态）
        Assert.False(Invoke<bool>("notification.sendNotification", new
        {
            title = "t",
            schedule = new { at = new { date = DateTimeOffset.Now.AddHours(1).ToString("O") } },
        }));
    }

    [Fact]
    public void SendNotificationAcceptsUnixMillisecondsForScheduleDate()
    {
        // 页面侧传 Date 时桥接层会给 ISO 字符串；但裸数字（Unix 毫秒）也收——
        // 少一条"为什么我的日期没生效"的排查路径。
        Assert.False(Invoke<bool>("notification.sendNotification", new
        {
            title = "t",
            schedule = new { at = new { date = DateTimeOffset.Now.AddHours(1).ToUnixTimeMilliseconds() } },
        }));
    }

    [Fact]
    public void CancelAcceptsNumbersAndIdObjects()
    {
        // Tauri 的 cancel 收 number[]、removeActive 收 { id }[]；两种元素都收，避免迁移时踩空。
        Assert.Equal(0, Invoke<int>("notification.cancel", new
        {
            notifications = new object[] { 1, new { id = 2 } },
        }));
    }

    [Fact]
    public void CancelRejectsNonIntegerIds()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Invoke("notification.cancel", new
        {
            notifications = new object[] { "1" },
        }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("必须是整数", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelRequiresTheNotificationsArray()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("notification.cancel", new { ids = new object[] { 1 } }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void RemoveAllActiveIsZeroWithoutABackend()
        => Assert.Equal(0, Invoke<int>("notification.removeAllActive", new { }));

    // ---- clipboard：图片 / HTML / 敏感文本（B19）----

    [Fact]
    public void ReadImageWithoutABackendIsNull()
        => Assert.Null(Invoke<object?>("clipboard.readImage", new { }));

    [Fact]
    public void WriteImageRejectsUnknownShape()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("clipboard.writeImage", new { image = new { rgba = "AAAA" } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("{ path }", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteImageRejectsNonBase64Bytes()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("clipboard.writeImage", new { image = new { bytes = "这不是 base64!" } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("base64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteImageRejectsBytesThatAreNotAnImage()
    {
        string notAnImage = Convert.ToBase64String("hello"u8.ToArray());

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("clipboard.writeImage", new { image = new { bytes = notAnImage } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void WriteImageAcceptsValidPngButFailsWithoutABackend()
    {
        // PNG 直接透传给后端（不解码再编码）；没有后端时返回 false
        byte[] png = OrielPngEncoder.Encode([255, 0, 0, 255], 1, 1);
        string base64 = Convert.ToBase64String(png);

        Assert.False(Invoke<bool>("clipboard.writeImage", new { image = new { bytes = base64 } }));
    }

    [Fact]
    public void WriteImageRejectsMissingPath()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("clipboard.writeImage", new { image = new { path = "不存在/这张图.png" } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("读不到", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteHtmlRequiresHtml()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke("clipboard.writeHtml", new { altText = "只有回退文本" }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void WriteHtmlWithoutABackendReturnsFalse()
        => Assert.False(Invoke<bool>("clipboard.writeHtml", new { html = "<b>x</b>", altText = "x" }));

    [Fact]
    public void WriteSecretWithoutABackendReturnsFalse()
        => Assert.False(Invoke<bool>("clipboard.writeSecret", new { text = "hunter2" }));

    // ---- autostart ----

    [Fact]
    public void AutoStartReadIsSafeWithoutABackend()
    {
        // 读是安全的：没有后端时 false，不抛
        Assert.False(Invoke<bool>("autostart.isEnabled", new { }));
    }

    // ---- 夹具 ----

    private static OrielApp CreateApp() => Oriel.CreateBuilder().Build();

    private static T Invoke<T>(string command, object args)
    {
        Assert.True(OrielBuiltInPlatformCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        // 应用**没有后端**：App.OpenExternal 之类会返回 false 而不产生副作用——
        // 单测里不该真的把浏览器拉起来。
        var window = new BuiltInWindowCommandTests.FakeWindow { App = CreateApp(), Label = "main" };
        return (T)handler(window, command, doc.RootElement)!;
    }

    private static void Invoke(string command, object args) => Invoke<object?>(command, args);
}
