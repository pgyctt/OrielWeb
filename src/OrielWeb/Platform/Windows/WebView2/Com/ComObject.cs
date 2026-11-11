using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Com;

/// <summary>
/// 把原生 COM 指针包装成 <see cref="IComObject{T}"/>。
/// </summary>
/// <remarks>
/// 只做导入方向（原生 → 托管）。导出方向由 <c>[GeneratedComClass]</c> 负责，
/// 事件处理器与完成处理器都走那条路，不经过这里。
/// </remarks>
internal static class ComObject
{
    /// <summary>
    /// 接管一个**已归调用方所有**的接口指针并包装。
    /// </summary>
    /// <param name="pointer">接口指针；0 返回 <c>null</c>（WebView2 用空指针表示"没有对象"）。</param>
    public static IComObject<T>? FromPointer<T>(nint pointer) where T : class
        => pointer == 0 ? null : new ComObject<T>(pointer);

    /// <summary>同上，但指针为空时抛异常而不是返回 <c>null</c>。</summary>
    public static IComObject<T> FromRequiredPointer<T>(nint pointer) where T : class
        => pointer == 0
            ? throw new InvalidOperationException($"WebView2 返回了空的 {typeof(T).Name} 指针。")
            : new ComObject<T>(pointer);

    /// <summary>
    /// 只要托管对象、**不要**包装器时用这条：直接转换，不 AddRef、不需要 Dispose。
    /// </summary>
    /// <param name="pointer">接口指针（所有权仍归调用方/被调方，本方法不接管）。</param>
    /// <remarks>
    /// <para>
    /// 2026-11-11 评审 M2 的四处调用点原本写的是 <c>ComObject.FromPointer&lt;T&gt;(p)?.Object</c>：
    /// 造一个包装器、立刻只取 <c>Object</c>、包装器随即变垃圾。而包装器在构造时**自己 AddRef 了一份**
    /// （理由见 <see cref="ComObject{T}"/> 的构造注释），没人 Dispose 就是净 +1——
    /// 每取一次属性泄漏一个引用。加终结器只是兜底，正确的修法是**根本不造那个包装器**。
    /// </para>
    /// <para>
    /// 与 <see cref="FromPointer{T}"/> 的分工：需要"确定性释放、且这个引用要活一段"时用包装器
    /// （<c>using</c>）；只是"现在就要那个托管对象"时用本方法。本方法不做 AddRef，
    /// 所以**不产生**需要释放的引用，也就不该被 <c>using</c>。
    /// </para>
    /// </remarks>
    public static unsafe T? ToManaged<T>(nint pointer) where T : class
        => pointer == 0 ? null : ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer);
}

/// <inheritdoc />
internal sealed class ComObject<T> : IComObject<T> where T : class
{
    private nint _pointer;
    private T? _object;

    internal unsafe ComObject(nint pointer)
    {
        _pointer = pointer;

        // 必须自己 AddRef 一份，不能依赖 ConvertToManaged 的引用语义。
        //
        // 实测（.NET 10）：同一个接口指针第二次交给 ConvertToManaged 时，它返回已缓存的
        // 托管实例且**不再 AddRef**。而 WebView2 里同一指针被反复包装是常态——事件参数、
        // QI 结果、每个窗口共享的 Environment 都会命中缓存。此时若 Dispose 仍 Release 一次，
        // 净效果就是 -1：原生对象被提前销毁，之后要么在 GC 的 ComObject.Finalize 里
        // Access Violation，要么后续调用返回 E_UNEXPECTED。
        //
        // 显式 AddRef 之后语义就固定了：构造 +1、Dispose -1，与 ConvertToManaged 的
        // 缓存状态无关。托管包装器自己那份由它的终结器负责，我们不碰。
        _ = Marshal.AddRef(pointer);

        _object = ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)
            ?? throw new InvalidOperationException($"无法把原生指针转换成 {typeof(T).Name}。");
    }

    public T Object => _object ?? throw new ObjectDisposedException(typeof(T).Name);

    object IComObject.Object => Object;

    public Type InterfaceType => typeof(T);

    public bool IsDisposed => _object is null;

    public void AddRef()
    {
        ObjectDisposedException.ThrowIf(_object is null, typeof(T));
        _ = Marshal.AddRef(_pointer);
    }

    public void Dispose()
    {
        if (_object is null)
        {
            return;
        }

        // 顺序不能反：先放掉自己接管的那份原生引用，再把 RCW 引用置空。
        // 若先置空再 Release，并发访问 Object 会拿到已释放的对象。
        nint pointer = _pointer;
        _pointer = 0;
        _object = null;

        if (pointer != 0)
        {
            _ = Marshal.Release(pointer);
        }

        // 有终结器就必须抑制它：否则这份引用会被 Release 两次（第二次在终结队列上）。
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 兜底：调用方忘了 <see cref="Dispose"/> 时，别让那份原生引用活到进程结束。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-11-11 评审 M2：构造函数自己 AddRef 了一份（见上面的说明），而托管包装器
    /// （RCW）的终结器**不管这一份**——不 Dispose 就是净 +1，长会话里"每次资源请求/菜单操作
    /// 泄漏一个引用"就是这么来的。
    /// </para>
    /// <para>
    /// 代价要说清：有终结器的对象会被提升到终结队列（多活一轮 GC）。这些包装器都是
    /// "取出来就用完"的短生命周期对象，代价可接受；真正该做的仍是调用方
    /// <see cref="Dispose"/>（确定性释放）。
    /// </para>
    /// </remarks>
    ~ComObject() => Dispose();
}
