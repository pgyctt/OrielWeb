namespace OrielWeb;

/// <summary>
/// webview **内建**右键菜单的策略（页面自己画的右键菜单不受影响）。
/// </summary>
/// <remarks>
/// 这个名字容易和 <see cref="OrielMenuItem"/> 那套（宿主自建的上下文菜单，见
/// <see cref="WebviewWindow.ShowContextMenu"/>）混淆——两者是**两条独立通道**：
/// 这里管的是 WebView2 / WKWebView / WebKitGTK 自己弹的那个菜单，
/// 而那套是宿主窗口弹自己构造的菜单。它们互不干涉，可以同时使用。
/// </remarks>
public enum OrielContextMenuPolicy
{
    /// <summary>
    /// 只保留<b>剪切 / 复制 / 粘贴</b>（默认）。
    /// </summary>
    /// <remarks>
    /// 内建菜单默认带着「后退 / 前进 / 刷新 / 另存为 / 打印 / 检查元素」这些项，
    /// 对一个应用窗口而言绝大多数是噪音：<c>刷新</c> 在单页应用里会丢掉整页状态，
    /// <c>另存为</c> 存下来的是一份孤立的 HTML 外壳（它依赖的资产不在里面），
    /// 而 <c>后退</c> 会跑出应用自己的路由。
    /// <para>
    /// 留下这三项是因为它们**只有原生侧能给**：粘贴需要把系统剪贴板内容送进页面的编辑区，
    /// 这件事页面自己做不了（<c>document.execCommand('paste')</c> 在现代浏览器里被禁用），
    /// 而原生菜单项的行为由渲染引擎直接完成。
    /// </para>
    /// </remarks>
    Editing = 0,

    /// <summary>平台原生菜单**原样**弹出（含后退、刷新、另存为、检查元素等）。</summary>
    /// <remarks>
    /// 开发时想用「检查元素」又不愿开 DevTools 面板的场合选它。它对应各平台的默认行为，
    /// 也是本库在没有显式配置时的历史行为。
    /// </remarks>
    Native = 1,

    /// <summary>完全不弹（右键无反应）。</summary>
    Disabled = 2,
}
