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

    /// <summary>取对象的类（<c>object_getClass</c>，与 <c>[obj class]</c> 的区别在 KVO 子类上）。</summary>
    [LibraryImport(ObjCLib)]
    internal static partial nint object_getClass(nint obj);

    /// <summary>取实例方法（不存在时返回 0）。B22 用它判断"某个 selector 是否真的实现了"。</summary>
    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint class_getInstanceMethod(nint cls, string name);

    /// <summary>取实例变量（不存在时返回 0）。B22 用它判断 KVC 能否安全地走 ivar。</summary>
    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint class_getInstanceVariable(nint cls, string name);

    /// <summary>
    /// 取**类方法**（不存在时返回 0）。B23 用它判断 <c>+[WKWebsiteDataStore dataStoreForIdentifier:]</c>
    /// 在旧系统上是否存在——向不认识的 selector 发消息会抛 <c>NSInvalidArgumentException</c>，
    /// 那是 ObjC 异常、托管侧接不住。
    /// </summary>
    [LibraryImport(ObjCLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint class_getClassMethod(nint cls, string name);

    [LibraryImport(ObjCLib)]
    internal static partial void objc_release(nint obj);

    // ---- objc_msgSend 按签名声明 ----

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendId(nint self, nint sel);

    /// <summary>无参数、返回 <c>NSUInteger</c> 的调用（如 <c>-[NSArray count]</c>）。</summary>
    /// <remarks>
    /// 与 <see cref="SendId"/> 同一寄存器口径，但语义不同：用它而不是复用 SendId，
    /// 是为了让调用点一眼能看出"这是长度不是对象"——返回指针当整数用是 ObjC 互操作里
    /// 最常见的静默错误来源。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNint(nint self, nint sel);

    /// <summary>无参数、返回 32 位无符号整数的调用（如 <c>-[NSNumber unsignedIntValue]</c>）。</summary>
    /// <remarks>
    /// 不能用 <see cref="SendId"/> 代用：64 位 ABI 下返回 <c>uint</c> 的函数只保证低 32 位有效，
    /// 按指针宽度读会把高位残留当成数值的一部分。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial uint SendUint(nint self, nint sel);

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

    /// <summary>
    /// 两个对象参数 + 一个 BOOL、无返回值的调用（如 <c>NSApplication postEvent:atStart:</c>）。
    /// </summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjObjBool(nint self, nint sel, nint a, nint b, [MarshalAs(UnmanagedType.Bool)] bool flag);

    /// <summary>两个对象参数、返回对象（如 <c>readObjectsForClasses:options:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjObj(nint self, nint sel, nint a, nint b);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidBool(nint self, nint sel, [MarshalAs(UnmanagedType.Bool)] bool arg);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidNint(nint self, nint sel, nint arg);

    /// <summary>
    /// 一个 <c>CGFloat</c>（double）参数、无返回值的调用（如 <c>NSStatusItem setLength:</c>）。
    /// </summary>
    /// <remarks>
    /// <b>别用 <see cref="SendVoidNint"/> 代替它</b>：<c>CGFloat</c> 是浮点，arm64 上浮点参数走
    /// <c>d0-d7</c>、整数走 <c>x0-x7</c>——把 CGFloat 塞进整数寄存器，被调方读到的就是垃圾值。
    /// 实测后果是 **SIGSEGV**（崩溃报告：<c>EXC_BAD_ACCESS</c> + "possible pointer authentication
    /// failure"，崩在 <c>objc_msgSend</c>），而且**非确定性**：寄存器里恰好残留什么决定崩不崩，
    /// 于是同一条代码路径有时过、有时崩。这类错误只有真机 + 正确签名才能根治。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble(nint self, nint sel, double arg);

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

    /// <summary>
    /// 调一个"收 <c>NSInteger</c>、返回 <c>NSInteger</c>"的 selector。
    /// </summary>
    /// <remarks>
    /// 与 <c>SendIdNint</c> 的区别在**返回值类型**：那个返回对象指针，这个返回整数值。
    /// 两者在 x86_64 上都走同一套整数寄存器，但类型必须选对——
    /// 不能"反正都是 8 字节"地混用，否则换个架构（ARM64 的浮点/整数返回约定不同）就出事。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNintNint(nint self, nint sel, nint arg);

    // NSPoint/NSSize（2×double）与 NSRect（4×double）参数——见文件头说明
    /// <summary>一个对象参数 + 两个 double（NSSize）的调用（如 <c>NSImage initWithCGImage:size:</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjDouble2(nint self, nint sel, nint obj, double a, double b);

    /// <summary>
    /// 一个对象 + 两个 double（NSPoint）+ 再一个对象的调用
    /// （如 <c>+[NSMenu popUpMenuPositioningItem:atLocation:inView:]</c>）。
    /// </summary>
    /// <remarks>
    /// 参数顺序必须与原生逐槽对应：<c>item</c> 走整数寄存器、<c>location</c> 走浮点寄存器、
    /// <c>view</c> 再回到整数寄存器。写成"两个 double 放最后"的形态（<see cref="SendIdObjDouble2"/>）
    /// 会让 view 被塞进浮点槽，AppKit 收到一个野指针——表现为弹出时崩在离这里很远的地方。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdObjDouble2Obj(nint self, nint sel, nint obj, double a, double b, nint tail);

    /// <summary>
    /// <c>SEL</c> + 一个对象 + 一个 double + 再一个对象的调用
    /// （如 <c>performSelector:withObject:afterDelay:inModes:</c>）。
    /// </summary>
    /// <remarks>
    /// 槽位顺序是 <c>self, _cmd, SEL, id, double, NSArray*</c>——**两个整数槽**（SEL 与
    /// withObject: 的 id）在 double 之前。少写那个 id 槽会让 double 落进整数寄存器，
    /// 延迟被当成一个指针值，表现是定时器立刻触发或永不触发。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidSelObjDoubleObj(
        nint self, nint sel, nint targetSel, nint arg, double delay, nint tail);

    /// <summary>
    /// 四个 double（NSRect）+ 一个整数 + 两个对象参数的调用
    /// （如 <c>NSTrackingArea initWithRect:options:owner:userInfo:</c>）。
    /// </summary>
    /// <remarks>
    /// <c>options</c> 是 <c>NSTrackingAreaOptions</c>（NSUInteger）：x64/arm64 上它占一个整数寄存器，
    /// 必须排在 4 个 double 之后、两个对象之前，与原生签名逐槽对应。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble4NintObjObj(
        nint self, nint sel, double a, double b, double c, double d, nuint options, nint obj1, nint obj2);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble2(nint self, nint sel, double a, double b);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble4(nint self, nint sel, double a, double b, double c, double d);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble4Obj(nint self, nint sel, double a, double b, double c, double d, nint obj);

    /// <summary>四个 double 参数、返回对象（如 <c>+[NSColor colorWithSRGBRed:green:blue:alpha:]</c>）。</summary>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdDouble4(nint self, nint sel, double a, double b, double c, double d);

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
    /// 对象 + 整数 + 对象三个参数、返回 void 的消息，例如
    /// <c>addSubview:positioned:relativeTo:</c>（B22 的材质视图要插到 webview 之下）。
    /// </summary>
    /// <remarks>
    /// <b>不能用 <see cref="SendIdObjNintObj"/> 代用</b>：那个的返回类型是对象指针。
    /// x64/arm64 上"忽略返回值"实际不会出事，但两处的参数寄存器口径是同一套、
    /// 语义却不同——混用会让后来的人以为这是个有返回值的调用。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidObjNintObj(nint self, nint sel, nint obj, nint integer, nint tail);

    /// <summary>
    /// 四个 double + 一个 BOOL 的消息，例如 <c>setFrame:display:</c>（B22 的简化全屏）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SendVoidDouble4NuintNuintBool"/> 同一口径：4 个 double 走浮点寄存器，
    /// BOOL 走第一个整数寄存器。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble4Bool(nint self, nint sel, double a, double b, double c, double d, [MarshalAs(UnmanagedType.Bool)] bool flag);

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
        // CoreGraphics 由 AppKit 间接依赖，但图标路径直接调 CGImageCreate——
        // 显式加载它，免得依赖"AppKit 恰好把它带进来了"这种巧合。
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
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

    // ---- CoreGraphics（图标：RGBA → CGImage）----

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>把一块内存包成 <c>CGDataProvider</c>（<c>CGImageCreate</c> 的数据源）。</summary>
    /// <remarks>
    /// 用 <c>CGDataProviderCreateWithData</c> 而不是 <c>…WithCFData</c>：后者要在托管侧造
    /// <c>CFData</c>，多一层引用管理。这里把托管数组 pin 住交给它，release 回调里再解开。
    /// </remarks>
    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGDataProviderCreateWithData")]
    internal static partial nint CGDataProviderCreateWithData(nint info, nint data, nuint size, nint releaseData);

    /// <summary>由像素数据造 <c>CGImage</c>。返回的对象归调用方，用完 <c>CGImageRelease</c>。</summary>
    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGImageCreate")]
    internal static partial nint CGImageCreate(
        nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow,
        nint colorSpace, uint bitmapInfo, nint provider, nint decode, [MarshalAs(UnmanagedType.Bool)] bool shouldInterpolate, int intent);

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGImageRelease")]
    internal static partial void CGImageRelease(nint image);

    /// <summary>取一个设备无关的 sRGB 色彩空间（RGBA 图标的正确色彩解释）。</summary>
    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGColorSpaceCreateDeviceRGB")]
    internal static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGColorSpaceRelease")]
    internal static partial void CGColorSpaceRelease(nint colorSpace);

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGDataProviderRelease")]
    internal static partial void CGDataProviderRelease(nint provider);

    /// <summary>
    /// 造一个"应用自定义"空事件：<c>+[NSEvent otherEventWithType:location:modifierFlags:
    /// timestamp:windowNumber:context:subtype:data1:data2:]</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用途只有一个：<c>NSApplication.stop:</c> 之后投它唤醒 run loop（见
    /// <c>MacOSPlatformBackend.Quit</c>）。<c>stop:</c> 是"处理完**当前**事件后退出循环"
    /// （Apple 文档原话），若此刻队列里没有事件，循环会一直等到下一个真实事件才检查标志——
    /// 表现为"调了 Quit 却退不掉"。
    /// </para>
    /// <para>
    /// <b>类型是 <c>NSEvent*</c>，不是 <c>CGEventRef</c></b>：<c>postEvent:atStart:</c> 要的是
    /// 前者，把后者传进去是未定义行为。这里照 [matplotlib 的 _macosx.m](
    /// https://github.com/matplotlib/matplotlib/blob/main/src/_macosx.m) 的标准做法
    /// （<c>stopWithEvent()</c>）用 <c>otherEventWithType:</c> 造 NSEvent。
    /// </para>
    /// <para>
    /// <b>ABI 说明</b>：这个方法混合了整数与浮点参数（<c>NSPoint</c> 与 <c>NSTimeInterval</c>
    /// 都是 double），因此寄存器分配不是"按顺序往后排"那么简单——.NET 的封送层按平台 ABI
    /// 分别处理整数寄存器（x64 的 rdi/rsi/rdx/rcx/r8/r9、arm64 的 x0-x7）与浮点寄存器
    /// （xmm0-、d0-），所以这里的声明顺序只要与 ObjC 签名一致即可。
    /// <c>subtype</c> 原生是 <c>short</c>（会提升为 int），用 <see cref="nint"/> 传 0 是安全的。
    /// </para>
    /// </remarks>
    /// <param name="eventType">NSEventType；<c>NSEventTypeApplicationDefined</c> = 15。</param>
    /// <param name="locationX">事件位置 x（<c>NSZeroPoint</c> 时为 0）。</param>
    /// <param name="locationY">事件位置 y。</param>
    /// <param name="modifierFlags">修饰键掩码。</param>
    /// <param name="timestamp">时间戳（秒）。</param>
    /// <param name="windowNumber">窗口编号（无窗口时为 0）。</param>
    /// <param name="context">已废弃的图形上下文参数（传 0）。</param>
    /// <param name="subtype">子类型（原生 <c>short</c>）。</param>
    /// <param name="data1">自定义数据 1。</param>
    /// <param name="data2">自定义数据 2。</param>
    /// <param name="self">ObjC 接收者。</param>
    /// <param name="sel">selector。</param>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdNintDouble2NuintDoubleNintNintNintNintNint(
        nint self, nint sel,
        nint eventType,
        double locationX, double locationY,
        nuint modifierFlags,
        double timestamp,
        nint windowNumber,
        nint context,
        nint subtype,
        nint data1,
        nint data2);

    /// <summary>[[NSString stringWithUTF8String:]]（autoreleased，主循环自动排空）。</summary>
    internal static nint MakeNSString(string value)
        => SendIdUtf8(GetClass("NSString"), Sel("stringWithUTF8String:"), value);

    /// <summary>
    /// 造一个 <c>NSUUID</c>（autoreleased）。标识符与页面之间走的是"UUID 的 16 字节"，
    /// 而 ObjC 侧只认它自己的对象——两处（建窗绑定存储、删除存储）都从这里造。
    /// </summary>
    /// <remarks>
    /// 用 <c>initWithUUIDString:</c> 而不是 <c>initWithUUIDBytes:</c>：后者要传
    /// <c>const unsigned char *</c>，得先在托管侧钉住数组，而字符串形态没有这个麻烦，
    /// 且 <see cref="Guid.ToString(string?)"/> 的 "D" 格式正是它要的写法。
    /// 返回 0 表示 UUID 字符串没被接受（理论上不会——输入来自 Guid）。
    /// </remarks>
    internal static nint MakeNSUUID(Guid identifier)
        => SendIdObj(
            SendId(GetClass("NSUUID"), Sel("alloc")),
            Sel("initWithUUIDString:"),
            MakeNSString(identifier.ToString("D")));

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

    /// <summary>
    /// <c>objc_msgSend</c> 的 <c>- (BOOL)setX:(id)a forY:(id)b</c> 形态。
    /// </summary>
    /// <remarks>
    /// <c>NSPasteboard</c> 的 <c>setString:forType:</c> 就是这一形。
    /// 返回值是 <c>BOOL</c>（在 arm64 上是 32 位、在 x86_64 上返回寄存器里也是 32 位），
    /// 所以这里用 <see cref="int"/> 接并判非零，而不是 <c>bool</c>——
    /// 用 <c>bool</c> 接一个只保证低 32 位有效的返回值在某些 ABI 上会读到高位垃圾。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial int SendIntObjObj(nint self, nint sel, nint a, nint b);

    /// <summary>
    /// <c>objc_msgSend</c> 的 <c>- (BOOL)setX:(id)a</c> 形态（如 <c>NSPasteboard writeObjects:</c>）。
    /// </summary>
    /// <remarks>返回值同样用 <see cref="int"/> 接，理由见上面 <see cref="SendIntObjObj"/>。</remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial int SendIntObj(nint self, nint sel, nint arg);

    /// <summary>
    /// <c>objc_msgSend</c> 的 <c>- (id)method:(NSInteger)n someObject:(id)obj</c> 形态。
    /// </summary>
    /// <remarks>
    /// <c>NSBitmapImageRep</c> 的 <c>representationUsingType:properties:</c> 是这一形
    /// （第一个参数是枚举，按 <c>NSInteger</c> 传）。剪贴板读图时用它把 TIFF 转成 PNG。
    /// </remarks>
    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIdNintObj(nint self, nint sel, nint integer, nint obj);

    /// <summary>[[NSString UTF8String]] → 托管字符串（指针仅在当前调用/池内有效，立即复制）。</summary>
    internal static string ToManagedString(nint nsString)
    {
        var utf8 = SendId(nsString, Sel("UTF8String"));
        return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }
}
