using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 2026-10-25 评审（P1）钉住的窗口集合线程契约：<see cref="OrielApp.Windows"/> /
/// <c>GetWindow</c> / <c>GetFocusedWindow</c> / <c>SetTheme</c> / <c>SetAppMenuBar</c>。
/// </summary>
/// <remarks>
/// <para>
/// 背景：窗口集合 <c>_windows</c> 由 UI 线程维护，但这些成员过去直接暴露活列表 /
/// 在调用线程遍历——跨线程读会与 UI 线程的增删竞态（"Collection was modified"），
/// <c>SetTheme</c> 更是在调用线程直接做原生调用（GTK/AppKit 上是未定义行为）。
/// 修复后：集合一律给快照，原生遍历一律编组到 UI 线程。
/// </para>
/// <para>
/// 没有后端注入点（<c>IPlatformBackend</c> 约 100 个成员，见 <c>ExitSemanticsTests</c> 的
/// 同款说明），所以单测只能钉「未运行」状态下的行为：<b>Run() 之前调用不因编组而新抛异常</b>
/// （保持原先"只记账"的行为），集合读取给的是**可安全遍历的快照**。
/// 真正的多窗口并发遍历由 <c>--selftest window</c> 与三平台 conformance 验。
/// </para>
/// </remarks>
public sealed class AppWindowCollectionThreadingTests
{
    [Fact]
    public void Windows_ReturnsSafeSnapshot_NotTheLiveList()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        // 快照语义的可观察部分：返回值的运行时类型必须是数组（ToArray 的产物），
        // 而不是库内部的活 List——活 List 会把后续 UI 线程的增删直接暴露给遍历方。
        // （不能断言"两次调用不同实例"：空数组的重复分配会被 JIT 合并，那条是脆断言。）
        Assert.IsType<WebviewWindow[]>(app.Windows);
        Assert.Empty(app.Windows);
    }

    [Fact]
    public void GetWindow_ReturnsNullWhenNotRunning()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        Assert.Null(app.GetWindow("main"));
    }

    [Fact]
    public void GetFocusedWindow_ReturnsNullWhenNotRunning()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        Assert.Null(app.GetFocusedWindow());
    }

    [Fact]
    public void SetTheme_BeforeRun_OnlyRecords_DoesNotThrow()
    {
        // Run() 之前没有窗口可套用，SetTheme 只是记账——修复引入 UI 线程编组后，
        // 这条必须保持不抛（编组依赖的后端在未运行时是 null）。
        OrielApp app = Oriel.CreateBuilder().Build();

        app.SetTheme(OrielTheme.Dark);
        app.SetTheme(null);
    }

    [Fact]
    public void SetAppMenuBar_BeforeRun_OnlyRecords_DoesNotThrow()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        app.SetAppMenuBar([OrielMenuItem.Item("quit", "退出")]);
    }

    [Fact]
    public void SetAppMenuBar_RejectsNull()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => app.SetAppMenuBar(null!));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndClearsBackend()
    {
        // Run() 的退出路径会先 Dispose 一次，宿主的 using / 手动收尾会再来一次——
        // 两次都是合法写法。2026-10-25 评审 P3：此前无幂等标志也不置空 _backend，
        // Dispose 后的投递会拿到已释放后端的难定位异常。
        OrielApp app = Oriel.CreateBuilder().Build();

        app.Dispose();
        app.Dispose();

        // Dispose 之后 _backend 应已置空：后续投递拿到「应用尚未运行」的明确异常，
        // 而不是已释放后端的 GTK/WK 难定位异常。
        Assert.Throws<InvalidOperationException>(() => app.PostToMainThread(() => { }));
    }
}
