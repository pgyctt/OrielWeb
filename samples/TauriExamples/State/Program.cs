using OrielWeb;
using OrielWeb.Ipc;

namespace TauriState;

/// <summary>
/// 入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是本示例唯一需要理解的一件事</b>：Tauri 的 <c>app.manage(Counter(Mutex::new(0)))</c>
/// 把状态放进一个全局容器，命令靠 <c>State&lt;'_, Counter&gt;</c> 形参把它取回来。
/// 本库没有那个容器——<b>命令类的实例本身就是容器</b>：
/// <c>AddCommands(() =&gt; new CounterCommands(counter))</c> 里的工厂只跑一次，
/// 之后每条 invoke 都作用在同一个对象上（见 docs/API.md「命令的注册语义」）。
/// </para>
/// <para>
/// 所以迁移动作是：把 <c>.manage(...)</c> 改成一个普通的构造调用，
/// 把 <c>State&lt;'_, T&gt;</c> 形参改成构造函数参数。命令的<b>方法签名在页面侧看不见</b>，
/// 页面不需要改任何一行。
/// </para>
/// <para>
/// 线程安全仍然要自己做：命令实例是共享的，多个窗口、多个 invoke 可能并发进来。
/// 原文靠 <c>Mutex</c>，这里靠 <c>lock</c>——不是"本库会替你串行化"。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 对应 Rust 侧的 `let state = Counter(Mutex::new(0))`
        var counter = new Counter();

        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            // 回执全是 long（基元），不需要 UseJsonContext：只有 DTO 才需要源生成上下文。
            .WithAppInfo(name: "Tauri 示例 · State", identifier: "com.orielweb.samples.tauri.state")
            // 工厂只调用一次，实例全应用共享——这就是 Tauri 的 manage/State
            .AddCommands(() => new CounterCommands(counter))
            .UseCapabilities(c => c.Allow("increment", "decrement", "reset", "get"))
            .AddWindow(w => w
                .WithTitle("Tauri 示例 · State")
                .WithSize(720, 640)
                .WithMinSize(520, 480)
                .Centered())
            .Run();
    }
}

/// <summary>
/// 共享状态。对应原文的 <c>struct Counter(Mutex&lt;isize&gt;)</c>。
/// </summary>
/// <remarks>
/// 用 <see cref="object"/> + <c>lock</c> 而不是 <see cref="System.Threading.Mutex"/>：
/// 这里的临界区只有一次加法，托管锁足够；用系统互斥体反而会引入"谁持有、跨不跨进程"的额外语义。
/// </remarks>
internal sealed class Counter
{
    private readonly object _gate = new();
    private long _value;

    internal long Add(int delta)
    {
        lock (_gate)
        {
            _value += delta;
            return _value;
        }
    }

    internal long Reset()
    {
        lock (_gate)
        {
            _value = 0;
            return _value;
        }
    }

    internal long Read()
    {
        lock (_gate)
        {
            return _value;
        }
    }
}

/// <summary>
/// 命令类。四条命令与原文逐字同名：<c>increment</c> / <c>decrement</c> / <c>reset</c> / <c>get</c>。
/// </summary>
internal sealed partial class CounterCommands(Counter counter)
{
    [OrielCommand("increment")]
    public long Increment() => counter.Add(1);

    [OrielCommand("decrement")]
    public long Decrement() => counter.Add(-1);

    [OrielCommand("reset")]
    public long Reset() => counter.Reset();

    [OrielCommand("get")]
    public long Get() => counter.Read();
}
