using System.Text;

namespace OrielWeb;

/// <summary>
/// 三平台共用桥接脚本模板（<c>Bridge/oriel-bridge.js</c>，EmbeddedResource）。
/// 模板在首次使用时读取一次，之后每个平台只做两次字符串替换（建窗口时一次，
/// 成本可忽略），从而彻底消除"三份手抄脚本"带来的缺陷同步传播。
/// </summary>
internal static class OrielBridgeTemplate
{
    private const string ResourceName = "OrielWeb.Bridge.oriel-bridge.js";
    private const string PlatformToken = "__ORIEL_PLATFORM__";
    private const string PostToken = "__ORIEL_POST__";

    private static readonly string s_source = LoadSource();

    /// <summary>
    /// 生成指定平台的注入脚本。
    /// </summary>
    /// <param name="platformLiteral">platform 字段的字面量，含引号，如 <c>'windows'</c>。</param>
    /// <param name="postExpression">投递表达式，其形参名固定为 <c>obj</c>。</param>
    internal static string Create(string platformLiteral, string postExpression)
        => s_source
            .Replace(PlatformToken, platformLiteral, StringComparison.Ordinal)
            .Replace(PostToken, postExpression, StringComparison.Ordinal);

    private static string LoadSource()
    {
        using var stream = typeof(OrielBridgeTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"未能加载内嵌桥接脚本资源 '{ResourceName}'。请检查 OrielWeb.csproj 的 EmbeddedResource 配置。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
