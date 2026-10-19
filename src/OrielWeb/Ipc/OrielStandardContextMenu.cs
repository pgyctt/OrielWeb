namespace OrielWeb.Ipc;

/// <summary>
/// 库内建的"标准编辑菜单"：剪切 / 复制 / 粘贴 / 全选 / 关闭。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来是因为 <c>win.contextMenu</c> 与 <c>window.contextMenu</c> 两个入口都要用它——
/// 各写一份的话，两份菜单迟早会不一致（多一项少一项都不报错，只是行为悄悄不同）。
/// </para>
/// <para>
/// 只放**跨应用通用**的项：应用自己的菜单项（业务动作、勾选项）仍应由应用用
/// <see cref="WebviewWindow.ShowContextMenu(IReadOnlyList{OrielMenuItem})"/> 自己弹，那是它才知道的内容。
/// 编辑类 role 是尽力而为的（见 <see cref="OrielMenuRoles"/> 的说明）。
/// </para>
/// </remarks>
internal static class OrielStandardContextMenu
{
    /// <summary>标准编辑菜单的项。</summary>
    internal static IReadOnlyList<OrielMenuItem> Items { get; } =
    [
        OrielMenuItem.RoleItem(OrielMenuRole.Cut),
        OrielMenuItem.RoleItem(OrielMenuRole.Copy),
        OrielMenuItem.RoleItem(OrielMenuRole.Paste),
        OrielMenuItem.Separator(),
        OrielMenuItem.RoleItem(OrielMenuRole.SelectAll),
        OrielMenuItem.Separator(),
        OrielMenuItem.RoleItem(OrielMenuRole.Close),
    ];
}
