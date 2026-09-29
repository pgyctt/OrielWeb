using System.Diagnostics;
using System.Text.Json.Serialization;
using OrielWeb;
using OrielDemo;
using OrielWeb.Ipc;

namespace OrielDemo;

internal static class Program
{
    internal static WebviewWindow? Window;

    [STAThread]
    private static void Main(string[] args)
    {
        // --hidden：以隐藏窗口启动。既用于验证三平台的 Hidden 语义（窗口不上屏，但页面照常
        // 加载、IPC 照常往返），也是"先隐藏预热、准备好再显示"这类用法的示例。
        var hidden = args.Contains("--hidden");

        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            .UseJsonContext(AppJsonContext.Default)
            .AddCommands<TodoCommands>()
            .AddCommands<WindowCommands>()
            .UseDebug()
            .OnWebView2RuntimeMissing(HandleWebView2RuntimeMissing)
            .AddWindow(w => (hidden ? w.WithHidden() : w)
                .WithTitle("Oriel Demo — Todo")
                .WithSize(1024, 720)
                .WithMinSize(640, 480)
                .WithFrameless()
                .Centered(), win => Window = win)
            .Run();
    }

    /// <summary>
    /// WebView2 运行时缺失时的引导：讲清缺什么、打开官方下载页，随后应用退出（装完再启动）。
    /// 不注册此回调时，库只弹一个含下载地址的错误框。
    /// </summary>
    private static void HandleWebView2RuntimeMissing(OrielWebView2RuntimeMissingEventArgs e)
    {
        e.Window.ShowMessage(
            "未检测到 Microsoft Edge WebView2 运行时。本应用的界面完全由网页渲染，缺少它无法显示。\r\n\r\n" +
            "点「确定」后将打开微软的下载页面；安装完成后请重新启动本应用。\r\n\r\n" +
            OrielWebView2RuntimeMissingEventArgs.DownloadUrl,
            "缺少 WebView2 运行时",
            OrielMessageBoxIcon.Warning);

        // 用户已确认：打开下载页。本窗口随后由库销毁，进程随之退出。
        Process.Start(new ProcessStartInfo
        {
            FileName = OrielWebView2RuntimeMissingEventArgs.DownloadUrl,
            UseShellExecute = true,
        });
    }
}

/// <summary>无边框窗口控制命令：由页面自绘标题栏调用（按平台分支拖动模式）。</summary>
public sealed partial class WindowCommands
{
    [OrielCommand("win.minimize")]
    public static void Minimize() => Program.Window?.Minimize();

    /// <summary>最大化/还原切换，返回切换后是否最大化（页面据此更新按钮图标）。</summary>
    [OrielCommand("win.toggleMaximize")]
    public static bool ToggleMaximize() => Program.Window?.ToggleMaximize() ?? false;

    [OrielCommand("win.close")]
    public static void Close() => Program.Window?.Close();

    /// <summary>Windows：进入原生模态拖动（参数忽略）；macOS：无操作（用 dragStart/dragTo）。</summary>
    [OrielCommand("win.drag")]
    public static void Drag() => Program.Window?.BeginDrag();

    /// <summary>macOS 流式拖动起点。px/py = 指针屏幕坐标（CSS 点）；wx/wy/ww/wh = 窗口几何；sh = 屏高。</summary>
    [OrielCommand("win.dragStart")]
    public static void DragStart(double px, double py, double wx, double wy, double ww, double wh, double sh)
        => Program.Window?.BeginDragStreaming(px, py, wx, wy, ww, wh, sh);

    /// <summary>macOS 流式拖动增量（CSS 点，y 向下为正）；Windows 上为 no-op。</summary>
    [OrielCommand("win.dragTo")]
    public static void DragTo(double dx, double dy) => Program.Window?.DragTo(dx, dy);

    [OrielCommand("win.dragEnd")]
    public static void DragEnd() => Program.Window?.EndDrag();

    [OrielCommand("win.toggleFullscreen")]
    public static bool ToggleFullscreen() => Program.Window?.ToggleFullscreen() ?? false;

    [OrielCommand("win.toggleOnTop")]
    public static bool ToggleOnTop() => Program.Window?.ToggleOnTop() ?? false;

    [OrielCommand("win.pickFile")]
    public static string? PickFile() => Program.Window?.ShowOpenFileDialog("选择文件", "所有文件|*.*");
}

/// <summary>DTO：STJ 源生成上下文（AOT 安全序列化的唯一入口）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoItem[]))]
[JsonSerializable(typeof(SysInfo))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
