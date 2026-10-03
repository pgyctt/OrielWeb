using System.Runtime.InteropServices;

namespace OrielWeb.Platform.MacOS.Interop;

// ============================================================================
// Objective-C runtime + AppKit/WebKit 互操作核心（纯 P/Invoke，无 C++ 组件）。
//
// 关键约定（全部经过 ABI 分析）：
//  * objc_msgSend 按具体签名多次声明（EntryPoint 指向同一导出）；
//  * NSPoint/NSSize/NSRect 参数是 double 聚合——x64 SysV 与 arm64 都把
//    整数/浮点参数分属不同寄存器组，因此 .NET 声明
//    (void* self, void* sel, double…) 与原生 (self, _cmd, NSPoint…) 槽位一致；
//  * 不做 struct 返回的 msgSend（窗口原点等由 JS 侧提供 + 宿主跟踪增量）；
//  * 回调经手工类对（objc_allocateClassPair + class_addMethod）+ [UnmanagedCallersOnly]，
//    与 Windows 侧的手工 CCW 同一思想。
// ============================================================================

internal static unsafe partial class ObjCRuntime
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // dlopen 的 mode 取值（/usr/include/dlfcn.h）：RTLD_NOW=0x2、RTLD_GLOBAL=0x8
    private const int RtldNow = 2;
    private const int RtldGlobal = 8;

    [LibraryImport(LibSystem, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen(string path, int mode);

    [LibraryImport(LibSystem, EntryPoint = "dlerror")]
    private static partial nint dlerror();

    // ---- runtime 基础 ----

    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport(ObjCLib)]
    internal static partial void objc_registerClassPair(nint cls);

    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool class_addMethod(nint cls, nint selector, void* imp, string typeEncoding);

    [LibraryImport(ObjCLib)]
    internal static partial nint objc_retain(nint obj);

    [LibraryImport(ObjCLib)]
    internal static partial void objc_release(nint obj);

    // ---- objc_msgSend 按签名声明 ----

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendId(nint self, nint sel);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint self, nint sel);

    /// <summary>
    /// 无参数、返回 <c>double</c> 的调用（如 <c>+[NSEvent doubleClickInterval]</c>、
    /// <c>-[NSScreen backingScaleFactor]</c>）。
    /// </summary>
    /// <remarks>
    /// 不能拿 <see cref="SendId"/> 代用：返回浮点与返回整数在 arm64 上走不同的寄存器组，
    /// 这正是本文件按签名多次声明 objc_msgSend 的原因。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial double SendDouble(nint self, nint sel);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObj(nint self, nint sel, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjObj(nint self, nint sel, nint a, nint b);

    /// <summary>两个对象参数、返回对象（如 <c>readObjectsForClasses:options:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjObj(nint self, nint sel, nint a, nint b);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidBool(nint self, nint sel, [MarshalAs(UnmanagedType.Bool)] bool arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidNint(nint self, nint sel, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObj(nint self, nint sel, nint arg);

    /// <summary>一个 double 参数的调用（如 <c>NSStatusBar.statusItemWithLength:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble(nint self, nint sel, double arg);

    /// <summary>三个对象参数的调用（如 <c>NSMenuItem initWithTitle:action:keyEquivalent:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjObjObj(nint self, nint sel, nint a, nint b, nint c);

    /// <summary>
    /// 三个对象参数且无返回值的调用。类方法也走它（self 位置传类对象），
    /// 例如 <c>NSMenu popUpContextMenu:withEvent:forView:</c>。
    /// </summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjObjObj(nint self, nint sel, nint a, nint b, nint c);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjBool(nint self, nint sel, nint arg, [MarshalAs(UnmanagedType.Bool)] bool flag);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdNint(nint self, nint sel, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidNintBool(nint self, nint sel, nint arg, [MarshalAs(UnmanagedType.Bool)] bool flag);

    // NSPoint/NSSize（2×double）与 NSRect（4×double）参数——见文件头说明
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble2(nint self, nint sel, double a, double b);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble4(nint self, nint sel, double a, double b, double c, double d);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble4Obj(nint self, nint sel, double a, double b, double c, double d, nint obj);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble4NuintNuintBool(nint self, nint sel, double a, double b, double c, double d, nuint style, nuint backing, [MarshalAs(UnmanagedType.Bool)] bool defer);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble4NuintNuintBool(nint self, nint sel, double a, double b, double c, double d, nuint style, nuint backing, [MarshalAs(UnmanagedType.Bool)] bool defer);

    // 仅用于参数类型是 const char*（UTF-8 C 字符串）的 selector，例如
    // +[NSString stringWithUTF8String:]。若 selector 期望的是 NSString*，必须先 MakeNSString
    // 造对象再走 SendIdObj 系列——把 char* 当 NSString* 传，WebKit 一问对象类型就会在
    // CoreFoundation 的 __CF_IS_OBJC 里 __builtin_trap（真机 CI 上实测到的崩溃点）。
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SendIdUtf8(nint self, nint sel, string utf8);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjNintBool(nint self, nint sel, nint obj, nint arg, [MarshalAs(UnmanagedType.Bool)] bool flag);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjNint(nint self, nint sel, nint obj, nint arg);

    /// <summary>
    /// 四个对象/选择器参数的消息，例如
    /// <c>addObserver:selector:name:object:</c>（observer、SEL、通知名、object）。
    /// </summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjSelObjObj(nint self, nint sel, nint observer, nint selector, nint name, nint obj);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidSelObjBool(nint self, nint sel, nint selectorArg, nint objectArg, [MarshalAs(UnmanagedType.Bool)] bool waitUntilDone);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendBoolRet(nint self, nint sel);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendBoolRetObj(nint self, nint sel, nint arg);

    /// <summary>三个对象参数、返回 BOOL（如 <c>NSApplication sendAction:to:from:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendBoolRetObjObjObj(nint self, nint sel, nint a, nint b, nint c);

    // ---- 框架加载 ----

    private static readonly string[] Frameworks =
    [
        "/System/Library/Frameworks/Foundation.framework/Foundation",
        "/System/Library/Frameworks/AppKit.framework/AppKit",
        "/System/Library/Frameworks/WebKit.framework/WebKit",
    ];

    private static int s_frameworksLoaded;

    /// <summary>
    /// 加载 Foundation/AppKit/WebKit（幂等），必须在任何 objc 类查找与建窗之前调用。
    ///
    /// 为什么必须显式做：macOS 后端是纯 P/Invoke，只链接 libobjc，不链接任何框架；
    /// 而产物是无 bundle 的裸可执行文件。于是 objc_getClass("NSApplication") / "NSWindow" /
    /// "WKWebView" 全部返回 nil，而 ObjC 向 nil 发消息是**静默 no-op**——表现为"进程不崩、
    /// 不报错、直接以 0 退出，但从来没有窗口、也没有 WebKit 子进程"（真机 CI 上实测到的现象）。
    /// </summary>
    internal static void LoadFrameworks()
    {
        if (System.Threading.Interlocked.Exchange(ref s_frameworksLoaded, 1) == 1)
        {
            return;
        }

        foreach (var framework in Frameworks)
        {
            if (dlopen(framework, RtldNow | RtldGlobal) == 0)
            {
                var error = dlerror();
                var message = error == 0 ? "（dlerror 无信息）" : Marshal.PtrToStringUTF8(error);
                throw new InvalidOperationException($"加载 {framework} 失败：{message}");
            }
        }
    }

    // ---- 辅助 ----

    internal static nint GetClass(string name) => objc_getClass(name);

    /// <summary>
    /// 取类，取不到即抛。用于窗口/webview 这类关键类：把"类不存在 → 后续调用全部静默
    /// no-op"这个最难排查的失败模式，转换成一条明确的异常。
    /// </summary>
    internal static nint GetClassOrThrow(string name)
    {
        var cls = objc_getClass(name);
        return cls != 0
            ? cls
            : throw new InvalidOperationException($"找不到 Objective-C 类 {name}：对应的框架可能未加载。");
    }

    internal static nint Sel(string name) => sel_registerName(name);

    /// <summary>[[NSString stringWithUTF8String:]]（autoreleased，主循环自动排空）。</summary>
    internal static nint MakeNSString(string value)
        => SendIdUtf8(GetClass("NSString"), Sel("stringWithUTF8String:"), value);

    // ---- 协议与追加的 msgSend 签名（oriel:// 的应答需要）----

    /// <summary>按名字取协议对象（如 <c>WKURLSchemeHandler</c>）。</summary>
    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_getProtocol(string name);

    /// <summary>
    /// 给类声明一个协议。
    /// </summary>
    /// <remarks>
    /// 不是形式主义：<c>[WKWebViewConfiguration setURLSchemeHandler:forURLScheme:]</c> 会检查
    /// <c>conformsToProtocol:</c>，没声明就抛 <c>NSInvalidArgumentException</c>——而托管侧看到的是
    /// "建窗崩了"，很难联想到协议。
    /// </remarks>
    [LibraryImport(ObjCLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool class_addProtocol(nint cls, nint protocol);

    /// <summary>两个整数参数（如 <c>+[NSData dataWithBytes:length:]</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdNintNint(nint self, nint sel, nint a, nint b);

    /// <summary>对象 + 整数 + 对象（如 <c>+[NSError errorWithDomain:code:userInfo:]</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjNintObj(nint self, nint sel, nint obj, nint integer, nint tail);

    /// <summary>对象 + 整数 + 两个对象（如 <c>-[NSHTTPURLResponse initWithURL:statusCode:HTTPVersion:headerFields:]</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjNintObjObj(nint self, nint sel, nint a, nint integer, nint b, nint c);

    /// <summary>[[NSString UTF8String]] → 托管字符串（指针仅在当前调用/池内有效，立即复制）。</summary>
    internal static string ToManagedString(nint nsString)
    {
        var utf8 = SendId(nsString, Sel("UTF8String"));
        return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }
}
