using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;

[assembly: InternalsVisibleTo("OrielWeb.Tests")]

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
    private readonly Dictionary<string, (IOrielCommandRouter Router, int Index)> _routes;
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
    }

    /// <summary>本分发器用的门禁（测试据此取令牌，见 TestHarness）。</summary>
    internal OrielIpcGuard Guard => _guard;

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

        // 内建窗口命令（win.*）**先于**应用路由：它们是库自己实现的，应用不需要注册。
        // 代价是应用自定义的同名命令会被遮蔽——win. 本来就是保留前缀（见 OrielBuiltInWindowCommands）。
        if (OrielBuiltInWindowCommands.TryGet(name, out var builtIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, builtIn(window, args));
            }
            catch (Exception ex)
            {
                // 内建命令抛出的是库自己的缺陷：走诊断通道，不进命令观测事件（见 Invoked 的说明）
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        // 内建托盘命令（tray.*）：同样是库实现、应用不必注册。
        // 托盘是应用级的，但命令由某个窗口的页面发起——经 window.App 拿到应用（见 IOrielWindowControl.App）。
        if (OrielBuiltInTrayCommands.TryGet(name, out var trayBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, trayBuiltIn(window, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        // 内建事件命令（event.*）：广播/定向发送。
        // **注意它不在"始终放行"名单里**（见 OrielCapabilityRules.IsBuiltInCommand）——
        // 走到这里之前已经过了命令授权，所以能到这一行的调用方是**被允许**的。
        // 这与 win.* / tray.* 刻意不同：广播是真正的能力（能驱动另一个窗口），
        // 而自绘标题栏不依赖它，没有开后门的理由。
        if (OrielBuiltInEventCommands.TryGet(name, out var eventBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, eventBuiltIn(window, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        // 内建路径命令族（path.*）：路径运算，对标 Tauri 2 的 @tauri-apps/api/path。
        //
        // **不需要来源窗口**——这是它与 win.* / window.* 的结构性差别：那些命令的操作对象
        // 是某个窗口，而路径运算只碰页面自己给的字符串。所以这里没有 window is null 的检查。
        //
        // 本族**始终放行**（见 OrielCapabilityRules.AlwaysAllowedPathCommands）。
        if (OrielBuiltInPathCommands.TryGet(name, window?.App.Info.Identifier, out var pathBuiltIn))
        {
            try
            {
                ReplyOk(sink, id, pathBuiltIn(args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        // 内建文件命令族（fs.*）：**不整族放行**，而且每个命令还要过**路径 scope**。
        //
        // 这是本库唯一一处"两道关都必过"的命令族：命令授权说"能调读文件这个动作"，
        // scope 说"能读哪些文件"。少了后者，一条 XSS 就能把 ~/.ssh/id_rsa 读走。
        // 与 path.* 的区别正在这里——path.* 只做运算不碰文件系统，所以能整族放行。
        if (OrielBuiltInFsCommands.TryGet(name, out var fsBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, fsBuiltIn(window, name, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }

            return;
        }

        // 内建对话框命令族（dialog.*）：对标 Tauri 的 plugin-dialog。
        //
        // **与既有 win.pickFile / window.pickFile 并存**：那两条是既有的公开契约
        // （冻结不动，只收 title/filter、返回单个路径），本族收 Tauri 形状的 options
        // （多选 / 过滤器 / 初始目录）。两者共用底下的原生对话框，
        // 所以不存在"两份实现漂移"——"独立"指的是命令契约独立。
        if (OrielBuiltInDialogCommands.TryGet(name, out var dialogBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, dialogBuiltIn(window, name, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }

            return;
        }

        // 内建平台集成命令族（opener.* / notification.* / autostart.*）：
        // 对标 Tauri 的 plugin-opener / plugin-notification / plugin-autostart。
        //
        // 三族**都不整族放行**：它们改的是系统状态（autostart）、能拉起外部程序
        // （opener.openPath 相当于"用默认程序打开任意文件"，双击一个 .exe 就是执行它）、
        // 或能拉起任意协议处理器（opener.openUrl）。都不是"只操作自己那个窗口"那一类。
        if (OrielBuiltInPlatformCommands.TryGet(name, out var platformBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, platformBuiltIn(window, name, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }

            return;
        }

        // 内建应用命令族（app.*）：只读元信息，对标 Tauri 2 的 @tauri-apps/api/app。
        //
        // 与 path.* 的差别：本族需要**应用实例**（`window.App`），所以要有来源窗口。
        // 实践中这不成问题——所有 invoke 都来自某个窗口里的页面；
        // 而真出现无窗口调用时，报 no_source_window 比"猜一个应用"清楚得多。
        if (OrielBuiltInAppCommands.TryGet(name, out var appBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, appBuiltIn(window, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        // 内建窗口 API 命令族（window.*）：可按 label 寻址，对标 Tauri 2 的 @tauri-apps/api/window。
        // 与 event.* 一样**不整族放行**：只有标题栏必需的那组才绕过能力校验
        // （见 OrielCapabilityRules.IsAlwaysAllowedWindowCommand）。能走到这里的调用方
        // 要么在那组白名单里，要么已经通过了命令授权。
        if (OrielBuiltInWindowApiCommands.TryGet(name, out var windowApiBuiltIn))
        {
            if (window is null)
            {
                ReplyError(sink, id, ErrorReply.Text($"命令 '{name}' 需要一个窗口，但这次调用没有来源窗口。", OrielIpcErrorCodes.NoSourceWindow));
                return;
            }

            try
            {
                ReplyOk(sink, id, windowApiBuiltIn(window, args));
            }
            catch (Exception ex)
            {
                ReplyError(sink, id, DescribeBuiltInFailure(name, ex));
            }
            return;
        }

        if (!_routes.TryGetValue(name, out var route))
        {
            ReplyError(sink, id, ErrorReply.Text($"未知命令 '{name}'。请确认方法已标注 [OrielCommand] 且所在程序集被应用引用。", OrielIpcErrorCodes.UnknownCommand));
            return;
        }

        // 命令观测：耗时取"进入命令到出结果/抛异常"这一段，不含门禁校验与回执投递。
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            object? target = route.Router.RequiresTarget ? ResolveTarget(route.Router.TargetType!) : null;
            var result = await route.Router.InvokeAsync(route.Index, target, args, _jsonContext).ConfigureAwait(false);
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
    /// 异常 → 回给页面的错误文案。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **只透传 <see cref="OrielIpcException"/>**（应用命令侧）。非 IPC 异常（IOException、
    /// SocketException……）的 Message 常含内部路径、主机名、端点，原样回传等于给（可能被 XSS 的）
    /// 页面一个探测内部的放大器；API.md「IPC」章对使用者的承诺也一直是"只有 OrielIpcException
    /// 的 Message 原样到达页面，其余给通用失败文本"——这里让代码兑现这句话。
    /// 参数校验错误（缺参数、类型不符）由 <see cref="OrielJson"/> 抛
    /// <see cref="OrielIpcException"/>，不受影响。命令名可以进文案：它是页面自己发来的。
    /// </para>
    /// <para>
    /// <b>内建命令（<c>win.*</c> / <c>tray.*</c>）是另一套口径</b>：它们全部由库自己实现，
    /// 异常里不会有应用/页面塞进去的内容，而失败原因（"应用尚未运行""托盘不存在"）正是
    /// 页面排障唯一能看到的东西。所以内建命令的异常**原样透传**——
    /// 把它净化成"命令执行失败"只会让调用方拿到一句无从下手的文案。
    /// </para>
    /// </remarks>
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
        internal static ErrorReply FromException(Exception ex, string fallbackMessage) => ex is OrielIpcException ipc
            ? new ErrorReply(ipc.Message, ipc.Code, ipc.Details)
            : new ErrorReply(fallbackMessage, OrielIpcErrorCodes.Internal, null);
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
