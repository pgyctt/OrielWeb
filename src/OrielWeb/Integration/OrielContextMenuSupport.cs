namespace OrielWeb;

/// <summary>
/// 「编辑类」内建右键菜单项的识别：三平台各有自己的表示，这里把判断集中起来，
/// 于是它可以在**任意平台**上被单测（包括为 macOS 与 Linux 写的那两个）。
/// </summary>
/// <remarks>
/// 三平台表示同一个概念用的是三种完全不同的东西：
/// <list type="bullet">
/// <item>WebView2：强类型枚举 <c>COREWEBVIEW2_CONTEXT_MENU_ITEM_KIND</c>——**用不到本类**，
/// 那里直接比枚举成员（见 <c>Win32WindowHost.FilterEditingItems</c> 的说明）。</item>
/// <item>Cocoa：<c>NSMenuItem.identifier</c> 里的 WebKit 公开常量字符串。</item>
/// <item>WebKitGTK：<c>WebKitContextMenuAction</c> 的整数编号。</item>
/// </list>
/// 后两者在 C# 里都只能落到字符串/整数上，没有编译期检查——写错了不会报错，
/// 只会表现为"右键菜单里多出一项"或"少一项"。所以它们被集中到这里并配了单测。
/// </remarks>
internal static class OrielContextMenuSupport
{
    // ---- Cocoa：WKMenuItemIdentifier 系列（WebKit 的公开常量，macOS 11+）----
    // 这些常量的**字符串值就等于常量名**，Cocoa 侧取到的 identifier 直接与它们比较。

    internal const string CocoaCut = "WKMenuItemIdentifierCut";
    internal const string CocoaCopy = "WKMenuItemIdentifierCopy";
    internal const string CocoaPaste = "WKMenuItemIdentifierPaste";

    /// <summary>
    /// 判断一个 Cocoa 菜单项标识是否属于"保留"范围。
    /// </summary>
    /// <remarks>
    /// <c>null</c>（旧系统或第三方插入的项没有 identifier）**不保留**——按白名单行事，
    /// 未知的东西一律当噪音删掉；反过来按黑名单写就会漏掉没预料到的项。
    /// </remarks>
    internal static bool IsEditingCocoaIdentifier(string? identifier)
        => identifier is CocoaCut or CocoaCopy or CocoaPaste;

    // ---- WebKitGTK：WebKitContextMenuAction 的数值 ----
    // 取自 webkit2gtk 4.1 的 WebKitContextMenuActions.h 枚举体顺序（NO_ACTION = 0 起算）：
    //   GO_BACK = 10, GO_FORWARD = 11, STOP = 12, RELOAD = 13, COPY = 14, CUT = 15, PASTE = 16,
    //   DELETE = 17, SELECT_ALL = 18
    // 锚点写成注释是因为这几个数值**没有**任何编译期保障，只有头文件里的顺序。

    internal const int WebKitActionCopy = 14;
    internal const int WebKitActionCut = 15;
    internal const int WebKitActionPaste = 16;

    /// <summary>判断一个 WebKitGTK 菜单项动作是否属于"保留"范围。</summary>
    /// <remarks>
    /// 分隔线的动作是 <c>NO_ACTION</c>(0)、"检查元素"是 33、输入法是 21——都会被删掉。
    /// </remarks>
    internal static bool IsEditingWebKitAction(int action)
        => action is WebKitActionCopy or WebKitActionCut or WebKitActionPaste;

    // ---- WebView2：ICoreWebView2ContextMenuItem.Name ----
    // 官方定义：Name 是**未本地化**的名称（英文小驼峰），供代码判断；给用户看的本地化文本在 Label。
    // 所以这里比 Name 而不是 Label——与 macOS 比 identifier 而不是 title 是同一个道理。
    // 取值形如 "cut" / "copy" / "paste"，同族还有 "saveAs" / "copyImage" / "openLinkInNewWindow"。
    //
    // 注意：WebView2 的 COREWEBVIEW2_CONTEXT_MENU_ITEM_KIND 是**控件种类**
    //（Command / CheckBox / Radio / Separator / Submenu），拿它判"是不是复制"是认错了维度。

    internal const string Win32Cut = "cut";
    internal const string Win32Copy = "copy";
    internal const string Win32Paste = "paste";

    internal static bool IsEditingWin32Name(string? name)
        => name is Win32Cut or Win32Copy or Win32Paste;
}
