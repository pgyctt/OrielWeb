namespace OrielWeb.Ipc;

/// <summary>IPC 层错误（参数缺失、类型不符、命令未知等）。消息会回传给 JS 侧的 Promise reject。</summary>
public class OrielIpcException : Exception
{
    public OrielIpcException(string message) : base(message) { }
}
