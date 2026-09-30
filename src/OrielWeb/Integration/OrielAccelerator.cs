using System.Globalization;
using System.Text;

namespace OrielWeb;

/// <summary>
/// 加速键（如 <c>"CmdOrCtrl+Shift+A"</c>）的解析结果。
/// </summary>
/// <remarks>
/// <para>
/// 解析与平台无关：这里只给"抽象键名 + 修饰键集合"，各平台自己映射到原生表示
/// （Windows 虚拟键码、macOS keyCode + modifier mask、X11 keycode）。
/// 应用菜单的加速键与全局快捷键共用本类型——它们是同一套语法，没有理由写两个解析器。
/// </para>
/// <para>
/// 语法：<c>修饰键+修饰键+…+键名</c>。修饰键可写 <c>Ctrl</c>/<c>Control</c>、
/// <c>Alt</c>/<c>Option</c>、<c>Shift</c>、<c>Cmd</c>/<c>Command</c>/<c>Super</c>/<c>Win</c>
/// 与 <c>CmdOrCtrl</c>（macOS 上是 Command、其它平台是 Control，用于写跨平台快捷键）。
/// 修饰键必须排在键名之前，顺序任意、重复无副作用。键名支持字母、数字、<c>F1</c>–<c>F24</c>
/// 与一组具名键（<c>Space</c>/<c>Enter</c>/<c>Tab</c>/<c>Escape</c>/<c>Backspace</c>/<c>Delete</c>/
/// <c>Insert</c>/<c>Home</c>/<c>End</c>/<c>PageUp</c>/<c>PageDown</c>/方向键/常见标点名）。
/// </para>
/// </remarks>
public sealed class OrielAccelerator
{
    private OrielAccelerator(bool control, bool alt, bool shift, bool command, string key)
    {
        Control = control;
        Alt = alt;
        Shift = shift;
        Command = command;
        Key = key;
    }

    /// <summary>Ctrl（macOS 上是 Control，注意它<em>不是</em> Command）。</summary>
    public bool Control { get; }

    /// <summary>Alt（macOS 上是 Option）。</summary>
    public bool Alt { get; }

    public bool Shift { get; }

    /// <summary>macOS 的 Command、Windows 的 Win 键、X11 的 Super。</summary>
    public bool Command { get; }

    /// <summary>规范化后的键名：字母与数字为大写（<c>"A"</c>/<c>"7"</c>）、<c>"F1"</c>–<c>"F24"</c>、或具名键（<c>"Space"</c>/<c>"PageDown"</c>…）。</summary>
    public string Key { get; }

    /// <summary>解析加速键语法（按当前平台解释 <c>CmdOrCtrl</c>）。</summary>
    public static bool TryParse(string? text, out OrielAccelerator? accelerator)
        => TryParse(text, OperatingSystem.IsMacOS(), requireModifier: false, out accelerator);

    /// <summary>
    /// 本平台的展示文本。macOS 用符号形式（<c>⌃⌥⇧⌘A</c>），其它平台用 <c>Ctrl+Shift+A</c> 形式。
    /// 只用于显示；实际按键行为由各平台的原生字段决定。
    /// </summary>
    public string DisplayString => ToDisplayString(OperatingSystem.IsMacOS());

    /// <summary>指定平台的展示文本（<paramref name="isMac"/> 为 true 时用 macOS 符号形式）。</summary>
    public string ToDisplayString(bool isMac)
    {
        if (isMac)
        {
            var symbols = new StringBuilder(5);
            if (Control)
            {
                symbols.Append('⌃');
            }
            if (Alt)
            {
                symbols.Append('⌥');
            }
            if (Shift)
            {
                symbols.Append('⇧');
            }
            if (Command)
            {
                symbols.Append('⌘');
            }
            return symbols.Append(Key).ToString();
        }

        var parts = new List<string>(5);
        if (Control)
        {
            parts.Add("Ctrl");
        }
        if (Alt)
        {
            parts.Add("Alt");
        }
        if (Shift)
        {
            parts.Add("Shift");
        }
        if (Command)
        {
            parts.Add("Win");
        }
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>
    /// 与平台无关的规范化串（<c>Ctrl+Shift+A</c> 形式），用于注册表去重：
    /// <c>"ctrl+a"</c> 与 <c>"CmdOrCtrl+A"</c> 在非 macOS 上是同一个组合。
    /// </summary>
    internal string ToCanonicalString() => ToDisplayString(isMac: false);

    /// <summary>
    /// 解析实现。<paramref name="isMac"/> 决定 <c>CmdOrCtrl</c> 落到 Command 还是 Control
    /// （显式传入而非读环境，测试才能两个平台都覆盖）；<paramref name="requireModifier"/>
    /// 用于全局快捷键——没有修饰键的全局组合会吞掉正常打字，
    /// 菜单里的加速键则可以只有功能键（如 <c>"F5"</c>）。
    /// </summary>
    internal static bool TryParse(string? text, bool isMac, bool requireModifier, out OrielAccelerator? accelerator)
    {
        accelerator = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+');
        bool control = false;
        bool alt = false;
        bool shift = false;
        bool command = false;
        string? key = null;

        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            // 空段（"Ctrl+" 的尾巴、连续加号）一律视为非法，不做宽容解析：
            // 静默忽略拼错的修饰键会让用户拿到一个和预期不同的快捷键。
            if (token.Length == 0)
            {
                return false;
            }

            bool isLast = i == parts.Length - 1;
            switch (Classify(token))
            {
                case Modifier.Control:
                    if (isLast)
                    {
                        return false; // "Ctrl+Shift" 只有修饰键、没有键名
                    }
                    control = true;
                    continue;
                case Modifier.Alt:
                    if (isLast)
                    {
                        return false;
                    }
                    alt = true;
                    continue;
                case Modifier.Shift:
                    if (isLast)
                    {
                        return false;
                    }
                    shift = true;
                    continue;
                case Modifier.Command:
                    if (isLast)
                    {
                        return false;
                    }
                    command = true;
                    continue;
                case Modifier.CommandOrControl:
                    if (isLast)
                    {
                        return false;
                    }
                    if (isMac)
                    {
                        command = true;
                    }
                    else
                    {
                        control = true;
                    }
                    continue;
            }

            // 非修饰键：必须是最后一段，且只能有一个（"Ctrl+A+B" 不合法）
            if (!isLast || key is not null || !TryNormalizeKey(token, out key))
            {
                return false;
            }
        }

        if (key is null || (requireModifier && !control && !alt && !shift && !command))
        {
            return false;
        }

        accelerator = new OrielAccelerator(control, alt, shift, command, key);
        return true;
    }

    private enum Modifier { None, Control, Alt, Shift, Command, CommandOrControl }

    private static Modifier Classify(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => Modifier.Control,
        "alt" or "option" => Modifier.Alt,
        "shift" => Modifier.Shift,
        "cmd" or "command" or "super" or "win" or "meta" => Modifier.Command,
        "cmdorctrl" or "commandorcontrol" => Modifier.CommandOrControl,
        _ => Modifier.None,
    };

    /// <summary>把键名规范化为大写字母/数字、<c>F1</c>–<c>F24</c> 或具名键；认不出就失败。</summary>
    private static bool TryNormalizeKey(string token, out string key)
    {
        key = string.Empty;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (char.IsAsciiLetterOrDigit(c))
            {
                key = c.ToString();
                return true;
            }
            return false;
        }

        if (token.Length <= 3
            && (token[0] is 'F' or 'f')
            && int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int fn)
            && fn is >= 1 and <= 24)
        {
            key = "F" + fn.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (s_namedKeys.TryGetValue(token, out string? named))
        {
            key = named;
            return true;
        }

        return false;
    }

    private static readonly Dictionary<string, string> s_namedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "Space",
        ["Enter"] = "Enter",
        ["Return"] = "Enter",
        ["Tab"] = "Tab",
        ["Escape"] = "Escape",
        ["Esc"] = "Escape",
        ["Backspace"] = "Backspace",
        ["Back"] = "Backspace",
        ["Delete"] = "Delete",
        ["Del"] = "Delete",
        ["Insert"] = "Insert",
        ["Ins"] = "Insert",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PageUp",
        ["PgUp"] = "PageUp",
        ["PageDown"] = "PageDown",
        ["PgDn"] = "PageDown",
        ["Up"] = "Up",
        ["Down"] = "Down",
        ["Left"] = "Left",
        ["Right"] = "Right",
        ["Comma"] = "Comma",
        ["Period"] = "Period",
        ["Slash"] = "Slash",
        ["Semicolon"] = "Semicolon",
        ["Quote"] = "Quote",
        ["BracketLeft"] = "BracketLeft",
        ["BracketRight"] = "BracketRight",
        ["Backslash"] = "Backslash",
        ["Minus"] = "Minus",
        ["Equal"] = "Equal",
        ["Backquote"] = "Backquote",
    };
}
