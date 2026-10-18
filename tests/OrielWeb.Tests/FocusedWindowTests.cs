using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// <c>GetFocusedWindow</c> / <c>window.getFocused</c>（对标 Tauri 2 的
/// <c>Window.getFocusedWindow()</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这条曾经是"有签名无能力"：三平台都没有"返回焦点窗口"的单次调用，所以它恒返回 <c>null</c>。
/// 但 <c>isFocused</c> 在 M5-1 落地之后，**"遍历存活窗口、取第一个有焦点的"** 就成立了
/// （Tauri 的 JS 侧正是这么做的，见 <c>@tauri-apps/api/window.js</c> 的
/// <c>static async getFocusedWindow()</c>）。本组钉住这个实现。
/// </para>
/// <para>
/// 没有真窗口时能验的是：命令注册、**不在标题栏白名单里**（它是跨窗口信息）、
/// 以及"没有任何窗口时返回 null 而不是抛"。真正的焦点判定在 <c>--selftest window</c> 里验。
/// </para>
/// </remarks>
public sealed class FocusedWindowTests
{
    [Fact]
    public void CommandIsRegistered()
        => Assert.True(OrielBuiltInWindowApiCommands.TryGet("window.getFocused", out _));

    [Fact]
    public void CommandIsNotInTheTitleBarWhitelist()
    {
        // **门禁姿态**：与 getAll 同类——它答的是"**别的**窗口是不是在前台"，
        // 属于跨窗口信息，不是"我这个窗口的状态"。所以不进白名单。
        Assert.False(
            OrielCapabilityRules.IsAlwaysAllowedWindowCommand("window.getFocused"),
            "window.getFocused 不该在始终放行的白名单里——它泄露的是跨窗口信息。");
    }

    [Fact]
    public void CommandIsDeniedByDefault()
        => Assert.False(OrielCapabilityRules.IsAllowed("window.getFocused", [], []));

    [Fact]
    public void CommandIsAllowedWhenListed()
    {
        Assert.True(OrielCapabilityRules.IsAllowed("window.getFocused", ["window.getFocused"], []));
        Assert.True(OrielCapabilityRules.IsAllowed("window.getFocused", ["window.*"], []));
    }

    [Fact]
    public void NoWindowsMeansNoFocusedWindow()
    {
        // 没有任何窗口时必须是 null（而不是抛、也不是一个空句柄）。
        // 这条在没有真实后端的环境里就能验：窗口集合为空，循环不执行。
        OrielApp app = Oriel.CreateBuilder().Build();

        Assert.Null(app.GetFocusedWindow());
    }

    [Fact]
    public void CommandReturnsNullWhenNoWindowIsFocused()
    {
        // 端到端：命令层把 null 原样传出去（页面据此拿到 null 而不是一个空字符串）
        var window = new BuiltInWindowApiCommandTests.RecordingWindow
        {
            App = Oriel.CreateBuilder().Build(),
        };

        Assert.True(OrielBuiltInWindowApiCommands.TryGet("window.getFocused", out var handler));

        using var doc = JsonDocument.Parse("""{ }""");
        Assert.Null(handler(window, doc.RootElement));
    }
}
