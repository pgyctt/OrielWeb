using OrielWeb;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 托盘事件模型（对标 Tauri 2 的 <c>TrayIconEvent</c>）。
/// </summary>
/// <remarks>
/// 这里钉住的是**数据形状**：事件类型、按键、状态、坐标，以及"平台未提供的字段是 null
/// 而不是编造一个值"。真实的原生事件送达只能在各平台人眼/自检验证（见 demo 的 --selftest tray）。
/// </remarks>
public sealed class TrayEventTests
{
    [Fact]
    public void Event_CarriesAllFields_WhenPlatformProvidesThem()
    {
        var e = new OrielTrayEvent(
            "main",
            OrielTrayEventType.Click,
            OrielTrayMouseButton.Right,
            OrielTrayButtonState.Up,
            new OrielTrayPoint(120, 1080));

        Assert.Equal("main", e.TrayId);
        Assert.Equal(OrielTrayEventType.Click, e.Type);
        Assert.Equal(OrielTrayMouseButton.Right, e.Button);
        Assert.Equal(OrielTrayButtonState.Up, e.ButtonState);
        Assert.Equal(new OrielTrayPoint(120, 1080), e.Position);
    }

    [Fact]
    public void Event_UnprovidedFields_AreNull_NotFabricated()
    {
        // Linux 只发 activate（无按键无坐标）：这些字段必须是 null，
        // 让页面能区分"平台不支持"与"值是 0"
        var e = new OrielTrayEvent("t", OrielTrayEventType.Click);

        Assert.Null(e.Button);
        Assert.Null(e.ButtonState);
        Assert.Null(e.Position);
        Assert.Null(e.Rect);
    }

    [Fact]
    public void Event_Rect_IsOptional()
    {
        var e = new OrielTrayEvent(
            "t", OrielTrayEventType.Click,
            rect: new OrielTrayRect(new OrielTrayPoint(1, 2), 16, 16));

        Assert.NotNull(e.Rect);
        Assert.Equal(16, e.Rect!.Value.Width);
        Assert.Equal(1, e.Rect.Value.Position.X);
    }

    [Theory]
    [InlineData(OrielTrayEventType.Click)]
    [InlineData(OrielTrayEventType.DoubleClick)]
    [InlineData(OrielTrayEventType.Enter)]
    [InlineData(OrielTrayEventType.Move)]
    [InlineData(OrielTrayEventType.Leave)]
    public void Event_AllTauriTypesExist(OrielTrayEventType type)
    {
        // 与 Tauri 2 的 TrayIconEventType 一一对应（Click/DoubleClick/Enter/Move/Leave）
        var e = new OrielTrayEvent("t", type);
        Assert.Equal(type, e.Type);
    }

    [Fact]
    public void Event_ToString_IsReadableForDiagnostics()
    {
        var e = new OrielTrayEvent(
            "t", OrielTrayEventType.Click, OrielTrayMouseButton.Left,
            OrielTrayButtonState.Down, new OrielTrayPoint(3, 4));

        string text = e.ToString();
        Assert.Contains("Click", text, StringComparison.Ordinal);
        Assert.Contains("Left", text, StringComparison.Ordinal);
        Assert.Contains("Down", text, StringComparison.Ordinal);
        Assert.Contains("(3,4)", text, StringComparison.Ordinal);
    }
}
