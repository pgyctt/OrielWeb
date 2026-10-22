using System.Runtime.InteropServices;

namespace OrielDemo;

/// <summary>
/// 观感自检用的 AppKit 取证器：把一个窗口的**实际观感状态**读回来。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用库内部的字段</b>：库若"记得自己调过 setTitlebarAppearsTransparent"，
/// 那只证明它执行了一条消息，证明不了那条消息落到了 AppKit 上。这里绕开库，
/// 直接对 <c>NSApp</c> 发消息，读的是 AppKit 自己的状态——两者不一致正是要抓的缺陷
/// （典型：消息发给了错误的 receiver、选择器拼错、窗口被替换成了另一个实例）。
/// </para>
/// <para>
/// <b>为什么窗口按 title 找而不是按句柄</b>：库不暴露原生句柄（那是它的封装边界）。
/// 从 <c>NSApp.windows</c> 里按标题匹配是外部观察者能拿到的最强证据；标题由
/// <see cref="OrielWeb.WebviewWindow.Title"/> 设定，自检自己知道期望值。
/// 标题在 overlay 形态下**不可见**但属性仍在，所以匹配不受影响。
/// </para>
/// <para>
/// <b>调用约定</b>：全部走 <c>objc_msgSend</c>。返回值类型必须选对——
/// 返回 <c>BOOL</c> 的用 <c>byte</c>（ObjC 的 BOOL 是 signed char，不是 Win32 的四字节），
/// 返回 <c>CGFloat</c> 的用 <c>double</c>，返回 <c>NSUInteger/NSInteger</c> 的用 <c>nint</c>。
/// 类型错会在寄存器层面读到垃圾，而且**不报错**。
/// </para>
/// </remarks>
internal static class MacWindowProbe
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>NSWindowStyleMask 的位（与库里的常量同值，故意各写一份：取证方不应依赖被测方的常量）。</summary>
    internal const nuint StyleTitled = 1 << 0;
    internal const nuint StyleClosable = 1 << 1;
    internal const nuint StyleMiniaturizable = 1 << 2;
    internal const nuint StyleResizable = 1 << 3;
    internal const nuint StyleFullSizeContentView = 1 << 15;

    // ---- objc_msgSend 的各种返回值形态 ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SendId(nint receiver, nint selector);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SendIdNint(nint receiver, nint selector, nint arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendBool(nint receiver, nint selector);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendVoid(nint receiver, nint selector);

    /// <summary>返回 <c>CGFloat</c>（x86_64 上是 <c>double</c>，走 <c>xmm0</c>）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate double SendDouble(nint receiver, nint selector);

    /// <summary>一个指针参数、无返回值（如 <c>[NSValue getValue:]</c>）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendVoidNint(nint receiver, nint selector, nint arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetClassFn(nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SelRegisterFn(nint name);

    private static readonly Lazy<Api> s_api = new(Load, isThreadSafe: true);

    private sealed class Api
    {
        internal required GetClassFn GetClass { get; init; }
        internal required SelRegisterFn Sel { get; init; }
        internal required SendId Send { get; init; }
        internal required SendIdNint SendNint { get; init; }
        internal required SendBool SendBool { get; init; }
        internal required SendVoid SendVoid { get; init; }
        internal required SendDouble SendDouble { get; init; }
        internal required SendVoidNint SendVoidNint { get; init; }
    }

    private static Api Load()
    {
        nint lib = NativeLibrary.Load(LibObjC);
        return new Api
        {
            GetClass = Marshal.GetDelegateForFunctionPointer<GetClassFn>(
                NativeLibrary.GetExport(lib, "objc_getClass")),
            Sel = Marshal.GetDelegateForFunctionPointer<SelRegisterFn>(
                NativeLibrary.GetExport(lib, "sel_registerName")),
            Send = Marshal.GetDelegateForFunctionPointer<SendId>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
            SendNint = Marshal.GetDelegateForFunctionPointer<SendIdNint>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
            SendBool = Marshal.GetDelegateForFunctionPointer<SendBool>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
            SendVoid = Marshal.GetDelegateForFunctionPointer<SendVoid>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
            SendDouble = Marshal.GetDelegateForFunctionPointer<SendDouble>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
            SendVoidNint = Marshal.GetDelegateForFunctionPointer<SendVoidNint>(
                NativeLibrary.GetExport(lib, "objc_msgSend")),
        };
    }

    /// <summary>是否可用（非 macOS 恒 false）。</summary>
    internal static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>一个窗口的观感状态快照。</summary>
    internal readonly record struct WindowState(
        bool Found,
        nuint StyleMask,
        bool TitlebarTransparent,
        nint TitleVisibility,
        bool IsOpaque,
        double WindowBackgroundAlpha,
        bool HasShadow,
        int EffectViewCount,
        nint EffectMaterial,
        nint EffectState,
        bool WebviewTransparent,
        bool WebviewIsOpaque,
        double WebviewBackgroundAlpha,
        string SubviewSummary,
        string WebviewInnerSummary,
        string EffectFrame,
        bool CloseButtonHidden,
        bool MiniaturizeButtonHidden,
        bool ZoomButtonHidden)
    {
        /// <summary>标题栏是否"系统可见"（样式位带 Titled 且标题可见）。</summary>
        internal bool HasSystemTitlebar => (StyleMask & StyleTitled) != 0 && TitleVisibility == 0;

        /// <summary>内容是否铺到标题栏之下。</summary>
        internal bool ContentExtendsUnderTitlebar => (StyleMask & StyleFullSizeContentView) != 0;
    }

    /// <summary>当前应用注册的窗口数（诊断用）。</summary>
    internal static int WindowCount()
    {
        if (!IsSupported)
        {
            return -1;
        }

        Api api = s_api.Value;
        nint app = api.Send(ClassNamed(api, "NSApplication"), Sel(api, "sharedApplication"));
        if (app == 0)
        {
            return -1;
        }

        nint windows = api.Send(app, Sel(api, "windows"));
        return windows == 0 ? -1 : (int)api.Send(windows, Sel(api, "count"));
    }

    /// <summary>当前应用所有窗口的标题（诊断用：找不到目标窗口时把实际值打出来）。</summary>
    internal static string[] WindowTitles()
    {
        if (!IsSupported)
        {
            return [];
        }

        Api api = s_api.Value;
        nint app = api.Send(ClassNamed(api, "NSApplication"), Sel(api, "sharedApplication"));
        if (app == 0)
        {
            return [];
        }

        nint windows = api.Send(app, Sel(api, "windows"));
        if (windows == 0)
        {
            return [];
        }

        nint count = api.Send(windows, Sel(api, "count"));
        var titles = new string[count];
        for (nint i = 0; i < count; i++)
        {
            nint window = api.SendNint(windows, Sel(api, "objectAtIndex:"), i);
            nint nsTitle = window == 0 ? 0 : api.Send(window, Sel(api, "title"));
            titles[i] = nsTitle == 0 ? "(nil)" : ReadNSString(api, nsTitle);
        }

        return titles;
    }

    /// <summary>按标题取窗口号（<c>CGWindowListCreateImage</c> 用它指定要截的窗口）。</summary>
    internal static uint WindowNumber(string title)
    {
        if (!IsSupported)
        {
            return 0;
        }

        Api api = s_api.Value;
        nint app = api.Send(ClassNamed(api, "NSApplication"), Sel(api, "sharedApplication"));
        if (app == 0)
        {
            return 0;
        }

        nint window = FindWindowByTitle(api, app, title);
        return window == 0 ? 0 : (uint)api.Send(window, Sel(api, "windowNumber"));
    }

    /// <summary>按标题找一个窗口并读回它的观感状态。</summary>
    internal static WindowState Capture(string title)
    {
        if (!IsSupported)
        {
            return default;
        }

        Api api = s_api.Value;
        nint app = api.Send(ClassNamed(api, "NSApplication"), Sel(api, "sharedApplication"));
        if (app == 0)
        {
            return default;
        }

        nint window = FindWindowByTitle(api, app, title);
        if (window == 0)
        {
            return default;
        }

        nint contentView = api.Send(window, Sel(api, "contentView"));

        int effectCount = 0;
        nint effectMaterial = -1;
        nint effectState = -1;
        bool webviewTransparent = false;
        bool webviewIsOpaque = true;
        double webviewBackgroundAlpha = 1.0;
        bool webviewFound = false;
        var inner = new System.Text.StringBuilder();

        var summary = new System.Text.StringBuilder();
        string effectFrame = "(无)";

        // **递归**查找，不只看 contentView 的直接子视图。
        // webview 不在第一层：库把它挂在 OrielDropView 里（contentView → dropView → WKWebView），
        // 拖放事件需要那一层中间视图。只查一层会得到"窗口里没有 webview"的错误结论，
        // 而症状是"webview 不透明"——一个不存在的东西当然读不出透明。
        if (contentView != 0)
        {
            Walk(contentView, depth: 0);

            void Walk(nint parent, int depth)
            {
                if (depth > 6)
                {
                    return; // WKWebView 内部层级很深，够用即可
                }

                nint subviews = api.Send(parent, Sel(api, "subviews"));
                nint count = subviews == 0 ? 0 : api.Send(subviews, Sel(api, "count"));

                for (nint i = 0; i < count; i++)
                {
                    nint view = api.SendNint(subviews, Sel(api, "objectAtIndex:"), i);
                    if (view == 0)
                    {
                        continue;
                    }

                    string className = ClassName(api, view);
                    if (depth == 0)
                    {
                        bool topOpaque = api.SendBool(view, Sel(api, "isOpaque")) != 0;
                        nint topLayer = api.Send(view, Sel(api, "layer"));
                        bool hasLayerBg = topLayer != 0
                            && api.Send(topLayer, Sel(api, "backgroundColor")) != 0;
                        summary.Append(className)
                            .Append("(op=").Append(topOpaque ? '1' : '0')
                            .Append(",bg=").Append(hasLayerBg ? "set" : "nil")
                            .Append(")#").Append(i).Append(' ');
                    }

                    if (className.Contains("VisualEffectView", StringComparison.Ordinal))
                    {
                        effectCount++;
                        if (effectMaterial < 0)
                        {
                            effectMaterial = api.Send(view, Sel(api, "material"));
                            effectState = api.Send(view, Sel(api, "state"));
                            effectFrame = DescribeRect(api, view);
                        }
                    }
                    else if (className.Contains("WKWebView", StringComparison.Ordinal) && !webviewFound)
                    {
                        webviewFound = true;

                        // 诊断：WKWebView 内部是不是还有视图在不透明地画底。
                        // 这是"材质视图明明挂上了、画面却是纯白"的最大嫌疑。
                        CollectOpaqueDescendants(view, 0);

                        void CollectOpaqueDescendants(nint parent, int deep)
                        {
                            if (deep > 3)
                            {
                                return;
                            }

                            nint kids = api.Send(parent, Sel(api, "subviews"));
                            nint kidCount = kids == 0 ? 0 : api.Send(kids, Sel(api, "count"));
                            for (nint k = 0; k < kidCount; k++)
                            {
                                nint kid = api.SendNint(kids, Sel(api, "objectAtIndex:"), k);
                                if (kid == 0)
                                {
                                    continue;
                                }

                                string kidClass = ClassName(api, kid);
                                bool kidOpaque = api.SendBool(kid, Sel(api, "isOpaque")) != 0;
                                nint layer = api.Send(kid, Sel(api, "layer"));
                                double layerAlpha = -1;
                                if (layer != 0)
                                {
                                    nint bg = api.Send(layer, Sel(api, "backgroundColor"));
                                    if (bg != 0)
                                    {
                                        // CGColorGetAlpha 是 C 函数，这里退而求其次：有背景色就记 1
                                        layerAlpha = 1;
                                    }
                                }

                                inner.Append(kidClass).Append("(op=").Append(kidOpaque ? '1' : '0')
                                    .Append(",bg=").Append(layerAlpha < 0 ? "nil" : "set").Append(") ");
                                CollectOpaqueDescendants(kid, deep + 1);
                            }
                        }
                        // 不能读 drawsBackground：macOS 15 起 WKWebView 没有那个私有 key
                        // （ivar 与选择器都没有，实测 class_copyIvarList 扫不出）。
                        // 判据换成公开可见的 backgroundColor 的 alpha——
                        // 页面没画东西的地方显示什么，正是材质能不能透出来的关键。
                        // （isOpaque 不能用：WKWebView 覆写了它，恒返回 YES。）
                        double alpha = 1.0;
                        nint background = api.Send(view, Sel(api, "backgroundColor"));
                        if (background != 0)
                        {
                            alpha = api.SendDouble(background, Sel(api, "alphaComponent"));
                        }

                        webviewIsOpaque = api.SendBool(view, Sel(api, "isOpaque")) != 0;
                        webviewBackgroundAlpha = alpha;
                        webviewTransparent = alpha <= 0.001;
                    }

                    Walk(view, depth + 1);
                }
            }
        }

        return new WindowState(
            Found: true,
            StyleMask: (nuint)api.Send(window, Sel(api, "styleMask")),
            TitlebarTransparent: api.SendBool(window, Sel(api, "titlebarAppearsTransparent")) != 0,
            TitleVisibility: api.Send(window, Sel(api, "titleVisibility")),
            IsOpaque: api.SendBool(window, Sel(api, "isOpaque")) != 0,
            WindowBackgroundAlpha: ReadAlpha(api, api.Send(window, Sel(api, "backgroundColor"))),
            HasShadow: api.SendBool(window, Sel(api, "hasShadow")) != 0,
            EffectViewCount: effectCount,
            EffectMaterial: effectMaterial,
            EffectState: effectState,
            WebviewTransparent: webviewTransparent,
            WebviewIsOpaque: webviewIsOpaque,
            WebviewBackgroundAlpha: webviewBackgroundAlpha,
            SubviewSummary: summary.ToString(),
            WebviewInnerSummary: inner.ToString(),
            EffectFrame: effectFrame,
            CloseButtonHidden: ButtonHidden(api, window, 0),
            MiniaturizeButtonHidden: ButtonHidden(api, window, 1),
            ZoomButtonHidden: ButtonHidden(api, window, 2));
    }

    /// <summary>应用的激活策略（0 = 有 Dock 图标，1 = 无 Dock 图标/附件）。</summary>
    internal static nint ActivationPolicy()
    {
        if (!IsSupported)
        {
            return -1;
        }

        Api api = s_api.Value;
        nint app = api.Send(ClassNamed(api, "NSApplication"), Sel(api, "sharedApplication"));
        return app == 0 ? -1 : api.Send(app, Sel(api, "activationPolicy"));
    }

    /// <summary>状态栏里当前有多少个状态项（托盘图标的宿主）。</summary>
    internal static int StatusItemCount()
    {
        if (!IsSupported)
        {
            return -1;
        }

        Api api = s_api.Value;
        nint statusBar = api.Send(ClassNamed(api, "NSStatusBar"), Sel(api, "systemStatusBar"));
        if (statusBar == 0)
        {
            return -1;
        }

        // _statusItems 是私有的 NSMutableArray；没有公开的计数入口。
        // 读不到就返回 -1（"不可知"），不要伪造成 0——那会让"托盘没建"与"读不到"混在一起。
        nint key = MakeNSString(api, "_statusItems");
        nint items = api.SendNint(statusBar, Sel(api, "valueForKey:"), key);
        if (items == 0)
        {
            return -1;
        }

        return (int)api.Send(items, Sel(api, "count"));
    }

    // ---- 内部 ----

    /// <summary>读一个视图的 frame 并写成文本（尺寸为 0 是"材质视图挂上了但看不见"的典型原因）。</summary>
    private static string DescribeRect(Api api, nint view)
    {
        nint boxed = api.SendNint(view, Sel(api, "valueForKey:"), MakeNSString(api, "frame"));
        if (boxed == 0)
        {
            return "(读不到)";
        }

        nint buffer = Marshal.AllocHGlobal(4 * sizeof(double));
        try
        {
            api.SendVoidNint(boxed, Sel(api, "getValue:"), buffer);
            double x = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 0));
            double y = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 8));
            double w = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 16));
            double h = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 24));
            return $"{x:0.#},{y:0.#} {w:0.#}x{h:0.#}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读一个 NSColor 的 alpha（0 表示完全透明；读不到按 1 处理）。</summary>
    private static double ReadAlpha(Api api, nint color)
    {
        if (color == 0)
        {
            return 1.0;
        }

        return api.SendDouble(color, Sel(api, "alphaComponent"));
    }

    private static bool ButtonHidden(Api api, nint window, nint index)
    {
        nint button = api.SendNint(window, Sel(api, "standardWindowButton:"), index);
        if (button == 0)
        {
            // 按钮不存在（borderless 窗口就是这样）——"看不见"等价于 hidden。
            return true;
        }

        return api.SendBool(button, Sel(api, "isHidden")) != 0;
    }

    private static nint FindWindowByTitle(Api api, nint app, string title)
    {
        nint windows = api.Send(app, Sel(api, "windows"));
        if (windows == 0)
        {
            return 0;
        }

        nint count = api.Send(windows, Sel(api, "count"));
        for (nint i = 0; i < count; i++)
        {
            nint window = api.SendNint(windows, Sel(api, "objectAtIndex:"), i);
            if (window == 0)
            {
                continue;
            }

            nint nsTitle = api.Send(window, Sel(api, "title"));
            if (nsTitle == 0)
            {
                continue;
            }

            if (string.Equals(ReadNSString(api, nsTitle), title, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return 0;
    }

    private static string ClassName(Api api, nint obj)
    {
        nint name = api.Send(obj, Sel(api, "className"));
        return name == 0 ? string.Empty : ReadNSString(api, name);
    }

    private static string ReadNSString(Api api, nint nsString)
    {
        nint utf8 = api.Send(nsString, Sel(api, "UTF8String"));
        return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }

    private static nint MakeNSString(Api api, string value)
    {
        nint cls = ClassNamed(api, "NSString");
        nint allocated = api.Send(cls, Sel(api, "alloc"));
        nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return api.SendNint(allocated, Sel(api, "initWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>按名字取 ObjC 类（<c>objc_getClass</c>，不是 KVC 的 NSClassFromString）。</summary>
    /// <remarks>
    /// <b>返回的就是类指针本身</b>，不要再套一层 <c>objc_getClass</c>——那等于把类指针
    /// 当成 C 字符串去解析，结果是 nil（或更糟：读到垃圾）。这个错误的表现是
    /// "所有消息都发给了 0"，症状是"窗口列表永远为空"，而它看起来像窗口没建出来。
    /// </remarks>
    private static nint ClassNamed(Api api, string value)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return api.GetClass(utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static nint Sel(Api api, string value)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return api.Sel(utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
