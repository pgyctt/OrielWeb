using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建右键菜单过滤的「保留名单」。三平台用三种完全不同的东西表示同一个概念，
/// 其中两种（Cocoa 的字符串标识、WebKitGTK 的整数编号）在 C# 里都没有编译期检查，
/// 写错了只会表现为「右键菜单里多一项或少一项」——所以这里逐个钉住。
/// </summary>
/// <remarks>
/// 这些判断之所以能被单测，是因为它们被刻意抽成了与平台无关的纯数据比较
/// （见 <see cref="OrielContextMenuSupport"/>）；真正的菜单操作在各平台后端里，
/// 那部分只能在对应平台上人眼验证。
/// </remarks>
public class ContextMenuTests
{
    // ------------------------------------------------------------------
    // WebView2：ICoreWebView2ContextMenuItem.Name
    // 官方定义 Name 是"未本地化的名称"（英文小驼峰），Label 才是本地化文本。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("cut")]
    [InlineData("copy")]
    [InlineData("paste")]
    public void Win32_编辑三项被保留(string name)
        => Assert.True(OrielContextMenuSupport.IsEditingWin32Name(name));

    [Theory]
    // 这几个是**真正的陷阱**：都跟"复制"沾边，但都不是"复制选区"
    [InlineData("copyImage")]
    [InlineData("copyLink")]
    [InlineData("copyImageUrl")]
    [InlineData("copyVideoLink")]
    // 与粘贴相邻的另一项粘贴
    [InlineData("pasteAndMatchStyle")]
    // 常见的噪音项
    [InlineData("saveAs")]
    [InlineData("openLinkInNewWindow")]
    [InlineData("selectAll")]
    [InlineData("inspectElement")]
    [InlineData("reload")]
    [InlineData("back")]
    [InlineData("extension")]
    [InlineData("custom")]
    [InlineData("spellCheck")]
    // 空与缺失
    [InlineData("")]
    [InlineData("Cut")]
    [InlineData(null)]
    public void Win32_其它项被剔除(string? name)
    {
        // 注意最后两个用例：
        //  - ""（空串）不该被当成有效名称
        //  - "Cut"（大写）**不**匹配——官方口径是小驼峰，因此这里刻意保持大小写敏感，
        //    万一日后平台改了写法，测试会立刻红，而不是静默地"看起来也能用"
        Assert.False(OrielContextMenuSupport.IsEditingWin32Name(name));
    }

    // ------------------------------------------------------------------
    // Cocoa：NSMenuItem.identifier（WebKit 的 WKMenuItemIdentifier 公开常量）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("WKMenuItemIdentifierCut")]
    [InlineData("WKMenuItemIdentifierCopy")]
    [InlineData("WKMenuItemIdentifierPaste")]
    public void Cocoa_编辑三项被保留(string identifier)
        => Assert.True(OrielContextMenuSupport.IsEditingCocoaIdentifier(identifier));

    [Theory]
    [InlineData("WKMenuItemIdentifierCopyLink")]
    [InlineData("WKMenuItemIdentifierCopyImage")]
    [InlineData("WKMenuItemIdentifierInspectElement")]
    [InlineData("WKMenuItemIdentifierReload")]
    [InlineData("WKMenuItemIdentifierGoBack")]
    [InlineData("WKMenuItemIdentifierSelectAll")]
    [InlineData("WKMenuItemIdentifierPasteAndMatchStyle")]
    // 旧系统的项没有 identifier，读出来是 null，等于"不认识的项"→ 按白名单剔除
    [InlineData(null)]
    [InlineData("")]
    public void Cocoa_其它项被剔除(string? identifier)
        => Assert.False(OrielContextMenuSupport.IsEditingCocoaIdentifier(identifier));

    // ------------------------------------------------------------------
    // WebKitGTK：WebKitContextMenuAction 的整数编号
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(14)] // COPY
    [InlineData(15)] // CUT
    [InlineData(16)] // PASTE
    public void WebKit_编辑三项被保留(int action)
        => Assert.True(OrielContextMenuSupport.IsEditingWebKitAction(action));

    [Theory]
    [InlineData(0)]     // NO_ACTION：分隔线
    [InlineData(10)]    // GO_BACK
    [InlineData(11)]    // GO_FORWARD
    [InlineData(12)]    // STOP
    [InlineData(13)]    // RELOAD —— 紧邻 COPY，错一位就会把"刷新"留下
    [InlineData(17)]    // DELETE —— 紧邻 PASTE，错一位就会把"删除"留下
    [InlineData(18)]    // SELECT_ALL
    [InlineData(21)]    // INPUT_METHODS
    [InlineData(33)]    // INSPECT_ELEMENT
    [InlineData(1)]     // OPEN_LINK
    [InlineData(10000)] // CUSTOM：应用自己插的项
    [InlineData(-1)]
    public void WebKit_其它项被剔除(int action)
    {
        // 13 与 17 是特意选的边界：它们就贴在 14/15/16 两侧。
        // 枚举编号是从 webkit2gtk 4.1 头文件的枚举体顺序数出来的，没有编译期保障——
        // 那两个用例的意义就是"如果有人数错了一位，这里立刻红"。
        Assert.False(OrielContextMenuSupport.IsEditingWebKitAction(action));
    }

    // ------------------------------------------------------------------
    // 三个名单是同一组语义：三处都恰好收三项，且互不重叠地覆盖剪切/复制/粘贴
    // ------------------------------------------------------------------

    [Fact]
    public void 三平台保留的项数一致()
    {
        // 这条不是形式主义：三个名单散在三段代码里，很容易在某一处多留/少留一项
        // （比如只在 Linux 上顺手把"全选"加了回去）。项数一致是最低限度的对齐检查。
        int win32 = CountKept(name => OrielContextMenuSupport.IsEditingWin32Name(name),
            "cut", "copy", "paste", "selectAll", "reload", "inspectElement");

        int cocoa = CountKept(id => OrielContextMenuSupport.IsEditingCocoaIdentifier(id),
            "WKMenuItemIdentifierCut", "WKMenuItemIdentifierCopy", "WKMenuItemIdentifierPaste",
            "WKMenuItemIdentifierSelectAll", "WKMenuItemIdentifierReload", "WKMenuItemIdentifierInspectElement");

        int webkit = CountKept(action => OrielContextMenuSupport.IsEditingWebKitAction(action),
            14, 15, 16, 18, 13, 33);

        Assert.Equal(3, win32);
        Assert.Equal(3, cocoa);
        Assert.Equal(3, webkit);
    }

    private static int CountKept<T>(Func<T, bool> keep, params T[] candidates)
        => candidates.Count(keep);
}
