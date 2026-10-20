using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrielWeb.Ipc;

/// <summary>
/// 命令执行期间向发起窗口的页面持续回传（对标 Tauri 的 <c>Channel</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的是"一次请求只能有一个回执"这个限制</b>：普通命令把结果放进唯一的那条 response，
/// 而流式场景（模型逐字输出、批量导入进度、日志尾部）要在命令返回**之前**多次推送。
/// </para>
/// <para>
/// <b>线格式与 Tauri 同形：一个字符串</b>。注入的桥接脚本会把参数对象 <c>JSON.stringify</c>
/// 之后发给宿主，页面侧的 <c>Channel</c> 通过 <c>toJSON</c>
/// 把自己变成一个 <c>__ORIEL_CHANNEL__:&lt;id&gt;</c> 字符串——因此**桥接层与三个平台后端
/// 一行都不用改**，通道只是"一个长得像字符串的参数"。宿主→页面则复用现成的事件通道
/// （两个事件名，见 <see cref="DataEventName"/> / <see cref="EndEventName"/>）。
/// </para>
/// <para>
/// <b>生命周期</b>：由生成器保证——命令返回、抛异常、或参数绑定失败，都会在
/// <c>finally</c> 里调用 <see cref="Close"/>。也就是说不必手动关；手动 <see cref="Close"/>
/// 只用于"提前结束但命令还要继续跑"的场景。
/// </para>
/// </remarks>
public sealed class OrielChannel
{
    /// <summary>页面侧 <c>toJSON()</c> 产出的前缀。桥接层不认识它，只有本类解析。</summary>
    internal const string WirePrefix = "__ORIEL_CHANNEL__:";

    private readonly OrielEventSink _sink;
    private readonly JsonSerializerContext? _jsonContext;
    private bool _closed;

    private OrielChannel(int id, OrielEventSink sink, JsonSerializerContext? jsonContext)
    {
        Id = id;
        _sink = sink;
        _jsonContext = jsonContext;
    }

    /// <summary>通道 id（页面侧生成，同一个窗口内唯一）。</summary>
    public int Id { get; }

    /// <summary>是否已经关闭（命令返回后为 true）。</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// 推一条消息给页面。
    /// </summary>
    /// <param name="value">任意可序列化的值。序列化规则与**命令返回值完全相同**
    /// （同一个 <see cref="OrielJson.WriteResult"/>）：基元、字符串序列、<c>byte[]</c> 直接写，
    /// 其它类型需要应用注册 <c>JsonSerializerContext</c> 并声明该类型——错误文案也一致。</param>
    /// <remarks>
    /// 刻意**没有** <c>Send&lt;T&gt;(T, JsonTypeInfo&lt;T&gt;)</c> 那一版：命令返回值既然能
    /// 靠应用上下文解决，通道没有理由另立一套。想自己控制序列化就调 <see cref="SendJson"/>。
    /// </remarks>
    public void Send(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            OrielJson.WriteResult(writer, value, _jsonContext);
        }

        SendJson(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>推一条**已经序列化好**的 JSON 给页面（不做任何校验）。</summary>
    public void SendJson(string json)
    {
        if (_closed)
        {
            // 命令已经返回，页面侧的通道监听也撤了。这里**不抛**：后台任务与命令返回
            // 天然会赛跑，把这种情况变成异常会让"fire-and-forget 的收尾"随机炸掉。
            // 但要留痕——否则它就是一条谁也不知道被丢掉的增量。
            OrielDiagnostics.Report(
                $"[OrielWeb] 通道 {Id} 已关闭，Send 被丢弃。命令已经返回，增量应当在命令内推完。");
            return;
        }

        Emit(DataEventName(Id), json);
    }

    /// <summary>关闭通道：通知页面退订。幂等。</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Emit(EndEventName(Id), "null");
    }

    /// <summary>数据事件名（页面侧按 id 订阅）。</summary>
    internal static string DataEventName(int id) => $"oriel://channel/{id}";

    /// <summary>结束事件名：页面收到就退订，不需要"哨兵值"混进数据里。</summary>
    internal static string EndEventName(int id) => $"oriel://channel-end/{id}";

    /// <summary>绑定必需通道参数：缺失或形状不对时抛 <see cref="OrielIpcException"/>。</summary>
    /// <remarks>
    /// <b>公开是因为生成器要调它</b>：生成的命令路由在**应用程序集**里，跨程序集调不到 internal。
    /// 应用代码不该直接用它——页面侧给一个值，绑定由生成代码完成。
    /// </remarks>
    public static OrielChannel Bind(
        JsonElement args, string name, OrielEventSink? sink, JsonSerializerContext? jsonContext)
        => Create(OrielJson.RequireArgElement(args, name), name, sink, jsonContext);

    /// <summary>绑定可选通道参数：缺失或为 null 时返回 null。</summary>
    /// <remarks>公开理由同 <see cref="Bind"/>。</remarks>
    public static OrielChannel? BindOptional(
        JsonElement args, string name, OrielEventSink? sink, JsonSerializerContext? jsonContext)
    {
        if (!OrielJson.TryGetArgElement(args, name, out JsonElement element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return Create(element, name, sink, jsonContext);
    }

    private static OrielChannel Create(
        JsonElement element, string name, OrielEventSink? sink, JsonSerializerContext? jsonContext)
    {
        // 通道的"另一端"是发起调用的那个窗口。没有来源窗口时无法推送（也没有"默认窗口"
        // 可退而求其次——多窗口下那会推到别人家里）。
        if (sink is null)
        {
            throw new OrielIpcException(
                $"命令参数 '{name}' 是通道，但它只能从窗口页面发起调用（这次调用没有来源窗口）。")
            {
                Code = OrielIpcErrorCodes.NoSourceWindow,
            };
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new OrielIpcException(
                $"命令参数 '{name}' 是通道，页面侧应当传 `new Channel()`（序列化成一个字符串）；" +
                $"实际收到 {element.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        string raw = element.GetString() ?? string.Empty;
        if (!raw.StartsWith(WirePrefix, StringComparison.Ordinal)
            || !int.TryParse(raw.AsSpan(WirePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            || id <= 0)
        {
            throw new OrielIpcException(
                $"命令参数 '{name}' 的值 '{raw}' 不是合法的通道标识（应为 `{WirePrefix}<正整数>`）。" +
                "页面侧不要自己拼这个字符串，用 `new Channel()`。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        return new OrielChannel(id, sink, jsonContext);
    }

    /// <summary>把一条事件推给页面；窗口已销毁时静默丢弃并留痕。</summary>
    /// <remarks>
    /// 与 <c>PushMenuItemClick</c> 同一姿态：这是"用户动作/后台任务的尾巴"，
    /// 窗口没了就没人在听，不值得让它把调用栈炸掉。
    /// </remarks>
    private void Emit(string eventName, string json)
    {
        try
        {
            _sink(eventName, json);
        }
        catch (InvalidOperationException ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 通道 {Id} 推送失败（窗口可能已销毁）：{ex.Message}");
        }
    }
}
