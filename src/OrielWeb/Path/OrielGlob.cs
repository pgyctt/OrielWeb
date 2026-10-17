namespace OrielWeb;

/// <summary>
/// glob 匹配（<c>glob</c> crate 的语义）。
/// </summary>
/// <remarks>
/// <para>
/// 自己写而不是用 <c>Microsoft.Extensions.FileSystemGlobbing</c>：那个包的语义与本库需要的不一样
/// （它是"在一棵目录树里找匹配的文件"，而这里要的是"给定一个具体路径，判断模式认不认它"），
/// 且它对 <c>**</c> 的处理与 <c>glob</c> crate 有差别。scope 是安全判定，
/// 语义上"差一点"就是放行面差一点，所以宁可自己实现并把规则钉在测试里。
/// </para>
/// <para>
/// 规则（照 <c>glob</c> crate）：
/// </para>
/// <list type="bullet">
///   <item><description><c>?</c> 匹配**一个**字符（不含分隔符）；</description></item>
///   <item><description><c>*</c> 匹配**零到多个**字符（不含分隔符）；</description></item>
///   <item><description><c>**</c> 作为**整个路径段**时匹配零到多段（含分隔符）。
///     出现在段中间（如 <c>a**b</c>）时不具特殊含义，等价于 <c>*</c>——<c>glob</c> crate 里
///     那种模式其实是非法的，但把它当普通 <c>*</c> 处理比报错温和，且不会放宽匹配面。</description></item>
///   <item><description><c>[abc]</c> / <c>[a-z]</c> / <c>[!abc]</c> 字符集；</description></item>
///   <item><description><c>{a,b}</c> 择一。</description></item>
/// </list>
/// <para>
/// 匹配前路径与模式都已经被归一过（分隔符统一为 <c>/</c>、压缩重复、去掉尾随）。见
/// <see cref="OrielPathScope"/>。
/// </para>
/// </remarks>
internal static class OrielGlob
{
    /// <summary>判断一个已归一的路径是否匹配一个已归一的模式。</summary>
    /// <remarks>
    /// 按**路径段**递归匹配，而不是逐字符扫描：<c>**</c> 的语义是"零到多段"，
    /// 逐字符实现要处理"回溯到哪里"的问题，很容易写出在某个组合上出错的版本。
    /// 分段之后 <c>**</c> 就是"吃掉 k 段"，语义直白。
    /// </remarks>
    internal static bool IsMatch(string pattern, string path)
    {
        // 先展开 {a,b}：展开成多条模式，任一条匹配即可。
        // 放在分段之前做，因为 {} 可以跨越段边界（`$HOME/{a,b}/x`）。
        foreach (string expanded in ExpandAlternation(pattern))
        {
            if (MatchSegments(
                expanded.Split('/', StringSplitOptions.RemoveEmptyEntries),
                path.Split('/', StringSplitOptions.RemoveEmptyEntries)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>段级递归匹配。</summary>
    private static bool MatchSegments(string[] pattern, string[] path)
        => MatchSegments(pattern, 0, path, 0);

    private static bool MatchSegments(string[] pattern, int pi, string[] path, int si)
    {
        while (true)
        {
            // 模式走完了：路径也必须走完
            if (pi == pattern.Length)
            {
                return si == path.Length;
            }

            // `**`：吃掉 0..n 段。**吃 0 段**这一支必须试——
            // `$HOME/**/*.txt` 要能匹配 `$HOME/a.txt`（中间零层目录）。
            if (pattern[pi] == "**")
            {
                for (int skip = 0; si + skip <= path.Length; skip++)
                {
                    if (MatchSegments(pattern, pi + 1, path, si + skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            // 路径走完了但模式还有（且不是 `**`）——不匹配
            if (si == path.Length)
            {
                return false;
            }

            if (!MatchSegment(pattern[pi], path[si]))
            {
                return false;
            }

            pi++;
            si++;
        }
    }

    /// <summary>单段匹配（支持 <c>*</c> / <c>?</c> / <c>[...]</c>）。</summary>
    /// <remarks>
    /// 用经典的回溯算法（两个指针 + 记住上一个 <c>*</c> 的位置）。这是线性空间、
    /// 最坏 O(n·m) 的做法——对路径这种长度完全够，而且比正则少一个"模式里的特殊字符
    /// 在正则里也有特殊含义"的转义陷阱。
    /// </remarks>
    private static bool MatchSegment(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int starPattern = -1;
        int starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                // 记下这个 `*` 的位置，后面失配时回到这里让它多吃一个字符
                starPattern = p;
                starText = t;
                p++;
                continue;
            }

            if (p < pattern.Length && MatchesOne(pattern, ref p, text[t]))
            {
                t++;
                continue;
            }

            if (starPattern >= 0)
            {
                // 回退：让上一个 `*` 多吃一个字符
                starText++;
                t = starText;
                p = starPattern + 1;
                continue;
            }

            return false;
        }

        // 文本走完了：模式剩下的必须全是 `*`
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>匹配单个字符；会推进 <paramref name="p"/>（字符集要跨多个字符）。</summary>
    private static bool MatchesOne(string pattern, ref int p, char c)
    {
        char pc = pattern[p];

        if (pc == '?')
        {
            p++;
            return true;
        }

        if (pc != '[')
        {
            p++;
            return OrdinalEquals(pc, c);
        }

        // 字符集：[abc] / [a-z] / [!abc] / [^abc]
        int index = p + 1;
        bool negate = index < pattern.Length && pattern[index] is '!' or '^';

        if (negate)
        {
            index++;
        }

        bool matched = false;

        // 首个 ']' 是字面量（POSIX 的规矩，glob crate 也照它）；
        // 这里用一个"还没匹配过任何字符"的标志来近似——够用且不会误判常见写法。
        bool first = true;

        while (index < pattern.Length && (pattern[index] != ']' || first))
        {
            first = false;

            // 范围 a-z：要保证 '-' 两边都有字符，且 '-' 不是区间最后一个
            if (index + 2 < pattern.Length && pattern[index + 1] == '-' && pattern[index + 2] != ']')
            {
                char low = pattern[index];
                char high = pattern[index + 2];
                if (c >= low && c <= high)
                {
                    matched = true;
                }

                index += 3;
                continue;
            }

            if (OrdinalEquals(pattern[index], c))
            {
                matched = true;
            }

            index++;
        }

        if (index >= pattern.Length)
        {
            // 没找到收尾的 ']'：把 '[' 当字面量
            p++;
            return OrdinalEquals('[', c);
        }

        p = index + 1;
        return matched != negate;
    }

    private static bool OrdinalEquals(char a, char b)
        => OrielPathScope.CaseSensitive ? a == b : char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    // ---------------------------------------------------------------- {a,b}

    /// <summary>
    /// 展开 <c>{a,b}</c> 择一：返回所有组合。
    /// </summary>
    /// <remarks>
    /// 递归展开第一个花括号，直到没有可展开的。嵌套（<c>{a,{b,c}}</c>）也支持；
    /// 花括号不配对时原样返回（当字面量），不报错——scope 是配置里的字符串，
    /// 一个手误的花括号不该让应用启动不了，而"不匹配"这个结果本身是安全的（拒绝）。
    /// </remarks>
    private static List<string> ExpandAlternation(string pattern)
    {
        int open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            return [pattern];
        }

        // 找配对的 '}'（要跨过嵌套）
        int depth = 0;
        int close = -1;
        for (int i = open; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                depth++;
            }
            else if (pattern[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0)
        {
            return [pattern];
        }

        string prefix = pattern[..open];
        string body = pattern[(open + 1)..close];
        string suffix = pattern[(close + 1)..];

        // 只在**顶层**逗号处切分：`{a,{b,c}}` 要切成 `a` 与 `{b,c}` 两段
        var results = new List<string>();
        int segmentStart = 0;
        depth = 0;

        for (int i = 0; i <= body.Length; i++)
        {
            bool atEnd = i == body.Length;

            if (!atEnd && body[i] == '{')
            {
                depth++;
            }
            else if (!atEnd && body[i] == '}')
            {
                depth--;
            }
            else if (atEnd || (body[i] == ',' && depth == 0))
            {
                string alternative = body[segmentStart..i];

                // 每个替代项自己还可能含花括号，所以递归展开前缀与后缀
                foreach (string expandedAlternative in ExpandAlternation(prefix + alternative + suffix))
                {
                    results.Add(expandedAlternative);
                }

                segmentStart = i + 1;
            }
        }

        return results;
    }
}
