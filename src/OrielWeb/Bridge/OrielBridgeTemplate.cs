using System.Globalization;
using System.Text;

namespace OrielWeb;

/// <summary>
/// 三平台共用桥接脚本模板（<c>Bridge/oriel-bridge.js</c>，EmbeddedResource）。
/// 模板在首次使用时读取一次，之后每个平台只做三次字符串替换（建窗口时一次，
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
    private const string ScaleToken = "__ORIEL_SCALE__";

    private static readonly string s_source = LoadSource();

    /// <summary>
    /// 生成指定平台的注入脚本。
    /// </summary>
    /// <param name="platformLiteral">platform 字段的字面量，含引号，如 <c>'windows'</c>。</param>
    /// <param name="postExpression">投递表达式，其形参名固定为 <c>obj</c>。</param>
    /// <param name="forwardConsole">是否启用 console 转发（见 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。</param>
    /// <param name="token">本次进程运行的 IPC 令牌（见 <see cref="Ipc.OrielIpcToken"/>）。</param>
    /// <param name="trustedPrefixes">可信来源的 URL 前缀（脚本据此决定要不要安装）。</param>
    /// <param name="system">宿主事实快照（页面同步可读，见 <see cref="OrielSystemSnapshot"/>）。</param>
    /// <remarks>
    /// console 转发的实现只存在于模板里（未启用时代码保留但不执行），C# 侧不再抄一份：
    /// 三份手抄脚本互相漂移正是这个模板要消灭的问题。
    /// </remarks>
    internal static string Create(
        string platformLiteral,
        string postExpression,
        bool forwardConsole,
        string token,
        IReadOnlyList<string> trustedPrefixes,
        OrielSystemSnapshot system)
        => s_source
            .Replace(PlatformToken, platformLiteral, StringComparison.Ordinal)
            .Replace(PostToken, postExpression, StringComparison.Ordinal)
            .Replace(ConsoleEnabledToken, forwardConsole ? "true" : "false", StringComparison.Ordinal)
            .Replace(VersionToken, VersionLiteral, StringComparison.Ordinal)
            .Replace(SecurityToken, token, StringComparison.Ordinal)
            .Replace(TrustedToken, TrustedLiteral(trustedPrefixes), StringComparison.Ordinal)
            .Replace(DoubleClickToken, NumberLiteral(system.DoubleClickTimeMs), StringComparison.Ordinal)
            .Replace(ScaleToken, NumberLiteral(system.Scale), StringComparison.Ordinal);

    /// <summary>数值 → JS 数值字面量。</summary>
    /// <remarks>
    /// 必须显式用 <see cref="CultureInfo.InvariantCulture"/>：按当前文化格式化会把小数点写成逗号
    /// （德语、法语等），生成的脚本就成了 <c>scale: 1,5</c>——在 JS 里那是逗号表达式，
    /// 要么语法错误要么静默变成 1，"注入脚本坏了"的表现还是"页面功能全无"。
    /// <para>
    /// 用 "R"（往返格式）而不是 "0.###"：后者会把极大/极小的值四舍五入掉，而这个方法的入参
    /// 已经是规整过的（见 <see cref="OrielSystemSnapshot.Normalize"/>），格式化只负责不引入新的失真。
    /// 非有限值（NaN/Infinity）理论上到不了这里，真到了就回退成 1——写进脚本的 NaN 是语法错误，
    /// 而那会让整个桥接脚本加载失败。
    /// </para>
    /// </remarks>
    internal static string NumberLiteral(double value)
        => double.IsFinite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : "1";

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
