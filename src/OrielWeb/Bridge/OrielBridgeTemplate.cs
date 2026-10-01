using System.Globalization;
using System.Text;

namespace OrielWeb;

/// <summary>
/// 三平台共用桥接脚本模板（<c>Bridge/oriel-bridge.js</c>，EmbeddedResource）。
/// 模板在首次使用时读取一次，之后每个平台只做几次字符串替换（建窗口时一次，
/// 成本可忽略），从而彻底消除"三份手抄脚本"带来的缺陷同步传播。
/// </summary>
internal static class OrielBridgeTemplate
{
    private const string ResourceName = "OrielWeb.Bridge.oriel-bridge.js";
    private const string PlatformToken = "__ORIEL_PLATFORM__";
    private const string PostToken = "__ORIEL_POST__";
    private const string ConsoleEnabledToken = "__ORIEL_CONSOLE_ENABLED__";
    private const string VersionToken = "__ORIEL_VERSION__";
    private const string SecurityToken = "__ORIEL_TOKEN__";
    private const string TrustedToken = "__ORIEL_TRUSTED__";
    private const string DoubleClickToken = "__ORIEL_DOUBLE_CLICK_MS__";
    private const string DragThresholdToken = "__ORIEL_DRAG_THRESHOLD_PX__";
    private const string DragSelectorToken = "__ORIEL_DRAG_SELECTOR__";

    private static readonly string s_source = LoadSource();

    /// <summary>
    /// 生成指定平台的注入脚本。
    /// </summary>
    /// <param name="platformLiteral">platform 字段的字面量，含引号，如 <c>'windows'</c>。</param>
    /// <param name="postExpression">投递表达式，其形参名固定为 <c>obj</c>。</param>
    /// <param name="forwardConsole">是否启用 console 转发（见 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。</param>
    /// <param name="token">本次进程运行的 IPC 令牌（见 <see cref="Ipc.OrielIpcToken"/>）。</param>
    /// <param name="trustedPrefixes">可信来源的 URL 前缀（脚本据此决定要不要安装）。</param>
    /// <param name="system">拖动实现需要的宿主事实（见 <see cref="OrielSystemSnapshot"/>）。</param>
    /// <param name="dragSelector">宿主指定的拖动区域选择器；null/空表示只认 <c>data-oriel-drag-region</c> 属性。</param>
    /// <remarks>
    /// console 转发与拖动实现都只存在于模板里，C# 侧不再抄一份：
    /// 三份手抄脚本互相漂移正是这个模板要消灭的问题。
    /// </remarks>
    internal static string Create(
        string platformLiteral,
        string postExpression,
        bool forwardConsole,
        string token,
        IReadOnlyList<string> trustedPrefixes,
        OrielSystemSnapshot system,
        string? dragSelector)
        => s_source
            .Replace(PlatformToken, platformLiteral, StringComparison.Ordinal)
            .Replace(PostToken, postExpression, StringComparison.Ordinal)
            .Replace(ConsoleEnabledToken, forwardConsole ? "true" : "false", StringComparison.Ordinal)
            .Replace(VersionToken, VersionLiteral, StringComparison.Ordinal)
            .Replace(SecurityToken, token, StringComparison.Ordinal)
            .Replace(TrustedToken, TrustedLiteral(trustedPrefixes), StringComparison.Ordinal)
            .Replace(DoubleClickToken, NumberLiteral(system.DoubleClickTimeMs), StringComparison.Ordinal)
            .Replace(DragThresholdToken, NumberLiteral(OrielSystemSnapshot.DragThresholdPx), StringComparison.Ordinal)
            .Replace(DragSelectorToken, SelectorLiteral(dragSelector), StringComparison.Ordinal);

    /// <summary>
    /// 可信来源前缀 → JS 数组字面量。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="JsonText.EncodeString"/> 而不是拼引号：前缀里可能含单引号、
    /// 反斜杠或非 ASCII 字符（Windows 路径、中文目录名），手拼会在这些输入上生成
    /// 语法错误的脚本——而注入脚本报错的表现是"页面功能全无且控制台只有一句 SyntaxError"。
    /// </remarks>
    internal static string TrustedLiteral(IReadOnlyList<string> prefixes)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < prefixes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(JsonText.EncodeString(prefixes[i]));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>拖动区域选择器 → JS 字符串字面量（空选择器写作空串，脚本按"只认属性"处理）。</summary>
    private static string SelectorLiteral(string? selector)
        => string.IsNullOrWhiteSpace(selector) ? "''" : JsonText.EncodeString(selector);

    /// <summary>
    /// 数值 → JS 数字字面量。
    /// </summary>
    /// <remarks>
    /// **必须用 InvariantCulture**：按当前文化格式化会在逗号做小数点的区域写出别的形态，
    /// 而注入脚本里出现一个语法错误的表现是"页面功能全无、控制台只有一句 SyntaxError"。
    /// NaN/Infinity 在 JS 里不是字面量，这里显式挡住（调用方本不该传，这是最后一道）。
    /// </remarks>
    internal static string NumberLiteral(double value)
    {
        if (!double.IsFinite(value))
        {
            return "1";
        }

        return value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 注入给页面的库版本：程序集版本的前三段（<c>Major.Minor.Build</c>）。
    /// </summary>
    /// <remarks>
    /// 以前这里是脚本里写死的 <c>'0.1.0'</c>，与包版本各自漂移——而 <c>oriel.version</c> 正是给页面
    /// 做能力探测用的，一个恒定的旧版本号会静默地误导所有基于它的兼容判断。改为从程序集读，
    /// 版本号就只有一个来源（csproj 的 <c>&lt;Version&gt;</c>）。
    /// </remarks>
    private static string VersionLiteral
    {
        get
        {
            Version? version = typeof(OrielBridgeTemplate).Assembly.GetName().Version;
            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }

    private static string LoadSource()
    {
        using var stream = typeof(OrielBridgeTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"未能加载内嵌桥接脚本资源 '{ResourceName}'。请检查 OrielWeb.csproj 的 EmbeddedResource 配置。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
