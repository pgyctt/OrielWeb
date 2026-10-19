using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 命令目标窗口的解析规则（<c>label</c> 缺省时是**发起者**）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成独立文件是因为它现在跨两个命令族使用：<c>window.*</c>（按 label 操作别的窗口）与
/// <c>menu.*</c>（菜单弹到哪个窗口上）。两条族各抄一份的话，"label 缺省 = 发起者"这条规则
/// 迟早会在其中一边漂移——而它的错误表现是静默的：命令作用到了错误的窗口上。
/// </para>
/// <para>
/// 两种情况都走 <c>App.GetWindow(label)</c> 查表，**包括缺省时的发起者自己**。
/// 这不只是图省事——查表顺带保证了目标是个**已注册的存活窗口**：
/// 窗口在关闭流程中（已从表里摘掉但对象还在）时，命令会明确报错而不是作用到半个窗口上。
/// </para>
/// </remarks>
internal static class OrielCommandTarget
{
    /// <summary>解析命令的目标窗口。</summary>
    /// <param name="sender">发起命令的窗口控制。</param>
    /// <param name="args">命令参数；读其中的可选字符串 <c>label</c>。</param>
    /// <returns>目标窗口（已确认是已注册的存活窗口）。</returns>
    /// <exception cref="OrielIpcException">
    /// 没有 <c>label</c> 且发起者身份未知（窗口后端未就位），或目标 label 不存在。
    /// </exception>
    internal static WebviewWindow Resolve(IOrielWindowControl sender, JsonElement args)
        => ResolveWindow(sender, args);

    /// <summary>
    /// 同上，但只要求 <see cref="IOrielWindowControl"/>（命令层不需要具体窗口类型）。
    /// </summary>
    /// <remarks>
    /// 两个入口指向同一份实现。分开是为了让只用到"能弹菜单 / 能读缩放比"的命令族
    /// （<c>menu.*</c>）不必依赖 <see cref="WebviewWindow"/>——那会让它们的解析逻辑
    /// 在单测里够不着（假窗口实现不了具体类）。
    /// </remarks>
    /// <summary>
    /// 解析命令的目标窗口（命令层用的窄接口版本）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>目标就是发起者自己时直接返回它，不查窗口表</b>。两个理由：
    /// </para>
    /// <list type="number">
    ///   <item><description>省一次查表，且避免"窗口刚被关掉、已从表里摘了"时
    ///     报 <c>target_not_found</c>——一个已经不存在的窗口不会发命令。</description></item>
    ///   <item><description>让 <c>menu.*</c> 的解析逻辑在单测里够得着：假窗口注册不进
    ///     <see cref="OrielApp"/> 的窗口表（<c>AttachWindow</c> 要真实后端），
    ///     查表这条路对测试是死的。B6a 就是因为这条，把 <c>menu.popup</c> 的正向路径
    ///     整个让给了真机自检。</description></item>
    /// </list>
    /// <para>
    /// 显式给了<b>别的</b> label 时仍走查表：那是在寻址另一个窗口，必须验它真的在。
    /// </para>
    /// </remarks>
    internal static IOrielWindowControl ResolveControl(IOrielWindowControl sender, JsonElement args)
    {
        string? label = OptionalText(args, "label");
        string? ownLabel = sender.Label;

        if (label is null || (ownLabel is not null && string.Equals(label, ownLabel, StringComparison.Ordinal)))
        {
            if (ownLabel is null)
            {
                throw new OrielIpcException(
                    "无法确定发起命令的窗口：窗口后端尚未就位。请在页面加载完成后再调用。")
                {
                    Code = OrielIpcErrorCodes.NoSourceWindow,
                };
            }

            return sender;
        }

        return sender.App.GetWindow(label) ?? throw new OrielIpcException(
            $"找不到 label 为「{label}」的窗口：它可能已被关闭，或 label 拼错了。")
        {
            Code = OrielIpcErrorCodes.TargetNotFound,
        };
    }

    private static WebviewWindow ResolveWindow(IOrielWindowControl sender, JsonElement args)
    {
        string? label = OptionalText(args, "label");
        OrielApp app = sender.App;

        if (label is null)
        {
            string? ownLabel = sender.Label;
            if (ownLabel is null)
            {
                throw new OrielIpcException(
                    "无法确定发起命令的窗口：窗口后端尚未就位。请在页面加载完成后再调用。")
                {
                    Code = OrielIpcErrorCodes.NoSourceWindow,
                };
            }

            label = ownLabel;
        }

        return app.GetWindow(label) ?? throw new OrielIpcException(
            $"找不到 label 为「{label}」的窗口：它可能已被关闭，或 label 拼错了。")
        {
            Code = OrielIpcErrorCodes.TargetNotFound,
        };
    }

    /// <summary>读可选字符串参数（缺失或类型不符都当作"没有给"）。</summary>
    internal static string? OptionalText(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
