using OrielWeb;

namespace TauriMultiWindow;

/// <summary>
/// 入口。
/// </summary>
/// <remarks>
/// <para>
/// 原文有两处建窗：<c>tauri.conf.json</c> 里声明主窗口，<c>setup</c> 里再建一个 "Third"。
/// 本库的 <c>AddWindow</c> 就是前者；后者在这里没有对应物，也不需要——
/// 页面侧 <c>new WebviewWindow(label, options)</c> 就能建窗（原文页面本来也这么写）。
/// </para>
/// <para>
/// 能力配置是这组示例里唯一"必须多写一行"的地方：<c>window.new</c> 与 <c>event.emit</c>
/// 都是库内建命令，但<b>不在始终放行的名单里</b>（前者能建窗口，后者能驱动别的窗口）。
/// Debug 构建不写也放行，Release 构建不写就一律拒绝——这正是 fail-closed 的意思。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            .WithAppInfo(name: "Tauri 示例 · MultiWindow", identifier: "com.orielweb.samples.tauri.multiwindow")
            // 回执与事件载荷都是字符串，不需要 UseJsonContext。
            .UseCapabilities(c => c
                // 页面侧 new WebviewWindow(...) / createWebviewWindow(...)
                .Allow("window.new")
                // 页面侧 emitTo(label, 'message', payload)
                .Allow("event.emit"))
            .AddWindow(w => w
                // 默认 label 就是 "main"：与原文 tauri.conf.json 的第一个窗口一致
                .WithTitle("Tauri 示例 · MultiWindow")
                .WithSize(900, 700)
                .WithMinSize(640, 520)
                .Centered())
            .Run();
    }
}
