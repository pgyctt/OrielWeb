namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>
/// 把 WebView2 的 COM 回调挡在异常边界之内。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这一层</b>（2026-11-11 评审 H3）：异常穿过 COM 边界 = 进程 fail-fast。
/// WebView2 的回调发生在它自己的消息泵里，那里**没有调用方**可以接住异常——
/// 应用代码里的一个空引用就能带走整个宿主进程，而且崩在引擎的调用栈上，看不出是谁的错。
/// </para>
/// <para>
/// 这份实现照 <c>Win32DropTarget</c>（评审认定的手写 COM 范本）的同一套工艺：
/// 全身 try/catch + 失败返回错误 HRESULT + 诊断走 <see cref="OrielDiagnostics"/>。
/// </para>
/// </remarks>
internal static class ComCallbackGuard
{
    /// <summary><c>E_FAIL</c>：回调内部失败时返回的错误 HRESULT。</summary>
    /// <remarks>
    /// 用 E_FAIL 而不是 S_OK：WebView2 会据此按"这次回调失败"处理。返回 S_OK 等于把
    /// "宿主处理失败了"伪装成"一切正常"，而那种伪装之后会表现为更难查的怪状态
    /// （比如"脚本执行了但回调没生效"）。
    /// </remarks>
    internal const int EFail = unchecked((int)0x80004005);

    /// <summary>
    /// 执行一次回调，把异常挡在 COM 边界之内。
    /// </summary>
    /// <param name="callback">回调本体。</param>
    /// <param name="what">回调名（进诊断文案，用于定位是哪一条回调出的错）。</param>
    /// <returns>回调的返回值；回调抛异常时返回 <see cref="EFail"/>。</returns>
    internal static int Run(Func<int> callback, string what)
    {
        try
        {
            return callback();
        }
        catch (Exception ex)
        {
            // 记全部异常，不做分类：这一层的职责是"别让进程死"，而不是"替调用方决定
            // 哪种异常算正常"。分类只会让某类异常重新变成 fail-fast。
            OrielDiagnostics.Report($"[OrielWeb] WebView2 回调「{what}」处理失败，已挡在 COM 边界内：{ex}");
            return EFail;
        }
    }

    /// <summary>
    /// 触发一个用户事件（订阅者可能抛任何异常）。
    /// </summary>
    /// <param name="raise">触发本体（通常是 <c>某个Event?.Invoke(…)</c>）。</param>
    /// <param name="what">事件名（进诊断文案）。</param>
    /// <remarks>
    /// <b>为什么这里吞掉异常、且不改变返回值</b>：事件订阅者抛异常是**应用代码的问题**，
    /// 但它的后果不该是"宿主进程死"（见 <see cref="Run"/>），也不该是"这次导航/消息处理
    /// 被引擎判定为失败"（那会把一个页面 handler 的空引用变成一次静默的功能异常）。
    /// 所以只报诊断、照旧返回 S_OK——同时也保证"一个订阅者抛异常不影响后续事件"，
    /// 因为异常没有机会离开这一层。
    /// </remarks>
    internal static void Raise(Action raise, string what)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 事件「{what}」的订阅者抛出异常，已挡在 COM 边界内：{ex}");
        }
    }
}
