using System.Text.Json;

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

    /// <summary>执行索引对应的命令。<paramref name="target"/> 为静态命令时为 null。</summary>
    ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args);
}
