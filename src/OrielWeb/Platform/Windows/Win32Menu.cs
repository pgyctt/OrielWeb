using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Win32 弹出菜单的构建、跟踪与释放（托盘菜单、窗口上下文菜单共用）。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来是因为<b>菜单构建只有一套</b>：分隔线、禁用、勾选、子菜单、加速键显示、role 默认文案
/// 这些规则在三种菜单里完全相同，各写一遍必然漂移。这里只负责"画"与"报回选择"，
/// role 的语义由 <see cref="OrielMenuRoles"/> 统一解释。
/// </para>
/// <para>
/// 命令 id 由本对象分配并维护映射（<c>TPM_RETURNCMD</c> 直接返回它），
/// 因此一个 <see cref="Win32Menu"/> 实例对应"一份菜单定义"，重建菜单就是换一个实例。
/// </para>
/// </remarks>
internal sealed unsafe class Win32Menu : IDisposable
{
    private nint _handle;
    private readonly Dictionary<uint, OrielMenuItem> _targets = [];
    private uint _nextCommandId = 1;
    private bool _disposed;

    private Win32Menu(nint handle) => _handle = handle;

    /// <summary>构建一份菜单；<paramref name="items"/> 为空时返回 null（调用方按"没有菜单"处理）。</summary>
    internal static Win32Menu? Build(IReadOnlyList<OrielMenuItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        nint handle = Win32.CreatePopupMenu();
        if (handle == 0)
        {
            return null;
        }

        var menu = new Win32Menu(handle);
        menu.AppendAll(handle, items);
        return menu;
    }

    /// <summary>按命令 id 找回菜单项（<see cref="Popup"/> 用它把 <c>TrackPopupMenuEx</c> 的返回值还原成项）。</summary>
    internal OrielMenuItem? Find(uint commandId)
        => _targets.TryGetValue(commandId, out OrielMenuItem? item) ? item : null;

    /// <summary>
    /// 在鼠标位置弹出并跟踪，返回用户选中的项（取消或选到不可选项时为 null）。
    /// 这是**阻塞调用**：<c>TrackPopupMenuEx</c> 自带模态消息循环。
    /// </summary>
    internal OrielMenuItem? Popup(nint ownerHwnd)
    {
        if (_handle == 0)
        {
            return null;
        }

        _ = Win32.GetCursorPos(out POINT cursor);

        // 两处经典 workaround，缺了会出"点菜单外面不消失"或"菜单立刻被关掉"：
        // 弹出前把宿主窗口置前台；跟踪结束后补一条 WM_NULL 收尾消息队列。
        _ = Win32.SetForegroundWindow(ownerHwnd);
        uint command = Win32.TrackPopupMenuEx(
            _handle,
            Win32Constants.TPM_RIGHTBUTTON | Win32Constants.TPM_RETURNCMD,
            cursor.X,
            cursor.Y,
            ownerHwnd,
            0);
        _ = Win32.PostMessageW(ownerHwnd, Win32Constants.WM_NULL, 0, 0);

        return command == 0 ? null : Find(command);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targets.Clear();

        if (_handle != 0)
        {
            // 子菜单由父菜单持有，DestroyMenu 会递归销毁它们，这里只释放顶层
            _ = Win32.DestroyMenu(_handle);
            _handle = 0;
        }
    }

    private void AppendAll(nint menu, IReadOnlyList<OrielMenuItem> items)
    {
        foreach (OrielMenuItem item in items)
        {
            if (item.IsSeparator)
            {
                _ = Win32.AppendMenuW(menu, Win32Constants.MF_SEPARATOR, 0, null);
                continue;
            }

            uint flags = Win32Constants.MF_STRING;
            if (!item.Enabled)
            {
                flags |= Win32Constants.MF_GRAYED;
            }
            if (item.Checked)
            {
                flags |= Win32Constants.MF_CHECKED;
            }

            string label = BuildLabel(item);

            if (item.Items is { Count: > 0 } children)
            {
                nint submenu = Win32.CreatePopupMenu();
                if (submenu != 0)
                {
                    AppendAll(submenu, children);
                    _ = Win32.AppendMenuW(menu, flags | Win32Constants.MF_POPUP, (nuint)submenu, label);
                    continue;
                }
            }

            // role 项也要分配 id：点击要回到 role 解释器去执行平台动作
            uint id = _nextCommandId++;
            _targets[id] = item;
            _ = Win32.AppendMenuW(menu, flags, id, label);
        }
    }

    private static string BuildLabel(OrielMenuItem item)
    {
        string text = item.Label ?? OrielMenuRoles.DefaultLabel(item.Role) ?? string.Empty;

        // Windows 菜单用制表符把快捷键右对齐显示。它只是显示：库不拦截按键，按键仍会送到页面
        // （与 macOS 上"真正生效的 key equivalent"不同，见 README 平台矩阵）。
        if (item.Accelerator is { Length: > 0 } acceleratorText
            && OrielAccelerator.TryParse(acceleratorText, out OrielAccelerator? accelerator))
        {
            text += "\t" + accelerator!.DisplayString;
        }

        return text;
    }
}
