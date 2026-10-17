using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// scope 里可以用的路径变量（<c>$APPDATA</c> / <c>$HOME</c> / …）。
/// </summary>
/// <remarks>
/// <para>
/// <b>名字完全照抄 Tauri</b>：每个变量对应 <c>BaseDirectory</c> 的一个取值，拼写也一致
/// （<c>$APPDATA</c> 而不是 <c>$AppData</c>、<c>$APPLOCALDATA</c> 而不是 <c>$AppLocalData</c>）。
/// 照抄的好处很实际：迁移时能力配置基本能逐行搬，而"变量名要改一遍"是最容易漏的地方
/// （漏了不会报错，只会让那条 scope 永远不匹配——也就是永远拒绝）。
/// </para>
/// <para>
/// 变量名的大小写**不敏感**（<c>$appdata</c> 也能用），因为配置文件里的大小写笔误
/// 后果是"静默不匹配"，而那正是最难查的一类。Tauri 是区分大小写的，这里刻意放宽。
/// </para>
/// </remarks>
public static class OrielPathVariables
{
    /// <summary>变量名（不含 <c>$</c>）→ 对应的 <c>BaseDirectory</c> 取值。</summary>
    /// <remarks>
    /// 这份映射与 <see cref="OrielKnownDirs.AllDirectories"/> 是一一对应的：
    /// 每个目录一个变量。有测试断言两个集合互不漏。
    /// </remarks>
    private static readonly Dictionary<string, string> s_variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$AUDIO"] = "Audio",
        ["$CACHE"] = "Cache",
        ["$CONFIG"] = "Config",
        ["$DATA"] = "Data",
        ["$LOCALDATA"] = "LocalData",
        ["$DOCUMENT"] = "Document",
        ["$DOWNLOAD"] = "Download",
        ["$PICTURE"] = "Picture",
        ["$PUBLIC"] = "Public",
        ["$VIDEO"] = "Video",
        ["$RESOURCE"] = "Resource",
        ["$TEMP"] = "Temp",
        ["$APPCONFIG"] = "AppConfig",
        ["$APPDATA"] = "AppData",
        ["$APPLOCALDATA"] = "AppLocalData",
        ["$APPCACHE"] = "AppCache",
        ["$APPLOG"] = "AppLog",
        ["$DESKTOP"] = "Desktop",
        ["$EXE"] = "Executable",
        ["$FONT"] = "Font",
        ["$HOME"] = "Home",
        ["$RUNTIME"] = "Runtime",
        ["$TEMPLATE"] = "Template",
    };

    /// <summary>全部变量名（含 <c>$</c>），按字典序。</summary>
    public static IReadOnlyList<string> All { get; } = [.. s_variables.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// 展开模式里的第一个 <c>$VAR</c>；模式里没有变量时原样返回。
    /// </summary>
    /// <returns>
    /// 展开后的模式；变量名不认识、或该变量在当前平台上解析不出来时返回 <c>null</c>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>解析不出来时返回 null 而不是"保留字面量"</b>：调用方据此保守拒绝。
    /// 保留字面量的后果是把 <c>$APPDTA/**</c>（拼错了）当成"匹配名为 <c>$APPDTA</c> 的目录"，
    /// 而那台机器上万一真有这个目录，拼错的 scope 就成了意外的放行。
    /// </para>
    /// <para>
    /// 只展开**开头**的那一个变量。Tauri 也只支持前缀形式（<c>$HOME/**</c>），
    /// 而中间嵌变量（<c>/a/$HOME/b</c>）没有意义——scope 的用途是"从某个已知根出发"。
    /// 开头要是没有变量，模式就是一个绝对路径，照字面匹配。
    /// </para>
    /// </remarks>
    internal static string? Expand(string pattern, string appIdentifier)
    {
        if (!pattern.StartsWith('$'))
        {
            return pattern;
        }

        // 变量名到第一个分隔符为止（`$HOME` 后面可能直接跟 `/`，也可能什么都没有）
        int end = pattern.IndexOfAny(['/', '\\']);
        string name = end < 0 ? pattern : pattern[..end];
        string rest = end < 0 ? string.Empty : pattern[end..];

        // `$APPLOCALDATA/**` 这类：名字本身可能就是整段（end < 0），也可能后面还跟着的东西
        // 属于同一个名字的一部分（`$APPDATAX/**` 里的 `$APPDATAX` 是个不认识的变量）。
        if (!s_variables.TryGetValue(name, out string? directory))
        {
            // 名字里可能混进了非变量字符（如 `$HOMEbackup`）——那是个不认识的名字，
            // 不是"变量 + 后缀"。所以直接把整段当名字查表，查不到就失败。
            return null;
        }

        if (!OrielKnownDirs.IsSupported(directory, appIdentifier))
        {
            // 变量对应的目录在当前平台上不存在（如 Windows 上的 $RUNTIME）。
            // 这条 scope 永远不能匹配——如实失败比"用一个空串拼出 /xxx"好。
            return null;
        }

        return OrielKnownDirs.Resolve(directory, appIdentifier) + rest;
    }

    /// <summary>取一个变量名对应的目录（给诊断与测试用）；不认识时返回 null。</summary>
    internal static string? DirectoryOf(string variable)
        => s_variables.TryGetValue(variable, out string? directory) ? directory : null;
}
