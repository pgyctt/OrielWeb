namespace OrielWeb.Ipc;

/// <summary>
/// 标记一个方法为可供前端 JS 调用的命令。由 OrielWeb.Generators 在编译期生成
/// 分发代码（运行期零反射，Native AOT 安全）。
/// </summary>
/// <remarks>
/// <para>支持的方法签名：</para>
/// <list type="bullet">
/// <item>同步/异步（Task、ValueTask 及其泛型版本均可）</item>
/// <item>静态方法直接调用；实例方法需经 <c>OrielAppBuilder.AddCommands&lt;T&gt;()</c> 注册</item>
/// <item>参数为命名参数：JS 侧传对象，如 <c>invoke('todo.add', { text: '...' })</c></item>
/// <item>参数/返回类型支持基元类型、string、JsonElement 及任意已注册 JsonSerializerContext 的 DTO</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class OrielCommandAttribute : Attribute
{
    /// <summary>命令名（JS 侧 <c>window.oriel.invoke(name)</c> 使用），建议形如 "模块.动作"。</summary>
    public string Name { get; }

    public OrielCommandAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
