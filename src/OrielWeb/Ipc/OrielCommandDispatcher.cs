using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;

[assembly: InternalsVisibleTo("OrielWeb.Tests")]

// 基准项目要直接调 HandleInvokeAsync 与资源/路径查找（都是 internal）：
// 通过这些类型的公开面绕一圈，测到的就不是热路径本身了。
[assembly: InternalsVisibleTo("OrielWeb.Benchmarks")]

namespace OrielWeb.Ipc;

/// <summary>把回执 JSON 发回前端（由平台后端实现；实现负责切回 UI 线程）。</summary>
internal interface IIpcReplySink
{
    void PostJson(string json);
}

/// <summary>
/// IPC 分发核心：解析 invoke 消息 → 查路由 → 执行命令 → 回执。
/// 消息协议：{ "__oriel":"invoke", "id":1, "name":"todo.add", "args":{...} }
/// 回执协议：{ "__oriel":"result", "id":1, "ok":true, "value":... | "ok":false, "error":"..." }
/// </summary>
/// <remarks>
/// 每个 <see cref="OrielApp"/> 一个实例，**JSON 上下文由构造参数持有**（不读任何全局静态状态），
/// 因此同一进程里创建多个应用时它们互不干扰。
/// </remarks>
internal sealed class OrielCommandDispatcher
{
    private readonly Dictionary<Type, Func<object>> _factories;
    private readonly ConcurrentDictionary<Type, object> _targets = [];
    /// <summary>
    /// 命令名 → (路由, 索引)。构造期快照，miss 且注册表修订号领先时重建（见 HandleInvokeAsync）——
    /// 因此不设 readonly。引用赋值原子，热路径读它无需加锁。
    /// </summary>
    private Dictionary<string, (IOrielCommandRouter Router, int Index)> _routes;
    private readonly JsonSerializerContext? _jsonContext;
    private readonly OrielIpcGuard _guard;

    public OrielCommandDispatcher(
        Dictionary<Type, Func<object>> factories, JsonSerializerContext? jsonContext, OrielIpcGuard guard)
    {
        _factories = factories;
        _jsonContext = jsonContext;
        _guard = guard;
        // 一次性构建「命令名 → (路由, 索引)」索引。此前是每条消息遍历全部 router：
        // O(router 数) + 每次加锁 + 一次数组分配（Snapshot 里的 [.. s_routers]），
        // README 声称的 O(1) 分发实际只成立于单个 router 内部。
        _routes = OrielCommandRegistry.BuildIndex();
        _routesVersion = OrielCommandRegistry.Version;
    }

    /// <summary>构建 <see cref="_routes"/> 快照时的注册表修订号（miss 时据此判断要不要重建）。</summary>
    private long _routesVersion;

    /// <summary>本分发器用的门禁（测试据此取令牌，见 TestHarness）。</summary>
    internal OrielIpcGuard Guard => _guard;

    /// <summary>
    /// 解析命令路由：先查快照，miss 且注册表修订号领先时重建一次再查。
    /// </summary>
    /// <remarks>
    /// miss 的两种情形要分开：① 真的没这个命令（拼错 / 没 AddCommands）；② 快照构建之后
    /// 又有程序集被加载（运行期 <c>Assembly.Load</c> 插件），全局注册表领先于构造期快照。
    /// ②的表现在过去是一句"未知命令"，而全局索引里明明有——排查者两边都查得到、就是对不上。
    /// 重建只在 miss 且修订号落后时发生：热路径（命中）仍是一次字典查找零额外开销；
    /// 拼错名字的持续 miss 也只重建一次（修订号追平后就不再重建）。
    /// </remarks>
    private bool TryResolveRoute(string name, out (IOrielCommandRouter Router, int Index) route)
    {
        if (_routes.TryGetValue(name, out route))
        {
            return true;
        }

        long registryVersion = OrielCommandRegistry.Version;
        if (registryVersion == _routesVersion)
        {
            return false;
        }

        _routes = OrielCommandRegistry.BuildIndex();
        _routesVersion = registryVersion;
        return _routes.TryGetValue(name, out route);
    }

    /// <summary>
    /// 应用命令执行完成时触发（成功与失败都有）。由 <see cref="OrielApp.CommandInvoked"/> 转发给应用。
    /// </summary>
    /// <remarks>
    /// 内建 <c>win.*</c> 不在此列：拖动期间的 <c>win.dragTo</c> 每秒几十条，混进来只会淹没
    /// "哪条命令慢"。内建命令的异常走 <see cref="OrielDiagnostics"/>（那是库自己的缺陷面）。
    /// </remarks>
    internal event Action<OrielCommandInvokedEventArgs>? Invoked;

    /// <summary>应用命令抛异常时触发（异常原样）。由 <see cref="OrielApp.CommandFailed"/> 转发。</summary>
    internal event Action<OrielCommandFailedEventArgs>? Failed;

    /// <param name="message">入站 invoke 消息。</param>
    /// <param name="sink">回执通道。</param>
    /// <param name="documentUrl">
    /// 发起调用的文档 URL（由窗口后端在导航时记录）。**没有它就没法做来源校验**——
    /// 消息本身不带来源，而"这个页面是不是应用自己的"是能力模型的第一道门。
    /// </param>
    /// <param name="window">
    /// 发起这次调用的窗口，供内建的 <c>win.*</c> 命令使用；没有来源窗口时传 null。
    /// **多窗口下必须由后端各传自己那一个**——否则"最小化"会作用到别的窗口上。
    /// </param>
    public async ValueTask HandleInvokeAsync(
        JsonElement message, IIpcReplySink sink, string? documentUrl, IOrielWindowControl? window = null)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // 门禁一、二（来源 + 令牌）先于任何解析：不信任的消息连命令名都不该被读出来。
        // 拒绝的记账（Debug 输出 + IpcRejected 事件）在门禁内部，这里只把原因回给页面。
        if (!_guard.TryAccept(documentUrl, message, out string? rejection))
        {
            ReplyError(sink, ReadId(message), ErrorReply.Text(rejection ?? "IPC 消息被拒绝。", OrielIpcErrorCodes.NotAllowed));
            return;
        }

        // id **原样保留**，不解析成 int。页面侧是 JS number，只要回执里的值与它发出去的一致，
        // 就能在 pending 里对上并立即 settle。以前解析成 int 时，1.5 / 1e30 这类值会抛
        // FormatException，兜底回执只好写死 id:0——而页面的 seq 从 1 开始，永远匹配不到，
        // 那个 Promise 要一直挂到 30 秒超时才失败（表现为"命令没反应"，而不是"参数错了"）。
        JsonElement id = ReadId(message);

        string? name = message.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        if (string.IsNullOrEmpty(name))
        {
            ReplyError(sink, id, ErrorReply.Text("invoke 消息缺少 name 字段。", OrielIpcErrorCodes.InvalidMessage));
            return;
        }

        // 门禁三（命令授权）：来源与令牌都对，不代表这个命令就该被调用。
        if (!_guard.TryAuthorize(name, out string? denialReason))
        {
            string reason = denialReason ?? "命令未被授权。";
            _guard.Report(OrielIpcRejectionLayer.Command, reason, documentUrl, name);
            ReplyError(sink, id, ErrorReply.Text(reason, OrielIpcErrorCodes.NotAllowed));
            return;
        }

        JsonElement args = message.TryGetProperty("args", out var argsElement) ? argsElement : default;

        // 内建命令族：按 OrielBuiltInCommandFamilies.All 的顺序尝试，命中即处理并返回。
        // **先于**应用路由：win. / tray. 是库保留前缀，应用同名命令在注册期就被拦下
        // （见 OrielCommandRegistry.AddRouter），所以这里不存在"应用命令被静默遮蔽"。
        foreach (IBuiltInCommandFamily family in OrielBuiltInCommandFamilies.All)
        {
            if (!family.TryGet(name, window, out Func<IOrielWindowControl, string, JsonElement, object?> handler))
            {
                continue;
            }

            if (family.RequiresWindow && window is null)
            {
                ReplyError(
                    sink,
                    id,
                    ErrorReply.Text(
                        $"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。",
                        OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                // window 只在 RequiresWindow=true 时非空；false 的族（path.*）按契约忽略这个参数，
                // 见 IBuiltInCommandFamily 的说明。
                ReplyOk(sink, id, handler(window!, name, args));
            }
            catch (Exception ex)
            {
                // 内建命令抛出的是库自己的缺陷：走诊断通道，不进命令观测事件（见 Invoked 的说明）
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }

            return;
        }


        if (!TryResolveRoute(name, out var route))
        {
            ReplyError(sink, id, ErrorReply.Text($"未知命令 '{name}'。请确认方法已标注 [OrielCommand] 且所在程序集被应用引用。", OrielIpcErrorCodes.UnknownCommand));
            return;
        }

        // 命令观测：耗时取"进入命令到出结果/抛异常"这一段，不含门禁校验与回执投递。
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            object? target = route.Router.RequiresTarget ? ResolveTarget(route.Router.TargetType!) : null;
            // 通道的推送能力：绑定到发起这次调用的那个窗口。没有来源窗口时传 null，
            // 让接受 OrielChannel 参数的命令以 no_source_window 失败（而不是推给"当前窗口"）。
            OrielEventSink? pushSink = window is null ? null : window.EmitEvent;
            var result = await route.Router.InvokeAsync(route.Index, target, args, _jsonContext, pushSink).ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            ReplyOk(sink, id, result);
            Invoked?.Invoke(new OrielCommandInvokedEventArgs(name, elapsed, true, documentUrl));
        }
        catch (Exception ex)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            ReplyError(sink, id, DescribeForPage(name, ex));
            // 先把异常交给宿主（它最关心），再把这次调用计进"已完成"——失败的调用同样是一次完成
            Failed?.Invoke(new OrielCommandFailedEventArgs(name, ex, elapsed, documentUrl));
            Invoked?.Invoke(new OrielCommandInvokedEventArgs(name, elapsed, false, documentUrl));
        }
    }

    /// <summary>
    /// 一条错误回执的内容：文案 + 可选的机器可判别的码与附加数据。
    /// </summary>
    /// <param name="Message">给页面看的文案（会进用户可见的提示）。</param>
    /// <param name="Code">错误码；<c>null</c> 表示没有分类。</param>
    /// <param name="Details">附加数据（任意 JSON）；<c>null</c> 表示没有。</param>
    /// <remarks>
    /// 单独一个类型而不是三个参数：调用点很多，而"忘了传码"在三个参数的位置上太容易发生
    /// （尤其后面还要加参数时）。<c>with</c> 与命名参数也更顺手。
    /// </remarks>
    private readonly record struct ErrorReply(string Message, string? Code, JsonElement? Details)
    {
        /// <summary>只有文案的错误。</summary>
        internal static ErrorReply Text(string message, string? code = null) => new(message, code, null);

        /// <summary>
        /// 从一个异常构造。
        /// </summary>
        /// <param name="ex">异常。</param>
        /// <param name="fallbackMessage">非 <see cref="OrielIpcException"/> 时给页面的通用文案。</param>
        /// <remarks>
        /// 非 <see cref="OrielIpcException"/> 一律归为 <see cref="OrielIpcErrorCodes.Internal"/>：
        /// 那意味着库的实现出了问题（应用命令的正常失败应当抛 <see cref="OrielIpcException"/>）。
        /// </remarks>
        internal static ErrorReply FromException(Exception ex, string fallbackMessage) => ex switch
        {
            OrielIpcException ipc => new ErrorReply(ipc.Message, ipc.Code, ipc.Details),

            // 后端用 NotSupportedException 表达"这个能力在当前平台没有"（光标抓取在 Linux、
            // resize 拖动在 macOS…）。不翻译的话页面只能拿到 internal，而"internal"读起来
            // 像库有 bug，实际是平台事实、调用方应当换条路走。
            NotSupportedException => new ErrorReply(ex.Message, OrielIpcErrorCodes.NotSupported, null),

            _ => new ErrorReply(fallbackMessage, OrielIpcErrorCodes.Internal, null),
        };
    }

    /// <summary>
    /// 应用命令的异常 → 给页面的错误回执（净化规则见方法内的说明）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只有 <see cref="OrielIpcException"/> 的 Message 原样到页面，其余给通用文案——
    /// 应用的异常 Message 可能含内部路径、SQL、连接串，而页面是"用户看得见的地方"。
    /// 参数校验错误（缺参数、类型不符）由 <see cref="OrielJson"/> 抛
    /// <see cref="OrielIpcException"/>，不受影响。命令名可以进文案：它是页面自己发来的。
    /// </para>
    /// <para>
    /// <b>内建命令是另一套口径</b>：它们全部由库自己实现，异常里不会有应用塞进去的内容，
    /// 而失败原因（"应用尚未运行""托盘不存在"）正是页面排障唯一能看到的东西。
    /// 所以内建命令的异常**原样透传**——把它净化成"命令执行失败"只会让调用方拿到
    /// 一句无从下手的文案。
    /// </para>
    /// </remarks>
    private static ErrorReply DescribeForPage(string name, Exception ex)
    {
        if (ex is OrielIpcException)
        {
            return ErrorReply.FromException(ex, string.Empty);
        }

        OrielDiagnostics.Report($"[OrielWeb] 命令 '{name}' 执行失败（详情不透传页面）：{ex}");
        return new ErrorReply($"命令 '{name}' 执行失败。", OrielIpcErrorCodes.Internal, null);
    }

    /// <summary>内建命令的异常：原样带出（理由见 <see cref="DescribeForPage"/> 的 remarks）。</summary>
    private static ErrorReply DescribeBuiltInFailure(string name, Exception ex)
    {
        OrielDiagnostics.Report($"[OrielWeb] 内建命令 '{name}' 执行失败：{ex}");
        return ErrorReply.FromException(ex, ex.Message);
    }

    private static JsonElement ReadId(JsonElement message)
        => message.TryGetProperty("id", out JsonElement idElement) ? idElement : default;

    private object ResolveTarget(Type targetType)
    {
        // GetOrAdd 在竞态下可能重复调用工厂（多次创建、留最后一个），
        // 命令实例无单例语义要求时无害；需要精确单例的应用应自行保证工厂幂等
        return _targets.GetOrAdd(targetType, static (type, factories) =>
        {
            if (!factories.TryGetValue(type, out var factory))
            {
                throw new OrielIpcException(
                    $"命令类型 {type} 含实例命令，但未注册：请调用 OrielAppBuilder.AddCommands<{type.Name}>()。");
            }
            return factory();
        }, _factories);
    }

    private void ReplyOk(IIpcReplySink sink, JsonElement id, object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("__oriel", "result");
            WriteId(writer, id);
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("value");
            OrielJson.WriteResult(writer, value, _jsonContext);
            writer.WriteEndObject();
        }
        sink.PostJson(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void ReplyError(IIpcReplySink sink, JsonElement id, string error)
        => ReplyError(sink, id, ErrorReply.Text(error));

    /// <summary>
    /// 写一条失败回执。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回执形状：<c>{ "__oriel":"result", id, ok:false, error, code?, data? }</c>。
    /// </para>
    /// <para>
    /// <c>code</c> 与 <c>data</c> **只在有值时才写**：老页面只读 <c>error</c>，多出来的字段
    /// 对它们是噪音；而"字段存在但是 null"与"字段不存在"在 JS 侧要靠 <c>in</c> 才能区分，
    /// 那是个容易写错的判断。少写两个字段，页面写 <c>if (e.code)</c> 就够了。
    /// </para>
    /// </remarks>
    private static void ReplyError(IIpcReplySink sink, JsonElement id, ErrorReply reply)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("__oriel", "result");
            WriteId(writer, id);
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", reply.Message);

            if (!string.IsNullOrEmpty(reply.Code))
            {
                writer.WriteString("code", reply.Code);
            }

            if (reply.Details is { } details)
            {
                writer.WritePropertyName("data");
                details.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
        sink.PostJson(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// 回写 id：数字**原样写回**（保留小数与超出 Int32 的大数），其余情况写 0。
    /// </summary>
    /// <remarks>
    /// 原样回写是为了让页面能用它自己发出的那个值在 <c>pending</c> 里查回来。
    /// 协议要求 <c>id</c> 字段始终存在，所以缺失或非数字时兜底写 0——那类消息本来也不可能是
    /// 桥接脚本发出的（它的 <c>seq</c> 是自增整数）。
    /// </remarks>
    private static void WriteId(Utf8JsonWriter writer, JsonElement id)
    {
        writer.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Number)
        {
            id.WriteTo(writer);
        }
        else
        {
            writer.WriteNumberValue(0);
        }
    }
}
