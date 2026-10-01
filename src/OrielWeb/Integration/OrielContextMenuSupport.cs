namespace OrielWeb;

/// <summary>
/// 「编辑类」内建右键菜单项的识别。macOS 与 Linux 改用接管式之后，只剩 WebView2 还需要它。
/// </summary>
/// <remarks>
/// <para>
/// Windows 仍是「就地过滤引擎菜单」：WebView2 没有公开的 cut/copy/paste 编程接口，自建菜单的
/// 编辑项只能退回 <c>document.execCommand</c>，而粘贴会被安全策略拦下。所以它保留过滤式，
/// 由引擎自己执行留下来的项。
/// </para>
/// <para>
/// macOS 与 Linux 现在自己弹只含剪切/复制/粘贴的菜单（见 <see cref="OrielMenuRoles.EditingMenuItems"/>），
/// 编辑命令走各自引擎的原生通道，因此不再需要读取引擎菜单的项——原来那两张识别表
/// （Cocoa 的 <c>NSMenuItem.identifier</c> 常量、WebKitGTK 的 <c>WebKitContextMenuAction</c> 整数编号）
/// 连同它们依赖的引擎内部结构一起删掉了。那套读改在 WebKitGTK 4.1 上会破坏内存。
/// </para>
/// </remarks>
internal static class OrielContextMenuSupport
{
    // ---- WebView2：ICoreWebView2ContextMenuItem.Name ----
    // 官方定义：Name 是**未本地化**的名称（英文小驼峰），供代码判断；给用户看的本地化文本在 Label。
    // 取值形如 "cut" / "copy" / "paste"，同族还有 "saveAs" / "copyImage" / "openLinkInNewWindow"。
    //
    // 注意：WebView2 的 COREWEBVIEW2_CONTEXT_MENU_ITEM_KIND 是**控件种类**
    //（Command / CheckBox / Radio / Separator / Submenu），拿它判"是不是复制"是认错了维度。

    internal const string Win32Cut = "cut";
    internal const string Win32Copy = "copy";
    internal const string Win32Paste = "paste";

    /// <summary>
    /// 判断一个 WebView2 菜单项名称是否属于「保留」范围。
    /// </summary>
    /// <remarks>
    /// 刻意保持大小写敏感：官方口径就是小驼峰，万一日后平台改了写法，宁可测试立刻红，
    /// 也不要静默地"看起来也能用"。
    /// </remarks>
    internal static bool IsEditingWin32Name(string? name)
        => name is Win32Cut or Win32Copy or Win32Paste;
}
