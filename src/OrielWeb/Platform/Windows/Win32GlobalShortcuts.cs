using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 全局快捷键：<c>RegisterHotKey</c> + 调度窗口收到的 <c>WM_HOTKEY</c>。
/// </summary>
/// <remarks>
/// <para>
/// 用平台已有的调度窗口作为接收者，因此不需要额外的消息循环或专用线程——
/// 这正是托盘与通知选择的同一宿主（见 <see cref="WindowsPlatformBackend.MessageWindowHandle"/>）。
/// </para>
/// <para>
/// id 的分配区间是 <c>0x4F00</c> 起：Win32 规定应用可用 0x0000–0xBFFF（0xC000 以上归系统），
/// 取一个中段的起始值可以避免与"应用自己也可能用低 id 注册别的热键"撞车。
/// </para>
/// </remarks>
internal sealed class Win32GlobalShortcuts
{
    private const int FirstId = 0x4F00;
    private const int MaxId = 0xBFFF;

    private readonly nint _hwnd;
    private readonly Dictionary<string, int> _idsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _keysById = [];
    private int _nextId = FirstId;

    internal Win32GlobalShortcuts(nint hwnd) => _hwnd = hwnd;

    /// <summary>某个快捷键被按下；参数是注册时给的 id（规范化串）。</summary>
    internal event Action<string>? Activated;

    internal bool Register(OrielAccelerator accelerator, string id)
    {
        if (_idsByKey.ContainsKey(id))
        {
            return true;
        }

        if (_nextId > MaxId || !TryMapKey(accelerator.Key, out uint virtualKey))
        {
            return false;
        }

        uint modifiers = 0;
        if (accelerator.Alt)
        {
            modifiers |= Win32Constants.MOD_ALT;
        }
        if (accelerator.Control)
        {
            modifiers |= Win32Constants.MOD_CONTROL;
        }
        if (accelerator.Shift)
        {
            modifiers |= Win32Constants.MOD_SHIFT;
        }
        if (accelerator.Command)
        {
            modifiers |= Win32Constants.MOD_WIN;
        }

        int hotkeyId = _nextId++;
        // 失败最常见的原因是组合已被别的程序占用；这属于正常结果，如实返回 false 让调用方提示用户
        if (!Win32.RegisterHotKey(_hwnd, hotkeyId, modifiers | Win32Constants.MOD_NOREPEAT, virtualKey))
        {
            return false;
        }

        _idsByKey[id] = hotkeyId;
        _keysById[hotkeyId] = id;
        return true;
    }

    internal bool Unregister(string id)
    {
        if (!_idsByKey.Remove(id, out int hotkeyId))
        {
            return false;
        }

        _keysById.Remove(hotkeyId);
        return Win32.UnregisterHotKey(_hwnd, hotkeyId);
    }

    internal void UnregisterAll()
    {
        foreach (int hotkeyId in _keysById.Keys)
        {
            _ = Win32.UnregisterHotKey(_hwnd, hotkeyId);
        }

        _idsByKey.Clear();
        _keysById.Clear();
    }

    /// <summary><c>WM_HOTKEY</c> 的 wParam 就是注册时给的 id。</summary>
    internal void HandleHotKey(nuint hotkeyId)
    {
        if (_keysById.TryGetValue((int)hotkeyId, out string? id))
        {
            Activated?.Invoke(id);
        }
    }

    /// <summary>把 <see cref="OrielAccelerator"/> 的规范化键名映射成虚拟键码。</summary>
    private static bool TryMapKey(string key, out uint virtualKey)
    {
        virtualKey = 0;

        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            // 字母与数字的 VK 就是它的大写 ASCII 码（'A' = 0x41、'7' = 0x37）
            virtualKey = key[0];
            return true;
        }

        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(key[1..], out int functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1); // VK_F1 = 0x70
            return true;
        }

        return s_namedKeys.TryGetValue(key, out virtualKey);
    }

    /// <summary>具名键的虚拟键码（键名与 <see cref="OrielAccelerator"/> 的规范化名一致）。</summary>
    private static readonly Dictionary<string, uint> s_namedKeys = new(StringComparer.Ordinal)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Comma"] = 0xBC,
        ["Period"] = 0xBE,
        ["Slash"] = 0xBF,
        ["Semicolon"] = 0xBA,
        ["Quote"] = 0xDE,
        ["BracketLeft"] = 0xDB,
        ["BracketRight"] = 0xDD,
        ["Backslash"] = 0xDC,
        ["Minus"] = 0xBD,
        ["Equal"] = 0xBB,
        ["Backquote"] = 0xC0,
    };
}
