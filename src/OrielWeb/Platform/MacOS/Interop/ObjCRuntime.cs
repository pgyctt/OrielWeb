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

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObj(nint self, nint sel, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjObj(nint self, nint sel, nint a, nint b);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidBool(nint self, nint sel, [MarshalAs(UnmanagedType.Bool)] bool arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidNint(nint self, nint sel, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObj(nint self, nint sel, nint arg);

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

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SendIdUtf8(nint self, nint sel, string utf8);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SendIdUtf8NintBool(nint self, nint sel, string utf8, nint arg, [MarshalAs(UnmanagedType.Bool)] bool flag);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjNint(nint self, nint sel, nint obj, nint arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidSelObjBool(nint self, nint sel, nint selectorArg, nint objectArg, [MarshalAs(UnmanagedType.Bool)] bool waitUntilDone);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendBoolRet(nint self, nint sel);

    // ---- 辅助 ----

    internal static nint GetClass(string name) => objc_getClass(name);

    internal static nint Sel(string name) => sel_registerName(name);

    /// <summary>[[NSString stringWithUTF8String:]]（autoreleased，主循环自动排空）。</summary>
    internal static nint MakeNSString(string value)
        => SendIdUtf8(GetClass("NSString"), Sel("stringWithUTF8String:"), value);

    /// <summary>[[NSString UTF8String]] → 托管字符串（指针仅在当前调用/池内有效，立即复制）。</summary>
    internal static string ToManagedString(nint nsString)
    {
        var utf8 = SendId(nsString, Sel("UTF8String"));
        return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }
}
