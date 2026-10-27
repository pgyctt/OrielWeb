namespace OrielWeb.Platform.Windows.WebView2.Com;

/// <summary>
/// 一个 COM 接口引用的托管句柄：持有原生指针与对应的 RCW，并提供确定性释放。
/// </summary>
/// <remarks>
/// 与 DirectN/WebView2Aot 的 <c>IComObject</c> 形状一致，是为了让上层代码在切换绑定时
/// 只改 using、不改调用点。语义上有两点必须保持：
/// <list type="bullet">
/// <item>从 <c>out</c> 参数拿到的引用**归调用方所有**，包装层接管它；</item>
/// <item><see cref="IDisposable.Dispose"/> 只释放包装层接管的那一份，RCW 自己的引用交给 GC。</item>
/// </list>
/// </remarks>
internal interface IComObject : IDisposable
{
    /// <summary>底层 RCW。释放后访问抛 <see cref="ObjectDisposedException"/>。</summary>
    object Object { get; }

    /// <summary>接口类型（诊断用）。</summary>
    Type InterfaceType { get; }

    bool IsDisposed { get; }

    /// <summary>给原生引用再加一个计数，用于把同一对象交给两个所有者。</summary>
    void AddRef();
}

/// <summary>强类型的 <see cref="IComObject"/>。</summary>
internal interface IComObject<out T> : IComObject where T : class
{
    new T Object { get; }
}
