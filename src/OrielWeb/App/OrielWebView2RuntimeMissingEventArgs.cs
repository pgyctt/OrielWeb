namespace OrielWeb;

/// <summary>
/// 「WebView2 运行时不可用」的处置上下文，经 <see cref="OrielAppBuilder.OnWebView2RuntimeMissing"/>
/// 交给应用处理（仅 Windows 会触发）。
/// </summary>
/// <remarks>
/// 触发时机是窗口装配期：先用 WebView2Loader 询问可用的运行时版本，取不到即进入本流程。
/// 此时窗口已经创建但没有任何内容——界面渲染完全依赖 WebView2。
/// </remarks>
public sealed class OrielWebView2RuntimeMissingEventArgs
{
    /// <summary>微软 WebView2 Evergreen Bootstrapper 的官方下载地址（永久链接）。</summary>
    public const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    internal OrielWebView2RuntimeMissingEventArgs(WebviewWindow window, string? browserExecutableFolder)
    {
        Window = window;
        BrowserExecutableFolder = browserExecutableFolder;
    }

    /// <summary>装配失败的窗口（尚未加载任何页面）。</summary>
    public WebviewWindow Window { get; }

    /// <summary>
    /// 浏览器进程目录，取自环境变量 <c>ORIEL_WEBVIEW2_FOLDER</c>；为 null 表示使用系统 Evergreen 运行时。
    /// 该值非 null 却仍取不到运行时，通常说明它没有指向有效的固定版本运行时目录。
    /// </summary>
    public string? BrowserExecutableFolder { get; }

    /// <summary>
    /// 置 true 表示回调已自行提示、并要求保留窗口；默认 false——回调返回后库销毁窗口。
    /// 没有 WebView2 的窗口没有内容可按，销毁即让应用退出，与"装完运行时再启动"的流程一致。
    /// </summary>
    public bool KeepWindowOpen { get; set; }
}
