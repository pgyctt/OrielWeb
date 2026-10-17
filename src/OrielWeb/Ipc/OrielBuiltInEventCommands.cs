using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建事件命令（<c>event.emit</c>）：页面把事件广播给其它窗口（对标 Tauri 2 的 <c>emit</c> / <c>emitTo</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它刻意不在"始终放行"的名单里</b>——这一点与 <c>win.*</c> / <c>tray.*</c> 不同，
/// 也是安全红线的落点：广播是真正的能力。一个窗口的页面能驱动另一个窗口的行为
/// （"打开那个页面"、"刷新数据"），这在多窗口应用里等同于跨窗口的指令通道。
/// 拒掉它不会让任何窗口变得不可用（自绘标题栏不依赖它），所以没有开后门的理由。
/// </para>
/// <para>
/// 因此它要走完整的命令授权：应用必须显式 <c>Allow("event.emit")</c> 或 <c>Allow("event.*")</c>。
/// Release 下未声明能力时它会被拒——与"新命令族默认不在 allow 名单里"这条原则一致。
/// </para>
/// <para>
/// <b>不校验事件名</b>：事件名是页面自己的命名空间（既有文档已写明"库不校验事件名"）。
/// 校验它既没有安全收益（能发事件不等于能调用命令），又会逼应用维护一份名单。
/// </para>
/// </remarks>
internal static class OrielBuiltInEventCommands
{
    /// <summary>命令名前缀。**不在** <see cref="OrielCapabilityRules.IsBuiltInCommand"/> 的放行名单里。</summary>
    internal const string Prefix = "event.";

    /// <summary>取某个 <c>event.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    /// <param name="name">命令名（区分大小写，与命令授权用的是同一套 Ordinal 比较）。</param>
    /// <param name="handler">处理函数：窗口 + args → 回执里的 value。</param>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        switch (name)
        {
            case "event.emit":
                handler = static (window, args) => Emit(window, args);
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>
    /// 把一次事件发给指定窗口或全部窗口。
    /// </summary>
    /// <param name="sender">发起调用的窗口（用它拿到应用）。</param>
    /// <param name="args">
    /// <c>{ event: string, payload?: any, target?: string | null }</c>。
    /// <c>target</c> 为 <c>null</c> / 缺省表示广播到所有窗口。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>目标窗口不存在时静默丢弃</b>（与 Tauri 的 <c>emitTo</c> 一致）：多窗口应用里窗口随时
    /// 可能被关掉，"发的时候还在、到的时候没了"是正常时序。让它抛异常只会逼调用方到处包
    /// try/catch，而那个 try 掩盖的是时序而不是错误。
    /// </para>
    /// <para>
    /// <b>发起窗口也会收到自己的广播</b>：与 Tauri 的全局 <c>emit</c> 语义一致。
    /// 想"只发给别人"应当用 <c>emitTo</c> 逐个指定，或由页面自己按 label 过滤。
    /// </para>
    /// </remarks>
    private static object? Emit(IOrielWindowControl sender, JsonElement args)
    {
        const string Command = "event.emit";

        string eventName = RequiredText(args, "event", Command);

        // payload 原样透传：库不解析也不改写它（见 API.md 对 EmitEvent 的承诺）。
        // 缺省或显式 null 都写成 JSON 的 null——页面侧 oriel.on 会收到 null 而不是 undefined，
        // 与"宿主没给 payload"的既有语义一致。
        string payloadJson = "null";
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("payload", out JsonElement payload)
            && payload.ValueKind != JsonValueKind.Undefined)
        {
            payloadJson = payload.GetRawText();
        }

        string? target = OptionalText(args, "target");
        OrielApp app = sender.App;

        if (target is null)
        {
            int delivered = 0;
            foreach (WebviewWindow window in app.Windows)
            {
                window.EmitEvent(eventName, payloadJson);
                delivered++;
            }

            return delivered;
        }

        // 定向发送：找不到就什么都不做（见方法说明）。返回 0/1 让页面能观测"到底送到没有"
        // ——静默丢弃是语义，但"丢弃了"这件事本身应当可查询，否则排查只能靠猜。
        WebviewWindow? destination = app.GetWindow(target);
        if (destination is null)
        {
            return 0;
        }

        destination.EmitEvent(eventName, payloadJson);
        return 1;
    }

    /// <summary>读一个必给的非空字符串参数。</summary>
    private static string RequiredText(JsonElement args, string property, string commandName)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        throw new OrielIpcException($"命令 '{commandName}' 缺少非空字符串参数 '{property}'。");
    }

    /// <summary>读一个可选字符串参数；缺失/null/空串都按"没给"处理。</summary>
    private static string? OptionalText(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
           && element.GetString() is { Length: > 0 } value
            ? value
            : null;
}
