using OrielWeb;

namespace TauriDrag;

/// <summary>
/// 入口。这个示例<b>没有一条应用命令</b>，也就不需要能力配置。
/// </summary>
/// <remarks>
/// <para>
/// 拖动走的是库内建的 <c>win.drag</c> / <c>win.dragStart</c> / <c>win.dragTo</c> /
/// <c>win.dragEnd</c>，关闭按钮走 <c>win.close</c>——<c>win.</c> 前缀始终放行，
/// 不受 <c>UseCapabilities</c> 约束（理由见 docs/API.md §4：拒掉它们会让无边框窗口关不掉）。
/// </para>
/// <para>
/// 与 Tauri 的差别：Tauri 在页面脚本里监听 <c>data-tauri-drag-region</c> 并调
/// <c>plugin:window|start_dragging</c>；本库把这段逻辑放进了桥接脚本，因为三平台的
/// 拖动形态差别很大（Windows 的原生拖动会吞掉后续点击、Wayland 要先等指针过阈值）。
/// 结果是页面侧只剩一个属性，且<b>不需要任何权限配置</b>。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            .WithAppInfo(name: "Tauri 示例 · Drag", identifier: "com.orielweb.samples.tauri.drag")
            .AddWindow(w => w
                .WithTitle("Tauri 示例 · Drag")
                .WithSize(900, 760)
                .WithMinSize(620, 540)
                // 无边框：没有系统标题栏，拖动完全由页面标注的区域决定
                .WithFrameless()
                .Centered())
            .Run();
    }
}
