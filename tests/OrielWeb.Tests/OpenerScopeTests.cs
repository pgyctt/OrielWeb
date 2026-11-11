using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>opener.*</c> 的路径 scope（2026-11-11 评审 H1）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前：<c>openPath</c> 只判"绝对路径 + 存在"，所以 <c>Allow("opener.openPath")</c> 一次
/// 就等于"能拉起机器上任意已存在的 <c>.exe/.bat/.hta</c>"。配合 fs 往 scope 内写一个 <c>.bat</c>
/// 再 openPath，是一条完整的 RCE 链。
/// </para>
/// <para>
/// 这里钉三件事：**scope 外拒绝**（含经符号链接绕出的情形）、**scope 内照常放行**（修的是绕过，
/// 不是把功能关掉）、**授权命令但没给 scope 时按"什么都不允许"处理**——与 <c>fs.*</c> 同一口径。
/// </para>
/// </remarks>
public sealed class OpenerScopeTests : IDisposable
{
    /// <summary>scope 覆盖的目录（模拟"应用的资源目录"）。</summary>
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "oriel-opener-" + Guid.NewGuid().ToString("N"));

    /// <summary>scope 之外的目录（与 <see cref="_root"/> 平级，保证不在 <c>_root/**</c> 之内）。</summary>
    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "oriel-opener-out-" + Guid.NewGuid().ToString("N"));

    public OpenerScopeTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        DeleteQuietly(_root);
        DeleteQuietly(_outside);
    }

    // ---- openPath ----

    [Fact]
    public void OpenPath_OutsideScope_IsRejected()
    {
        // 路径**不需要真的存在**：scope 判定先于存在性检查，越界就该在这里被拦下
        // （否则"存在与否"本身又成了一条探测通道）。
        using OrielApp app = CreateApp(c => c.Allow("opener.openPath", s => s.AllowPath(Scope())));
        string target = Path.Combine(_outside, "payload.bat");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "opener.openPath", new { path = target }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void OpenPath_ThroughFileLinkOutsideScope_IsRejected()
    {
        // 攻击面不是"页面直接写绝对路径"，而是"scope 目录里放一个链接"——
        // 只判字面路径的实现在这里会放行。
        string secret = Path.Combine(_outside, "secret.bat");
        File.WriteAllText(secret, "echo pwned");

        string link = Path.Combine(_root, "innocent.txt");
        CreateFileLink(link, secret);

        using OrielApp app = CreateApp(c => c.Allow("opener.openPath", s => s.AllowPath(Scope())));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "opener.openPath", new { path = link }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void OpenPath_InsideScope_ReachesTheHostCall()
    {
        // 修的是绕过，不是把命令关掉：scope 内的路径照常走到宿主那一层。
        // **刻意用一个不存在的文件**：它证明"闸门放行了"，而宿主的存在性检查随后返回 false，
        // 于是这条用例不会真的拉起外部程序（单测里不该弹窗口）。
        string file = Path.Combine(_root, "not-created.txt");

        using OrielApp app = CreateApp(c => c.Allow("opener.openPath", s => s.AllowPath(Scope())));

        Assert.False(InvokeWith<bool>(app, "opener.openPath", new { path = file }));
    }

    [Fact]
    public void OpenPath_LinkInsideScope_IsStillAllowed()
    {
        // 指向 scope 内的链接照常放行：解析的是"链接指向哪里"，不是"是不是链接"。
        // 链接指向目录、末段文件不存在——同样是为了不触发真实的外部程序启动。
        string real = Path.Combine(_root, "real-dir");
        Directory.CreateDirectory(real);

        string alias = Path.Combine(_root, "alias-dir");
        CreateDirectoryLink(alias, real);

        using OrielApp app = CreateApp(c => c.Allow("opener.openPath", s => s.AllowPath(Scope())));

        Assert.False(InvokeWith<bool>(app, "opener.openPath", new { path = Path.Combine(alias, "not-created.txt") }));
    }

    [Fact]
    public void OpenPath_AllowedWithoutScope_DeniesEverything()
    {
        // 授权了命令但没给 scope = 什么都不允许（与 fs.* 同一条口径，见 OrielPathScope）。
        using OrielApp app = CreateExact(c => c.Allow("opener.openPath"));
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => InvokeWith(app, "opener.openPath", new { path = file }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
        Assert.Contains("scope", ex.Message, StringComparison.Ordinal);
    }

    // ---- revealItemInDir ----

    [Fact]
    public void Reveal_OutsideScope_IsRejected()
    {
        using OrielApp app = CreateApp(
            c => c.Allow("opener.revealItemInDir", s => s.AllowPath(Scope())));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => InvokeWith(
            app, "opener.revealItemInDir", new { paths = new[] { Path.Combine(_outside, "x.txt") } }));

        Assert.Equal(OrielIpcErrorCodes.PathNotAllowed, ex.Code);
    }

    [Fact]
    public void Reveal_InsideScopeParallelsOpenPath()
    {
        // 同 openPath：用一个不存在的路径证明闸门放行，而不真的拉起 explorer。
        string file = Path.Combine(_root, "not-created.txt");

        using OrielApp app = CreateApp(
            c => c.Allow("opener.revealItemInDir", s => s.AllowPath(Scope())));

        Assert.False(InvokeWith<bool>(app, "opener.revealItemInDir", new { paths = new[] { file } }));
    }

    // ---- 未配置能力时的姿态（门禁层，显式指定构建类型以免依赖测试的运行配置）----

    [Fact]
    public void UnconfiguredGuard_AllowsOpenerPathsInDebugAndDeniesInRelease()
    {
        Assert.True(TestGuards.Unconfigured(isDebugBuild: true)
            .TryAuthorizePath("opener.openPath", Path.Combine(_root, "a.txt"), "app.oriel", out _));

        Assert.False(TestGuards.Unconfigured(isDebugBuild: false)
            .TryAuthorizePath("opener.openPath", Path.Combine(_root, "a.txt"), "app.oriel", out string? reason));

        Assert.Contains("Release", reason, StringComparison.Ordinal);
    }

    // ---- 夹具 ----

    private string Scope() => _root.Replace('\\', '/') + "/**";

    private static OrielApp CreateApp(Action<OrielCapabilityOptions> configure)
        => Oriel.CreateBuilder().UseCapabilities(configure).Build();

    private static OrielApp CreateExact(Action<OrielCapabilityOptions> configure)
        => Oriel.CreateBuilder().UseCapabilities(configure).Build();

    private static void InvokeWith(OrielApp app, string command, object args)
        => InvokeWith<object?>(app, command, args);

    private static T InvokeWith<T>(OrielApp app, string command, object args)
    {
        Assert.True(OrielBuiltInPlatformCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        return (T)handler(window, command, doc.RootElement)!;
    }

    private static void CreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail(
                "无法创建符号链接：" + ex.Message +
                "。Windows 需要 SeCreateSymbolicLinkPrivilege（以管理员运行，或开启开发者模式）。" +
                "这条用例不能用\"跳过\"代替——链接绕过是 scope 的真实攻击面。");
        }
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail(
                "无法创建目录符号链接：" + ex.Message +
                "（Windows 需要管理员或开发者模式）。");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言；临时目录留给系统回收
        }
    }
}
