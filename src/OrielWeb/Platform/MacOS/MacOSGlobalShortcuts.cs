using System.Diagnostics;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 全局快捷键：Carbon 的 <c>RegisterEventHotKey</c>。
/// </summary>
/// <remarks>
/// <para>
/// Carbon 早已标称弃用，但 <c>RegisterEventHotKey</c> 至今是**唯一不需要辅助功能/输入监控权限**
/// 就能注册系统级快捷键的公开接口——主流应用与框架都还在用它（Ryn 在同一处也选了它）。
/// 换用 <c>CGEventTap</c> 或 <c>NSEvent</c> 全局监听都要用户去系统设置里授权，对一个"注册快捷键"
/// 的诉求来说代价过高。
/// </para>
/// <para>
/// 事件回调经 <c>InstallEventHandler</c> 装到应用事件目标上，回调里要把
/// <c>EventHotKeyID</c> 从事件参数里取出来才知道是哪一个快捷键被按下——Carbon 不会把
/// "哪个 handler 对应哪个热键"关联起来（userData 是每个 handler 一份，不是每个热键一份）。
/// </para>
/// </remarks>
internal sealed unsafe partial class MacOSGlobalShortcuts
{
    private const string CarbonFramework = "/System/Library/Frameworks/Carbon.framework/Carbon";

    /// <summary>本应用注册的热键签名（'ORIE'），用来忽略别的进程/组件发来的同类事件。</summary>
    private const uint Signature = 0x4F524945;

    // Carbon 事件常量：kEventClassKeyboard = 'keyb'、kEventHotKeyPressed = 5
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;

    // 事件参数：kEventParamDirectObject = '----'、typeEventHotKeyID = 'hkid'
    private const uint ParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyID = 0x686B6964;

    // Carbon 修饰键位（与 NSEvent 的位不同，不能混用）
    private const uint CmdKey = 1 << 8;
    private const uint ShiftKey = 1 << 9;
    private const uint OptionKey = 1 << 11;
    private const uint ControlKey = 1 << 12;

    private readonly Dictionary<string, nint> _refsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _keysById = [];
    private uint _nextId = 1;
    private bool _handlerInstalled;

    internal event Action<string>? Activated;

    internal bool Register(OrielAccelerator accelerator, string id)
    {
        if (_refsByKey.ContainsKey(id))
        {
            return true;
        }

        if (!TryMapKey(accelerator.Key, out uint keyCode))
        {
            return false;
        }

        try
        {
            if (!_handlerInstalled && !InstallHandler())
            {
                return false;
            }

            uint modifiers = 0;
            if (accelerator.Command)
            {
                modifiers |= CmdKey;
            }
            if (accelerator.Shift)
            {
                modifiers |= ShiftKey;
            }
            if (accelerator.Alt)
            {
                modifiers |= OptionKey;
            }
            if (accelerator.Control)
            {
                modifiers |= ControlKey;
            }

            uint hotKeyId = _nextId++;
            var hotKeyIdStruct = new EventHotKeyID { Signature = Signature, Id = hotKeyId };

            // 失败多因该组合已被其它程序占用（eventHotKeyExistsErr），如实返回 false
            int status = RegisterEventHotKey(
                keyCode, modifiers, hotKeyIdStruct, GetApplicationEventTarget(), 0, out nint hotKeyRef);
            if (status != 0 || hotKeyRef == 0)
            {
                Debug.WriteLine($"[OrielWeb] RegisterEventHotKey 失败（OSStatus={status}）：{id}");
                return false;
            }

            _refsByKey[id] = hotKeyRef;
            _keysById[hotKeyId] = id;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            // Carbon 加载不上（理论上不该发生）：如实报告"注册不了"，而不是让调用方以为成功了
            Debug.WriteLine($"[OrielWeb] 加载 Carbon 失败，全局快捷键不可用：{ex.Message}");
            return false;
        }
    }

    internal bool Unregister(string id)
    {
        if (!_refsByKey.Remove(id, out nint hotKeyRef))
        {
            return false;
        }

        _ = UnregisterEventHotKey(hotKeyRef);

        foreach ((uint hotKeyId, string key) in _keysById.ToArray())
        {
            if (key == id)
            {
                _keysById.Remove(hotKeyId);
            }
        }

        return true;
    }

    internal void UnregisterAll()
    {
        foreach (nint hotKeyRef in _refsByKey.Values)
        {
            _ = UnregisterEventHotKey(hotKeyRef);
        }

        _refsByKey.Clear();
        _keysById.Clear();
    }

    private bool InstallHandler()
    {
        var spec = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };

        int status = InstallEventHandler(
            GetApplicationEventTarget(),
            &OnHotKey,
            1,
            ref spec,
            0,
            out nint handlerRef);

        if (status != 0)
        {
            Debug.WriteLine($"[OrielWeb] InstallEventHandler 失败（OSStatus={status}），全局快捷键不可用。");
            return false;
        }

        // 处理器由应用事件目标持有，不需要额外保留 handlerRef（也就不用 release）
        _ = handlerRef;
        _handlerInstalled = true;
        return true;
    }

    private void Raise(uint hotKeyId)
    {
        if (_keysById.TryGetValue(hotKeyId, out string? id))
        {
            Activated?.Invoke(id);
        }
    }

    /// <summary>把规范化键名映射成 macOS 的虚拟键码（与 Windows 的 VK 完全不同，不能共用一张表）。</summary>
    private static bool TryMapKey(string key, out uint keyCode)
    {
        keyCode = 0;

        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return s_letterAndDigitKeys.TryGetValue(key[0], out keyCode);
        }

        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(key[1..], out int functionKey)
            && functionKey is >= 1 and <= 20)
        {
            // 只有 F1–F20 有键码；F21–F24 在标准键盘上不存在
            keyCode = s_functionKeys[functionKey];
            return true;
        }

        return s_namedKeys.TryGetValue(key, out keyCode);
    }

    [UnmanagedCallersOnly]
    private static int OnHotKey(nint callRef, nint eventRef, nint userData)
    {
        try
        {
            var hotKeyId = default(EventHotKeyID);
            int status = GetEventParameter(
                eventRef,
                ParamDirectObject,
                TypeEventHotKeyID,
                0,
                (uint)sizeof(EventHotKeyID),
                0,
                &hotKeyId);

            if (status == 0 && hotKeyId.Signature == Signature)
            {
                s_current?.Raise(hotKeyId.Id);
            }
        }
        catch (Exception ex)
        {
            // 托管异常穿越 Carbon 边界会 fail-fast，绝不能外泄
            Debug.WriteLine($"[OrielWeb] 全局快捷键回调抛出异常：{ex}");
        }

        return 0; // noErr
    }

    /// <summary>当前实例：Carbon 回调是静态函数，只能靠它回到托管侧。</summary>
    private static MacOSGlobalShortcuts? s_current;

    internal MacOSGlobalShortcuts() => s_current = this;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(CarbonFramework)]
    private static partial nint GetApplicationEventTarget();

    [LibraryImport(CarbonFramework)]
    private static partial int RegisterEventHotKey(
        uint hotKeyCode,
        uint hotKeyModifiers,
        EventHotKeyID hotKeyId,
        nint target,
        uint options,
        out nint outRef);

    [LibraryImport(CarbonFramework)]
    private static partial int UnregisterEventHotKey(nint hotKeyRef);

    [LibraryImport(CarbonFramework)]
    private static partial int InstallEventHandler(
        nint target,
        delegate* unmanaged<nint, nint, nint, int> handler,
        uint numTypes,
        ref EventTypeSpec eventTypeList,
        nint userData,
        out nint outRef);

    [LibraryImport(CarbonFramework)]
    private static partial int GetEventParameter(
        nint eventRef,
        uint name,
        uint desiredType,
        nint actualType,
        uint bufferSize,
        nint actualSize,
        void* data);

    /// <summary>字母与数字的 macOS 虚拟键码（ANSI 布局；键码是物理位置而非字符）。</summary>
    private static readonly Dictionary<char, uint> s_letterAndDigitKeys = new()
    {
        ['A'] = 0,
        ['S'] = 1,
        ['D'] = 2,
        ['F'] = 3,
        ['H'] = 4,
        ['G'] = 5,
        ['Z'] = 6,
        ['X'] = 7,
        ['C'] = 8,
        ['V'] = 9,
        ['B'] = 11,
        ['Q'] = 12,
        ['W'] = 13,
        ['E'] = 14,
        ['R'] = 15,
        ['Y'] = 16,
        ['T'] = 17,
        ['1'] = 18,
        ['2'] = 19,
        ['3'] = 20,
        ['4'] = 21,
        ['6'] = 22,
        ['5'] = 23,
        ['9'] = 25,
        ['7'] = 26,
        ['8'] = 28,
        ['0'] = 29,
        ['O'] = 31,
        ['U'] = 32,
        ['I'] = 34,
        ['P'] = 35,
        ['L'] = 37,
        ['J'] = 38,
        ['K'] = 40,
        ['N'] = 45,
        ['M'] = 46,
    };

    private static readonly Dictionary<int, uint> s_functionKeys = new()
    {
        [1] = 122,
        [2] = 120,
        [3] = 99,
        [4] = 118,
        [5] = 96,
        [6] = 97,
        [7] = 98,
        [8] = 100,
        [9] = 101,
        [10] = 109,
        [11] = 103,
        [12] = 111,
        [13] = 105,
        [14] = 107,
        [15] = 113,
        [16] = 106,
        [17] = 64,
        [18] = 79,
        [19] = 80,
        [20] = 90,
    };

    private static readonly Dictionary<string, uint> s_namedKeys = new(StringComparer.Ordinal)
    {
        ["Space"] = 49,
        ["Enter"] = 36,
        ["Tab"] = 48,
        ["Escape"] = 53,
        ["Backspace"] = 51,
        ["Delete"] = 117,
        ["Home"] = 115,
        ["End"] = 119,
        ["PageUp"] = 116,
        ["PageDown"] = 121,
        ["Left"] = 123,
        ["Right"] = 124,
        ["Down"] = 125,
        ["Up"] = 126,
        ["Comma"] = 43,
        ["Period"] = 47,
        ["Slash"] = 44,
        ["Semicolon"] = 41,
        ["Quote"] = 39,
        ["BracketLeft"] = 33,
        ["BracketRight"] = 30,
        ["Backslash"] = 42,
        ["Minus"] = 27,
        ["Equal"] = 24,
        ["Backquote"] = 50,
    };
}
