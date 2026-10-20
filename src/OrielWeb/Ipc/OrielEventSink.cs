namespace OrielWeb.Ipc;

/// <summary>
/// 向发起调用的窗口页面推送一条事件（payload 已是 JSON 文本）。
/// </summary>
/// <param name="eventName">事件名（页面侧 <c>listen</c> 用的那个）。</param>
/// <param name="jsonPayload">已序列化的 JSON 文本。</param>
/// <remarks>
/// <para>
/// 存在的理由：<see cref="IOrielCommandRouter"/> 是**公开**接口（由生成器在用户程序集里实现），
/// 而窗口控制接口 <c>IOrielWindowControl</c> 是 <c>internal</c> 的——公开接口的签名里不能出现
/// 它。用一个只暴露"推一条事件"的委托代替，比把那个十几个成员的接口提升为公开面要小得多。
/// </para>
/// <para>
/// 值为 <c>null</c> 表示这次调用没有来源窗口（例如应用启动阶段的内部调用），
/// 此时接受 <see cref="OrielChannel"/> 参数的命令会以 <c>no_source_window</c> 失败。
/// </para>
/// </remarks>
public delegate void OrielEventSink(string eventName, string jsonPayload);
