using System.Runtime.InteropServices;

namespace OrielConformance;

/// <summary>
/// 自检用的环境探针：当前进程是否处在一个**有图形会话**的 macOS 里。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自检需要它</b>：macOS 的窗口只要没有 WindowServer 会话就"存在但不可见"——
/// 最小化不生效、<c>OuterSize</c> 读回 0。那几条断言验的是"环境给不给窗口服务器"，
/// 不是库的行为。
/// </para>
/// <para>
/// <b>典型场景</b>：通过 SSH 跑自检、而机器停在登录窗口（<c>stat -f%Su /dev/console</c>
/// 是 <c>root</c>）时就是这种状态。Hyper-V 的 macOS VM 默认如此——不登录图形会话，
/// 86 条断言照样过，但最小化/外框尺寸那 3 条必然失败。
/// </para>
/// <para>
/// 判据取 <c>CGSessionCopyCurrentDictionary()</c>：**不在图形会话里时它返回 NULL**，
/// 这是 macOS 自己用来回答"我这个进程在不在 Aqua 会话里"的函数。更弱的信号是
/// <c>NSScreen.screens.count</c>——实测停在登录窗口时它**仍然非空**，所以只当退路。
/// </para>
/// </remarks>
internal static class MacDisplay
{
    private static readonly Lazy<bool> s_hasScreenSession = new(Detect, isThreadSafe: true);

    /// <summary>是否有图形会话（非 macOS 恒 <c>true</c>，交由各自的判据处理）。</summary>
    internal static bool HasScreenSession => s_hasScreenSession.Value;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetClassFn(nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SelRegisterFn(nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SendNoArgs(nint receiver, nint selector);

    /// <summary><c>CFDictionaryRef CGSessionCopyCurrentDictionary(void)</c>。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CopySessionFn();

    private static bool Detect()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true;
        }

        try
        {
            nint libobjc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
            var getClass = Marshal.GetDelegateForFunctionPointer<GetClassFn>(
                NativeLibrary.GetExport(libobjc, "objc_getClass"));
            var selRegister = Marshal.GetDelegateForFunctionPointer<SelRegisterFn>(
                NativeLibrary.GetExport(libobjc, "sel_registerName"));
            var send = Marshal.GetDelegateForFunctionPointer<SendNoArgs>(
                NativeLibrary.GetExport(libobjc, "objc_msgSend"));

            // 首选：CoreGraphics 的会话字典（不在图形会话里返回 NULL）
            if (NativeLibrary.TryLoad(
                    "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
                    out nint coreGraphics)
                && NativeLibrary.TryGetExport(coreGraphics, "CGSessionCopyCurrentDictionary", out nint sessionFn))
            {
                var copySession = Marshal.GetDelegateForFunctionPointer<CopySessionFn>(sessionFn);
                return copySession() != 0;
            }

            // 退路：屏幕数组（信号偏弱，见注释）
            nint screenClass = WithUtf8("NSScreen", name => getClass(name));
            if (screenClass == 0)
            {
                return true;
            }

            nint screens = send(screenClass, WithUtf8("screens", name => selRegister(name)));
            if (screens == 0)
            {
                return false;
            }

            return send(screens, WithUtf8("count", name => selRegister(name))) > 0;
        }
        catch (Exception)
        {
            // 探不到就按"有会话"处理：那是更严格的一侧（不跳过任何断言），
            // 失败信息会指向真正的问题，而不是被一条环境跳过掩盖。
            return true;
        }
    }

    /// <summary>把 C# 字符串转成临时的 UTF-8 原生串，用完立刻释放。</summary>
    private static nint WithUtf8(string value, Func<nint, nint> use)
    {
        nint pointer = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return use(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
