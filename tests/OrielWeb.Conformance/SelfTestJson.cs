namespace OrielConformance;

/// <summary>
/// 自检脚本用的最小 JSON 拼装。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <c>JsonSerializer</c></b>：它走反射，在 Native AOT 下会给出
/// IL2026/IL3050（demo 就是 AOT 发布的）。自检要拼的都是固定形状的小对象，
/// 手写比引入源生成上下文轻得多。
/// </para>
/// <para>
/// 抽成一处而不是每个自检各写一份：这份文件之前在三处被重复实现过
/// （event / close / window 自检各一份），而"转义规则"这种东西三份必然漂移——
/// 漂移的症状是"某个自检的注入脚本语法错误 → 莫名超时"，指不到根因上。
/// </para>
/// </remarks>
internal static class SelfTestJson
{
    /// <summary>JSON 字符串字面量（含两端引号，内部已转义）。</summary>
    internal static string String(string value) => "\"" + Escape(value) + "\"";

    /// <summary>JSON 布尔字面量。</summary>
    internal static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// JSON 数字字面量。
    /// </summary>
    /// <remarks>
    /// 必须用 <see cref="System.Globalization.CultureInfo.InvariantCulture"/>：
    /// 德语/法语环境下 <c>double.ToString()</c> 会写出 <c>1,5</c>，
    /// 那是**非法 JSON**，而报错会出现在页面侧的 JSON.parse 上。
    /// </remarks>
    internal static string Number(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>拼一个对象：成员值必须是**已经编码好的 JSON 片段**。</summary>
    /// <remarks>
    /// 参数是 (键, 值片段) 而不是 (键, 任意值)：那样调用方必须显式决定怎么编码这个值，
    /// 而"默认调用 ToString()"恰恰是最容易写出非法 JSON 的那条路
    /// （字符串忘了加引号、bool 写成 True、double 用了本地化格式）。
    /// </remarks>
    internal static string Object(params (string Key, string JsonValue)[] members)
    {
        var builder = new System.Text.StringBuilder(64);
        builder.Append('{');

        for (int i = 0; i < members.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(String(members[i].Key)).Append(':').Append(members[i].JsonValue);
        }

        return builder.Append('}').ToString();
    }

    /// <summary>拼一个字符串数组（用于 <c>window.getAll</c> 这类返回值的构造）。</summary>
    internal static string Array(params string[] values)
    {
        var builder = new System.Text.StringBuilder(64);
        builder.Append('[');

        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(String(values[i]));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    /// 最小 JSON 字符串转义。
    /// </summary>
    /// <remarks>
    /// 只处理会让 JSON / 注入脚本语法出错的字符。正常用法下 label 与事件名只含安全字符
    /// （label 被 <c>ValidateLabel</c> 限成 <c>[a-z0-9._-]</c>），但"正常用法"不是不转义的理由：
    /// 漏掉引号转义的表现是注入脚本语法错误，而那种症状指向不到转义上。
    /// </remarks>
    internal static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
