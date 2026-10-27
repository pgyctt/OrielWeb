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
    }
}
