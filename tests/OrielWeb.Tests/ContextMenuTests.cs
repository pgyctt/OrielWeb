using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建右键菜单的两块纯逻辑：
/// <list type="bullet">
/// <item>Windows 的「保留名单」——它仍在就地过滤引擎菜单（<see cref="OrielContextMenuSupport"/>）。</item>
/// <item>Linux/macOS 接管式菜单的内容与 role 归类（<see cref="OrielMenuRoles"/>）。</item>
/// </list>
/// </summary>
/// <remarks>
/// 这些判断能被单测，是因为它们被刻意抽成了与平台无关的纯数据比较；真正的菜单弹出与编辑命令
/// 在各平台后端里，只能在对应平台上人眼验证。
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
    // 编辑类 role 的归类：接管式菜单靠它决定"哪些项要走引擎的原生编辑命令"
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(OrielMenuRole.Undo)]
    [InlineData(OrielMenuRole.Redo)]
    [InlineData(OrielMenuRole.Cut)]
    [InlineData(OrielMenuRole.Copy)]
    [InlineData(OrielMenuRole.Paste)]
    [InlineData(OrielMenuRole.SelectAll)]
    [InlineData(OrielMenuRole.Delete)]
    public void 编辑类_role_被归入_editing(string role)
        => Assert.True(OrielMenuRoles.IsEditingRole(role));

    [Theory]
    // 窗口与应用类 role 不该走编辑命令——它们没有原生的 editing selector/command 可对
    [InlineData(OrielMenuRole.Quit)]
    [InlineData(OrielMenuRole.Close)]
    [InlineData(OrielMenuRole.Minimize)]
    [InlineData(OrielMenuRole.Zoom)]
    [InlineData(OrielMenuRole.ToggleFullScreen)]
    [InlineData(OrielMenuRole.About)]
    // 未知 role 也不归入
    [InlineData("noSuchRole")]
    [InlineData("")]
    public void 非编辑_role_不归入_editing(string role)
        => Assert.False(OrielMenuRoles.IsEditingRole(role));

    // ------------------------------------------------------------------
    // 接管式菜单的内容：恰好是剪切/复制/粘贴三项，顺序固定，且都有默认文案
    // ------------------------------------------------------------------

    [Fact]
    public void 接管式菜单只含剪切复制粘贴()
    {
        IReadOnlyList<OrielMenuItem> items = OrielMenuRoles.EditingMenuItems();

        // 项数与顺序一起钉住：Linux/macOS 两个后端都直接消费这个序列，
        // 哪一侧多塞一项（比如顺手把"全选"加回去）都会让 Editing 语义悄悄跑偏。
        Assert.Equal(
            [OrielMenuRole.Cut, OrielMenuRole.Copy, OrielMenuRole.Paste],
            items.Select(item => item.Role));

        // 每一项都要有默认文案，否则菜单上会出现空白项
        Assert.All(items, item =>
        {
            Assert.NotNull(item.Role);
            Assert.NotNull(OrielMenuRoles.DefaultLabel(item.Role));
        });
    }

    [Fact]
    public void 接管式菜单的项都不是自定义项()
    {
        // role 项不该带 Id：带上就会在自己弹菜单时走 ContextMenuItemClicked 那条路，
        // 而不是落到引擎的编辑命令上（表现为点了没反应）。
        Assert.All(OrielMenuRoles.EditingMenuItems(), item => Assert.Null(item.Id));
    }
}
