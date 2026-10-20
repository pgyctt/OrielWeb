using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 把 HDROP 读成路径列表。
/// </summary>
/// <remarks>
/// 抽出来是因为它有**两个来源**：<c>WM_DROPFILES</c> 的 <c>wParam</c>，和 OLE 的
/// <c>STGMEDIUM.hGlobal</c>（同一个 HDROP 布局）。两处各写一遍会各自腐化——
/// 尤其"取长度读两次"这段，少一次就得到截断的路径。
/// <para>
/// <b>不负责释放</b>：前者由调用方 <c>DragFinish</c>，后者由 <c>ReleaseStgMedium</c>。
/// 谁分配谁释放这条规矩不能在这里被吞掉。
/// </para>
/// </remarks>
internal static class Win32FileDropReader
{
    internal static List<string> Read(nint hDrop)
    {
        if (hDrop == 0)
        {
            return [];
        }

        uint count = Win32.DragQueryFileW(hDrop, 0xFFFFFFFF, 0, 0);
        var paths = new List<string>((int)count);

        for (uint i = 0; i < count; i++)
        {
            // 先问长度、再读内容：一次调用拿不到两者
            uint length = Win32.DragQueryFileW(hDrop, i, 0, 0);
            if (length == 0)
            {
                continue;
            }

            nint buffer = Marshal.AllocHGlobal((int)(length + 1) * sizeof(char));
            try
            {
                Marshal.WriteInt16(buffer, 0);
                if (Win32.DragQueryFileW(hDrop, i, buffer, length + 1) > 0
                    && Marshal.PtrToStringUni(buffer) is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return paths;
    }
}
