using System.Security.Cryptography;
using System.Text;

namespace OrielWeb.Ipc;

/// <summary>
/// 每个应用实例生成一次的 IPC 令牌（同一个进程里创建多个 <see cref="OrielApp"/> 时各有一份，
/// 比"每进程一个"更严）。
/// </summary>
/// <remarks>
/// <para>
/// 防的是这一件事：**不是这个应用自己注入的桥接脚本，也能往 webview 的消息通道里塞消息。**
/// 令牌随桥接脚本注入页面，之后每条入站消息都带上它；宿主侧不匹配即丢弃。
/// </para>
/// <para>
/// 它**不是**防网络攻击的凭据——消息不经过网络，攻击者要拿到令牌得先能在这个进程的地址空间里
/// 执行代码，那时令牌已经不重要了。所以这里不做过期、不做刷新、也不做按文档轮换，
/// 只做"每个应用实例一个、够随机、比较固定时长"。
/// </para>
/// </remarks>
internal static class OrielIpcToken
{
    /// <summary>令牌的十六进制字符数（16 字节 = 128 位）。</summary>
    internal const int Length = 32;

    /// <summary>生成一个新令牌。</summary>
    internal static string Generate() => RandomNumberGenerator.GetHexString(Length, lowercase: true);

    /// <summary>
    /// 比较两个令牌。空值、长度不同、内容不同一律返回 false。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="CryptographicOperations.FixedTimeEquals"/> 而不是 <c>==</c>：
    /// 前者不会因为"第一个字符就不同"而提前返回，比较耗时与匹配程度无关。
    /// 这一处本不构成真实威胁（见类说明），但固定时长比较没有代价，
    /// 省下的是"将来有人把它当成凭据复用"这个隐患。
    /// </remarks>
    internal static bool Equals(string? expected, string? presented)
    {
        if (expected is null || presented is null || expected.Length != presented.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
    }

    /// <summary>令牌形如 32 位小写十六进制。用于自检断言"生成的确实是随机串而不是占位符"。</summary>
    internal static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != Length)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
