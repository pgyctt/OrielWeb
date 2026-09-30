using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 开机自启：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 下的一个值。
/// </summary>
/// <remarks>
/// <para>
/// 用 HKCU 而不是 HKLM：前者不需要管理员权限，且"开机自启"本来就该是**当前用户**的偏好。
/// 也不走"启动"文件夹里的 .lnk 快捷方式——那需要 COM（IShellLink），
/// 而注册表这条路只需一次 <c>RegSetValueExW</c>。
/// </para>
/// <para>
/// 与 Linux/macOS 一样只写不读回内存状态：<see cref="IsEnabled"/> 直接查注册表，
/// 用户可能在"任务管理器 → 启动"里把它禁掉，那时这里应当如实反映。
/// </para>
/// </remarks>
internal static class Win32AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static bool Enable(string id, IReadOnlyList<string>? arguments)
    {
        string value = AutoStartContent.WindowsRunValue(ExecutablePath, arguments);
        nint buffer = Marshal.StringToHGlobalUni(value);
        try
        {
            // 字节数含结尾的 NUL
            uint bytes = (uint)((value.Length + 1) * 2);
            int status = Win32.RegSetValueExW(
                Win32Constants.HKEY_CURRENT_USER,
                RunKey,
                0,
                Win32Constants.REG_SZ,
                buffer,
                bytes);

            if (status != 0)
            {
                Debug.WriteLine($"[OrielWeb] 写 Run 键失败（Win32 错误 {status}）。");
            }

            return status == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool Disable(string id)
    {
        int status = Win32.RegDeleteValueW(Win32Constants.HKEY_CURRENT_USER, RunKey, id);
        if (status != 0)
        {
            // 值本来就不存在（ERROR_FILE_NOT_FOUND = 2）也算成功：结果就是"没有自启项"
            return status == 2;
        }

        return true;
    }

    internal static bool IsEnabled(string id)
    {
        uint size = 0;
        int status = Win32.RegGetValueW(
            Win32Constants.HKEY_CURRENT_USER,
            RunKey,
            id,
            Win32Constants.RRF_RT_REG_SZ,
            0,
            0,
            ref size);

        // 先探大小：值存在即已启用（内容不关心；进程路径写死在这里，用户改路径也仍是"启用了"）
        return status == 0 || status == Win32Constants.ERROR_MORE_DATA;
    }

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;
}
