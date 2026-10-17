using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 库自己产生的错误码。
/// </summary>
/// <remarks>
/// <para>
/// 页面按 <c>error.code</c> 分支时应当用这些常量，而不是硬编码字符串——
/// 码值一旦定下就是契约（改了会静默地让页面的分支走错）。
/// </para>
/// <para>
/// <b>应用自己的命令不需要用这里的码</b>：它们可以抛 <see cref="OrielIpcException"/>
/// 并带上自己的 <see cref="OrielIpcException.Code"/>。这份清单只覆盖
/// "库替应用挡下来的那些失败"——分不清"命令没授权"与"命令自己失败了"，
/// 是排障时最费时间的一件事。
/// </para>
/// </remarks>
public static class OrielIpcErrorCodes
{
    /// <summary>命令名不在能力名单里（被门禁拒绝）。</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>命令名未注册（拼错、或所在程序集没被应用引用）。</summary>
    public const string UnknownCommand = "unknown_command";

    /// <summary>缺少必需参数。</summary>
    public const string MissingArgument = "missing_argument";

    /// <summary>参数存在但类型或取值不合法。</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>消息本身不合法（不是对象、缺 <c>name</c> 字段等）。</summary>
    public const string InvalidMessage = "invalid_message";

    /// <summary>按 label 找不到目标窗口（已关闭或 label 拼错）。</summary>
    public const string TargetNotFound = "target_not_found";

    /// <summary>这次调用缺少来源窗口，而该命令必须有窗口才能执行。</summary>
    public const string NoSourceWindow = "no_source_window";

    /// <summary>库内部的意外失败（**不是**应用命令自己抛的异常）。</summary>
    /// <remarks>
    /// 应用命令抛的异常不带码——那是应用的错误分类，库不该替它编。
    /// 这个码留给"库的实现出了问题"那一类：它意味着该报一个库的缺陷。
    /// </remarks>
    public const string Internal = "internal";
}

/// <summary>
/// IPC 层错误（参数缺失、类型不符、命令未知等）。消息会回传给 JS 侧的 Promise reject。
/// </summary>
/// <remarks>
/// <para>
/// <b>消息原样到页面，所以不要往里面塞内部路径或堆栈</b>——页面是可信的，
/// 但文案会进用户可见的错误提示。库自己抛的那些只写"是什么错、怎么改"。
/// </para>
/// <para>
/// <see cref="Code"/> 与 <see cref="Details"/> 都是可选的：给了就随回执一起到页面
/// （页面侧的 <c>error.code</c> / <c>error.data</c>），页面据此分支；不给就是纯文案。
/// 用对象初始化器写：
/// <c>throw new OrielIpcException("…") { Code = OrielIpcErrorCodes.TargetNotFound }</c>。
/// </para>
/// </remarks>
public class OrielIpcException : Exception
{
    public OrielIpcException(string message) : base(message) { }

    public OrielIpcException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// 机器可判别的错误码；<c>null</c> 表示没有分类（页面只能看文案）。
    /// </summary>
    /// <remarks>
    /// 用 <c>init</c> 而不是构造器参数：绝大多数抛出点只关心文案，
    /// 强制每个都传码会让"这个错误算什么类"变成一个随手填的值——
    /// 而随手填的码比没有码更糟（页面会按它分支，然后走进一条错的路径）。
    /// </remarks>
    public string? Code { get; init; }

    /// <summary>
    /// 附加数据（任意 JSON）。
    /// </summary>
    /// <remarks>
    /// 类型是 <see cref="JsonElement"/> 而不是 <c>object</c>：回执要按 AOT 安全的方式写出，
    /// 而 <see cref="JsonElement"/> 正是文档里那个"不需要应用注册 STJ 上下文"的逃生口
    /// （见 <see cref="OrielJson.WriteResult"/>）。用 <c>object</c> 就得走反射。
    /// </remarks>
    public JsonElement? Details { get; init; }
}
