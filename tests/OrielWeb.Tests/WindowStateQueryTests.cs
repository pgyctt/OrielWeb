using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 窗口只读状态查询（M5-1）：<c>isMinimized</c> / <c>isFocused</c> /
/// <c>isFullscreen</c> / <c>isAlwaysOnTop</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试在**没有真窗口**的情况下跑（<c>FakeWindow</c> 的 <c>App</c> 没有后端），
/// 所以能验的是三件事：命令注册、**门禁姿态**（在标题栏白名单里）、以及返回类型。
/// </para>
/// <para>
/// 真正的状态取值在 <c>--selftest window</c> 里验（那才有真窗口）。
/// </para>
/// </remarks>
public sealed class WindowStateQueryTests
{
    private static readonly string[] NewQueries =
    [
        "window.isMinimized", "window.isFocused", "window.isFullscreen", "window.isAlwaysOnTop",
    ];

    [Theory]
    [InlineData("window.isMinimized")]
    [InlineData("window.isFocused")]
    [InlineData("window.isFullscreen")]
    [InlineData("window.isAlwaysOnTop")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInWindowApiCommands.TryGet(name, out _), $"未注册：{name}");

    [Theory]
    [InlineData("window.isMinimized")]
    [InlineData("window.isFocused")]
    [InlineData("window.isFullscreen")]
    [InlineData("window.isAlwaysOnTop")]
    public void QueriesAreInTheTitleBarWhitelist(string name)
    {
        // **门禁姿态**：这四条与 isVisible / isMaximized 一样在标题栏白名单里——
        // 自绘标题栏要靠它们渲染按钮的当前状态与"窗口是否激活"的配色，
        // 而它们只报告自己窗口的状态、不泄露跨窗口信息。
        //
        // 若哪天有人把它们从白名单里拿掉，自绘标题栏会突然要不到状态，
        // 而那表现为"按钮图标不跟着变"——很难联想到是门禁。
        Assert.True(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    [Theory]
    [InlineData("window.isMinimized")]
    [InlineData("window.isFocused")]
    [InlineData("window.isFullscreen")]
    [InlineData("window.isAlwaysOnTop")]
    public void QueriesDoNotNeedCapabilityDeclaration(string name)
    {
        // 没配能力时（_options 为 null）也要放行——与 win.* 那族同样的待遇
        var guard = new OrielIpcGuard(null, isDebugBuild: false, "app.oriel");

        Assert.True(guard.TryAuthorize(name, out string? reason), $"{name} 应当无条件放行（{reason}）");
    }

    [Fact]
    public void QueriesCannotBeExercisedWithoutARealWindow()
    {
        // **这里刻意断言"取不到值"**，而不是绕过去。
        //
        // `window.*` 的解析要走 `App.GetWindow(label)` 查表——它顺带保证目标是个
        // **已注册的存活窗口**（见 OrielBuiltInWindowApiCommands.Resolve 的说明）。
        // 假窗口没注册在应用里，所以这条必然失败：这不是夹具的缺陷，
        // 而是"这四个查询的参数是真实窗口状态"的直接体现。
        //
        // 真正的取值在 `--selftest window` 里验（那有真窗口）。单测守的是
        // 注册、门禁姿态与授权——那三件在没有窗口时也成立。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow
        {
            App = Oriel.CreateBuilder().Build(),
        };

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<object?>(window, "window.isMinimized"));

        Assert.Contains("找不到 label", ex.Message, StringComparison.Ordinal);
    }

    private static T Invoke<T>(BuiltInWindowApiCommandTests.RecordingWindow window, string command)
    {
        Assert.True(OrielBuiltInWindowApiCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse("""{ }""");
        return (T)handler(window, doc.RootElement)!;
    }
}
