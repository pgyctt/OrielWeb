namespace OrielWeb.Ipc;

/// <summary>应用 IPC 命令执行完成（<see cref="OrielApp.CommandInvoked"/> 的参数）。</summary>
/// <remarks>
/// <para>
/// 只覆盖**应用注册的命令**（<c>todo.add</c> 这类）：内建 <c>win.*</c> 不在此列——拖动期间的
/// <c>win.dragTo</c> 每秒会来几十条，混进来只会淹没"哪条命令慢"这个问题。
/// </para>
/// <para>
/// 成功与失败都会触发；失败时额外触发 <see cref="OrielApp.CommandFailed"/>（异常原样带出）。
/// 被门禁拒绝的调用**不算命令执行**（那是 <see cref="OrielApp.IpcRejected"/> 的事）。
/// </para>
/// </remarks>
public sealed class OrielCommandInvokedEventArgs
{
    internal OrielCommandInvokedEventArgs(string name, TimeSpan duration, bool succeeded, string? documentUrl)
    {
        Name = name;
        Duration = duration;
        Succeeded = succeeded;
        DocumentUrl = documentUrl;
    }

    /// <summary>命令名（页面自己发来的那个）。</summary>
    public string Name { get; }

    /// <summary>命令执行耗时：含参数读取/反序列化，不含回执投递与门禁校验。</summary>
    public TimeSpan Duration { get; }

    /// <summary>是否成功（失败时看 <see cref="OrielApp.CommandFailed"/> 拿异常）。</summary>
    public bool Succeeded { get; }

    /// <summary>发起调用的文档 URL；平台或场景取不到时为 <c>null</c>。</summary>
    public string? DocumentUrl { get; }
}

/// <summary>应用 IPC 命令抛异常（<see cref="OrielApp.CommandFailed"/> 的参数）。</summary>
/// <remarks>
/// 异常**原样**带出（含内部路径、类型名等）：这是只给宿主的通道，页面拿到的仍是
/// <see cref="OrielIpcException"/> 之外统一净化后的文案（见 <c>DescribeForPage</c>）。
/// </remarks>
public sealed class OrielCommandFailedEventArgs
{
    internal OrielCommandFailedEventArgs(string name, Exception exception, TimeSpan duration, string? documentUrl)
    {
        Name = name;
        Exception = exception;
        Duration = duration;
        DocumentUrl = documentUrl;
    }

    /// <summary>命令名。</summary>
    public string Name { get; }

    /// <summary>命令抛出的原始异常。</summary>
    public Exception Exception { get; }

    /// <summary>到抛出为止的耗时（含参数读取/反序列化）。</summary>
    public TimeSpan Duration { get; }

    /// <summary>发起调用的文档 URL；平台或场景取不到时为 <c>null</c>。</summary>
    public string? DocumentUrl { get; }
}
