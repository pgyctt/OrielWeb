namespace OrielWeb;

/// <summary>
/// <see cref="ITrayEventProbe"/> 的派发实现：三个后端的事件签名完全一致，
/// 所以"合成一次事件"的逻辑只写这一份。
/// </summary>
/// <remarks>
/// 让各后端把委托交进来、而不是各自 copy 一遍 switch：抄三份的代价不是行数，
/// 而是"其中一份写错了订阅路径"——那正好是探针本该挡住的那类错误。
/// 传委托（而不是传后端实例）也让本类不必认识三个平台类型。
/// </remarks>
internal static class TrayEventProbeDispatch
{
    /// <summary>按 <paramref name="kind"/> 触发对应环节的订阅者。</summary>
    internal static void Raise(
        string trayId,
        Action? clicked,
        Action<OrielTrayEvent>? eventReceived,
        Action<string>? menuItemClicked,
        TraySyntheticEvent kind,
        string? itemId)
    {
        switch (kind)
        {
            case TraySyntheticEvent.Clicked:
                clicked?.Invoke();
                break;

            case TraySyntheticEvent.TrayEvent:
                // 形状取"左键抬起"这一最常见的交互：真实点击到达时，
                // Clicked 与 TrayEvent 本来也是同一次交互的两面。
                eventReceived?.Invoke(new OrielTrayEvent(
                    trayId,
                    OrielTrayEventType.Click,
                    OrielTrayMouseButton.Left,
                    OrielTrayButtonState.Up,
                    new OrielTrayPoint(0, 0)));
                break;

            case TraySyntheticEvent.MenuItemClicked:
                // 调用方通常要断言"参数原样到达"，所以 id 由它给；不给时用一个稳定的哨兵。
                menuItemClicked?.Invoke(itemId ?? "selftest-item");
                break;
        }
    }
}
