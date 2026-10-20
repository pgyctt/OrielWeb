using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrielWeb.Ipc;

/// <summary>
/// 由 OrielWeb.Generators 生成并实现：把命令名映射为索引并执行。
/// 这是 IPC 的编译期契约——运行期不存在任何反射查找。
/// </summary>
public interface IOrielCommandRouter
{
    /// <summary>本路由注册的全部命令名（用于构建全局索引与同名冲突检测）。</summary>
    IReadOnlyList<string> CommandNames { get; }

    /// <summary>命令所在类型；全部命令为静态时返回 null。</summary>
    Type? TargetType { get; }

    /// <summary>是否存在需要实例目标的命令（决定是否从应用容器解析实例）。</summary>
    bool RequiresTarget { get; }

    /// <summary>命令名 → 索引；未注册的命令返回 -1。</summary>
    int Route(string name);

    /// <summary>
    /// 执行索引对应的命令。
    /// </summary>
    /// <param name="index">命令索引（由 <see cref="Route"/> 得到）。</param>
    /// <param name="target">命令实例；静态命令时为 null。</param>
    /// <param name="args">命令的 args 元素。</param>
    /// <param name="jsonContext">
    /// 应用注册的 STJ 源生成上下文，供 DTO 参数反序列化用；基元参数用不到，可能为 null。
    /// 由调用方（<see cref="OrielCommandDispatcher"/>）透传，**不来自任何静态状态**。
    /// </param>
    /// <param name="sink">
    /// 向发起调用的窗口推事件的能力；没有来源窗口时为 null。
    /// <see cref="OrielChannel"/> 参数靠它把增量推回页面——通道的"另一端"是**发起调用的那个窗口**，
    /// 不是应用，也不是"当前活动窗口"（多窗口下那两者都可能不是它）。
    /// </param>
    ValueTask<object?> InvokeAsync(
        int index,
        object? target,
        JsonElement args,
        JsonSerializerContext? jsonContext,
        OrielEventSink? sink);
}
