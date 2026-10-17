using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 剪贴板命令族（F6）：<c>clipboard.readText</c> / <c>writeText</c> / <c>clear</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试用**没有后端的应用**，所以三个命令都会走到"没后端"的那条分支：
/// <c>readText</c> 给 <c>null</c>、另两个给 <c>false</c>。
/// 这不是"测了个寂寞"——它钉的正是那个约定：**没后端不能抛异常**。
/// 抛的话页面拿到的是"命令执行失败"（internal 码），而调用方要判断的其实是
/// "有没有文本"，对那个判断来说"宿主没起来"与"剪贴板里没有文本"是同一个结论。
/// </para>
/// <para>
/// 真实的读写在 <c>--selftest api</c> 里验（真剪贴板），瞎子单测做不到——
/// 单测进程没有 UI 线程，而 Windows 的剪贴板 API 可以在无窗口线程上用，
/// 但那样测的是"这台机器现在的剪贴板"（会被开发者的剪贴板内容左右），
/// 不是一个稳定的断言对象。
/// </para>
/// </remarks>
public sealed class ClipboardCommandTests
{
    [Theory]
    [InlineData("clipboard.readText")]
    [InlineData("clipboard.writeText")]
    [InlineData("clipboard.clear")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInPlatformCommands.TryGet(name, out _), $"未注册：{name}");

    [Fact]
    public void UnknownClipboardCommandIsNotHandled()
        => Assert.False(OrielBuiltInPlatformCommands.TryGet("clipboard.readHtml", out _));

    [Theory]
    [InlineData("clipboard.writeText")]
    [InlineData("clipboard.clear")]
    public void ClipboardCommandsAreNotAlwaysAllowed(string name)
    {
        // 写剪贴板是**能力**（能覆盖用户已经复制的内容），读也一样
        // （剪贴板里可能是密码管理器刚放进去的东西）。所以要用必须声明。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(name), name);
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    [Fact]
    public void ReadTextReturnsNullWithoutABackendInsteadOfThrowing()
    {
        // 没后端 → null（"没有文本"），而不是抛
        Assert.Null(Invoke<string?>("clipboard.readText", new { }));
    }

    [Fact]
    public void WriteTextReturnsFalseWithoutABackendInsteadOfThrowing()
    {
        // 失败返回 false：剪贴板被别的进程占着是正常状态，不是调用方的错误
        Assert.False(Invoke<bool>("clipboard.writeText", new { text = "内容" }));
    }

    [Fact]
    public void WriteTextAcceptsAnEmptyString()
    {
        // 空串是合法内容（"清空成空文本"）。这里只断言它不被当成缺参数。
        // 真去写空串会把用户的剪贴板清掉，所以单测里走的是没后端那条路。
        Assert.False(Invoke<bool>("clipboard.writeText", new { text = "" }));
    }

    [Fact]
    public void WriteTextRequiresTheTextArgument()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<bool>("clipboard.writeText", new { }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Fact]
    public void ClearReturnsFalseWithoutABackend()
        => Assert.False(Invoke<bool>("clipboard.clear", new { }));

    // ---- label：明确拒绝，不静默忽略 ----

    [Fact]
    public void WriteTextRejectsLabel()
    {
        // Tauri 用 label 指定 X11 的哪个剪贴板（CLIPBOARD / PRIMARY / SECONDARY）。
        // 静默忽略会让调用方以为写进了 PRIMARY（中键粘贴那个），实际写进了 CLIPBOARD——
        // 一次静默的目标错位，而调用方不会发现。
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<bool>("clipboard.writeText", new { text = "x", label = "PRIMARY" }));

        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
        Assert.Contains("label", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTextTreatsNullLabelAsAbsent()
    {
        // 参数从别处透传、值为 null 时等于"没给"（与 opener 的 openWith 同一口径）
        Assert.False(Invoke<bool>("clipboard.writeText", new { text = "x", label = (string?)null }));
    }

    // ---- 夹具 ----

    private static T Invoke<T>(string command, object args)
    {
        Assert.True(OrielBuiltInPlatformCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));

        // 应用**没有后端**：读/写剪贴板会走到"没后端"那条分支，不碰真实剪贴板
        var window = new BuiltInWindowCommandTests.FakeWindow
        {
            App = Oriel.CreateBuilder().Build(),
            Label = "main",
        };

        return (T)handler(window, command, doc.RootElement)!;
    }
}
