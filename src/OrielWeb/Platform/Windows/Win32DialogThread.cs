using System.Collections.Concurrent;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 专供原生对话框使用的 STA 线程（进程级单例）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：<c>GetOpenFileNameW</c> / <c>GetSaveFileNameW</c> / <c>MessageBoxW</c>
/// 都是<b>模态阻塞</b>调用——函数返回之前，调用线程一直停在那一帧里。放在 UI 线程上调用，
/// 整条 IPC 通道就跟着停摆：对话框开多久，页面发来的 invoke 就排队多久，关闭后一次性涌入。
/// 实测（0.6.1）：保存对话框开 29 秒 → 29 秒内零条命令执行，关闭后 140+ 条积压命令在 5ms 内执行。
/// </para>
/// <para>
/// <b>为什么是独立线程而不是线程池</b>：文件对话框的 shell 集成要求调用线程是 STA，
/// 且需要该线程自己的 OLE 初始化（<c>OleInitialize</c> / <c>OleUninitialize</c> 成对）。
/// 线程池线程是 MTA，直接在它上面调 <c>GetOpenFileNameW</c> 可能行为异常。
/// </para>
/// <para>
/// <b>为什么共用一条线程</b>：对话框是低频操作，但窗口可能反复创建销毁；每窗口一条线程
/// 会留下"窗口没了、线程还堵在对话框里"的悬挂线程。共用一条之后，线程生命周期与进程一致。
/// 对话框的 owner 是主窗口句柄，跨线程使用在 Windows 上是允许的——这正是"主窗口被禁用、
/// 但消息循环继续跑"这个模态语义的来源。
/// </para>
/// <para>
/// <b>线程模型的边界</b>：本类只保证"启动对话框这个动作"不在 UI 线程上发生。
/// 对话框自己的消息循环在该线程上运行（Win32 模态对话框自带），主窗口会被系统禁用
/// （用户点不到页面，这是模态的本意），但 UI 线程的消息泵不受影响。
/// </para>
/// </remarks>
internal sealed class Win32DialogThread
{
    private static readonly Lazy<Win32DialogThread> s_shared = new(() => new Win32DialogThread());

    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly Thread _thread;

    /// <summary>进程共享的对话框线程。</summary>
    internal static Win32DialogThread Shared => s_shared.Value;

    private Win32DialogThread()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,   // 进程退出时不必等一个还开着的对话框
            Name = "OrielWeb.Dialog",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        // shell 对话框要求 STA + OLE。返回 S_FALSE(1) 也是成功，按文档同样要配对解除。
        int hr = Win32.OleInitialize(0);
        try
        {
            foreach (WorkItem item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Completion.SetResult(item.Work());
                }
                catch (Exception ex)
                {
                    item.Completion.SetException(ex);
                }
            }
        }
        finally
        {
            if (hr >= 0)
            {
                Win32.OleUninitialize();
            }
        }
    }

    /// <summary>
    /// 在这条线程上执行 <paramref name="work"/>，把结果以 Task 形式还给调用方。
    /// </summary>
    /// <remarks>
    /// 调用方**不要**在 UI 线程上同步等这个 Task——那等于把刚让出来的线程又堵回去。
    /// 只有 async 方法能真正获益。
    /// </remarks>
    internal Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(() => work(), completion));
        return CastAsync<T>(completion.Task);
    }

    /// <remarks>
    /// 单独一个方法而不是内联在 <see cref="InvokeAsync{T}"/> 里：async 方法不能有 out/ref，
    /// 而这里要把 <c>Task&lt;object?&gt;</c> 还原成 <c>Task&lt;T&gt;</c>。
    /// </remarks>
    private static async Task<T> CastAsync<T>(Task<object?> task)
        => (T)(await task.ConfigureAwait(false))!;

    private sealed class WorkItem(Func<object?> work, TaskCompletionSource<object?> completion)
    {
        internal Func<object?> Work { get; } = work;
        internal TaskCompletionSource<object?> Completion { get; } = completion;
    }
}
