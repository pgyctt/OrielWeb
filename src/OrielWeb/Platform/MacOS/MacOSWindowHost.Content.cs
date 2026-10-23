using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>webview 内容面：装配、导航、脚本、消息往返与页面事件。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

    /// <summary>清除全部浏览数据（见 <see cref="IWindowBackend.ClearAllBrowsingData"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 落点是 <c>[WKWebsiteDataStore.defaultDataStore removeDataOfTypes:
    /// allWebsiteDataTypes modifiedSince:[NSDate distantPast] completionHandler:&lt;空 block&gt;]</c>：
    /// 默认（持久）数据存储里的全部类型、全部时间的数据。
    /// </para>
    /// <para>
    /// <b>completionHandler 不能传 nil</b>（B16 那版就是传 0，只是自检没跑到这条 API
    /// 的完成路径，所以一直没暴露）：WebKit 会无条件调用那个 block，传 nil 就是在
    /// <c>block-&gt;invoke</c>（偏移 0x10）取指令——真机上是
    /// <c>KERN_INVALID_ADDRESS at 0x10</c>。现在统一传
    /// <see cref="ObjCBlock.NoOp"/>（全局 block，不需要持有或释放）。
    /// 注意这与 <c>evaluateJavaScript:completionHandler:</c> 不同：那条的 nil 是**真的被支持**
    /// （Apple 明确文档化了"handler 可为 nil"），这两条不是。
    /// </para>
    /// <para>清除是**异步**的：本方法返回只代表"已经交给 WebKit"，不代表已经删干净。</para>
    /// </remarks>
    public void ClearAllBrowsingData()
    {
        if (_webview == 0)
        {
            return;
        }

        nint storeType = ObjCRuntime.GetClassOrThrow("WKWebsiteDataStore");
        nint store = ObjCRuntime.SendId(storeType, ObjCRuntime.Sel("defaultDataStore"));
        nint types = ObjCRuntime.SendId(storeType, ObjCRuntime.Sel("allWebsiteDataTypes"));
        nint distantPast = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSDate"), ObjCRuntime.Sel("distantPast"));
        if (store == 0 || types == 0 || distantPast == 0)
        {
            // 类或对象没拿到（理论上不会——都是 Foundation/WebKit 的稳定 API）。
            // 静默返回会不可观测，走诊断通道。
            OrielDiagnostics.Report("[OrielWeb] clearAllBrowsingData：WKWebsiteDataStore 对象获取失败，未清除。");
            return;
        }

        ObjCRuntime.SendVoidObjObjObj(
            store,
            ObjCRuntime.Sel("removeDataOfTypes:modifiedSince:completionHandler:"),
            types, distantPast, ObjCBlock.NoOp);
    }

    /// <summary>
    /// 开关 webview 的背景绘制（<c>drawsBackground</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是私有属性，走 KVC。</b>WKWebView 没有公开的"透明背景"入口，而材质要透出来
    /// 就必须让它别画那层不透明的底——macOS 上除了这条路没有别的办法（Tauri/tao 同样如此）。
    /// </para>
    /// <para>
    /// <b>调之前必须确认 KVC 不会抛。</b>KVC 对"既没有 setter、也没有同名 ivar"的键会抛
    /// <c>NSUndefinedKeyException</c>，而那是 ObjC 异常、托管侧接不住——一次 setEffects
    /// 就能让整个应用消失。所以先用运行时 API 问两个问题：类上有没有
    /// <c>setDrawsBackground:</c>、有没有 <c>_drawsBackground</c> ivar。任一存在，KVC 就
    /// 走得到；都不存在（将来的 WebKit 改了实现）就跳过并记一条诊断——
    /// 那种情况下材质看不见，但应用不会挂。
    /// </para>
    /// </remarks>
    private void SetWebviewDrawsBackground(bool draws)
    {
        if (_webview == 0)
        {
            return;
        }

        nint cls = ObjCRuntime.object_getClass(_webview);

        // 首选**公开** API：WKWebView.backgroundColor 自 macOS 10.14 起可用。
        // 它同时决定"页面没画东西的地方显示什么"，这正是材质能不能透出来的关键。
        if (ObjCRuntime.class_getInstanceMethod(cls, ObjCRuntime.Sel("setBackgroundColor:")) != 0)
        {
            nint colorClass = ObjCRuntime.GetClassOrThrow("NSColor");
            nint color = ObjCRuntime.SendId(
                colorClass,
                ObjCRuntime.Sel(draws ? "windowBackgroundColor" : "clearColor"));
            if (color != 0)
            {
                ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setBackgroundColor:"), color);
            }

            // opaque 必须一起改：只把颜色设成透明而 NSView 仍标记为不透明时，
            // AppKit 会跳过底下的材质合成——颜色对了，材质还是看不到。
            ObjCRuntime.SendVoidBool(_webview, ObjCRuntime.Sel("setOpaque:"), draws);

            // 设完读回：这条链路设不上时症状是"材质永远看不见"，而日志一片安静。
            // 读回的是 webview 自己的 backgroundColor（不是刚传进去的那个对象），
            // 所以它同时验证了"消息发对了 receiver"。
            double alphaAfter = ReadBackgroundAlpha(_webview);
            if (draws ? alphaAfter < 0.999 : alphaAfter > 0.001)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] setEffects：WKWebView 的 backgroundColor 设置未生效"
                    + $"（期望 alpha {(draws ? "1" : "0")}，读回 {alphaAfter:0.###}）——"
                    + "材质可能被 webview 的不透明底色挡住。");
            }

            return;
        }

        // 老系统回退：私有的 drawsBackground（KVC）。macOS 15 上这个 key 已被移除，
        // 所以它只对 10.14 之前有意义。
        if (!SupportsDrawsBackground(_webview))
        {
            OrielDiagnostics.Report(
                "[OrielWeb] setEffects：这个系统上的 WKWebView 既没有 backgroundColor 也没有"
                + " drawsBackground，webview 仍会画不透明底色——材质可能看不见（页面背景透不出来）。");
            return;
        }

        nint key = ObjCRuntime.MakeNSString("drawsBackground");
        nint boxed = ObjCRuntime.SendIdNint(
            ObjCRuntime.GetClassOrThrow("NSNumber"), ObjCRuntime.Sel("numberWithBool:"), draws ? 1 : 0);
        ObjCRuntime.SendVoidObjObj(_webview, ObjCRuntime.Sel("setValue:forKey:"), boxed, key);
    }

    /// <summary>读 webview 当前背景色的 alpha（读不到按 1 处理，即"不透明"）。</summary>
    private static double ReadBackgroundAlpha(nint webview)
    {
        nint color = ObjCRuntime.SendId(webview, ObjCRuntime.Sel("backgroundColor"));
        if (color == 0)
        {
            return 1.0;
        }

        return ObjCRuntime.SendDouble(color, ObjCRuntime.Sel("alphaComponent"));
    }

    /// <summary>
    /// webview 支不支持对 <c>drawsBackground</c> 走 KVC。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条件任一成立即可：有 <c>setDrawsBackground:</c> 方法，或继承链上有能对上 KVC 名字的
    /// ivar。WKWebView 的这个属性**是私有的**，实测（Catalina 15.8.1）两个公开选择器都不响应，
    /// 但 KVC 仍能写进去——它走的是 ivar 路径。
    /// </para>
    /// <para>
    /// <b>为什么不能只看 <c>class_getInstanceVariable</c></b>：那个函数只查**该类自身**，
    /// 不查父类（runtime 文档明确）。B22 最初的实现就是这么判断的，于是在 Catalina 上
    /// 恒为"不支持"，材质永远透不出来——而日志只提示"这个系统没有 drawsBackground"，
    /// 指向的是系统而不是那个判断本身。
    /// </para>
    /// <para>
    /// <c>class_copyIvarList</c> 返回的数组由 runtime 分配，必须用 libc 的 <c>free</c> 释放。
    /// </para>
    /// </remarks>
    private static bool SupportsDrawsBackground(nint webview)
    {
        nint cls = ObjCRuntime.object_getClass(webview);

        if (ObjCRuntime.class_getInstanceMethod(cls, ObjCRuntime.Sel("setDrawsBackground:")) != 0)
        {
            return true;
        }

        // KVC 走 ivar 时依次找 _<key>、_is<Key>、<key>、is<Key>
        bool found = false;
        var scan = new System.Text.StringBuilder();
        for (nint current = cls; current != 0; current = ObjCRuntime.class_getSuperclass(current))
        {
            nint list = ObjCRuntime.class_copyIvarList(current, out uint count);
            if (list == 0)
            {
                continue;
            }

            try
            {
                var interesting = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    nint ivar = Marshal.ReadIntPtr(list, (int)i * IntPtr.Size);
                    nint name = ObjCRuntime.ivar_getName(ivar);
                    if (name == 0)
                    {
                        continue;
                    }

                    string? text = Marshal.PtrToStringUTF8(name);
                    if (text is null)
                    {
                        continue;
                    }

                    if (text is "drawsBackground" or "_drawsBackground"
                        or "_isDrawsBackground" or "isDrawsBackground")
                    {
                        found = true;
                    }

                    // 诊断只留可能相关的名字，否则 WKWebView 的 ivar 列表能刷满一屏
                    if (text.Contains("raw", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("ackground", StringComparison.OrdinalIgnoreCase))
                    {
                        interesting.Add(text);
                    }
                }

                scan.Append($"[{count} ivars: {string.Join("/", interesting)}] ");
            }
            finally
            {
                ObjCRuntime.free(list);
            }

            if (found)
            {
                break;
            }
        }

        if (!found)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] WKWebView 的 drawsBackground 探测失败，ivar 扫描结果（逐层父类）：" + scan);
        }

        return found;
    }

    // ---- 导航操作（WKWebView 内建历史；无历史时调用是空操作）----
    // _webview 在窗口销毁后被置 0，判空是必需的。

    public bool CanGoBack => _webview != 0 && ObjCRuntime.SendBoolRet(_webview, ObjCRuntime.Sel("canGoBack"));
    public bool CanGoForward => _webview != 0 && ObjCRuntime.SendBoolRet(_webview, ObjCRuntime.Sel("canGoForward"));

    public void GoBack()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("goBack"));
        }
    }

    public void GoForward()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("goForward"));
        }
    }

    public void Reload()
    {
        if (_webview != 0)
        {
            ObjCRuntime.SendVoid(_webview, ObjCRuntime.Sel("reload"));
        }
    }

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    /// <summary>
    /// 把当前生效的底色应用上去（见 <see cref="SetBackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// 设两处：<c>NSWindow.backgroundColor</c>（窗口底色）与（12+）
    /// <c>WKWebView.underPageBackgroundColor</c>——后者才是"页面没铺满时露出来的那块"。
    /// 老系统上取不到就只剩窗口底色。
    /// <para>
    /// <c>underPageBackgroundColor</c> 是 macOS 12 才有的选择器，对老系统发这条消息不是
    /// 抛异常而是 trap，所以先用 <c>respondsToSelector:</c> 问一句。
    /// </para>
    /// <para>
    /// 用 <c>colorWithSRGBRed:…</c> 而不是 <c>colorWithRed:…</c>：后者按**设备**色彩空间解释，
    /// 同一个数值在 P3 屏与 sRGB 屏上会显示成不同的颜色。
    /// NSColor 由类方法返回、autoreleased，不需要自己 release。
    /// </para>
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        // 运行期设过就以它为准（后设的赢）；没设过才回落到创建期选项。
        uint? effective = _runtimeBackgroundArgb;
        if (effective is null && _options.TryGetBackgroundArgb(out byte ca, out byte cr, out byte cg, out byte cb))
        {
            effective = ((uint)ca << 24) | ((uint)cr << 16) | ((uint)cg << 8) | cb;
        }

        if (effective is not { } argb || _nsWindow == 0)
        {
            return;
        }

        OrielWindowOptions.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);

        nint color = ObjCRuntime.SendIdDouble4(
            ObjCRuntime.GetClass("NSColor"),
            ObjCRuntime.Sel("colorWithSRGBRed:green:blue:alpha:"),
            r / 255.0,
            g / 255.0,
            b / 255.0,
            a / 255.0);

        if (color == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidObj(_nsWindow, ObjCRuntime.Sel("setBackgroundColor:"), color);

        if (_webview != 0
            && ObjCRuntime.SendBoolRetObj(
                _webview,
                ObjCRuntime.Sel("respondsToSelector:"),
                ObjCRuntime.Sel("setUnderPageBackgroundColor:")))
        {
            ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("setUnderPageBackgroundColor:"), color);
        }
    }

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            // 内嵌资源交给 WKWebView 的 oriel:// 处理器（见 MacOSAssetScheme）；兼容别名
            // https://<host>/… 在这里被归一成 oriel://，于是页面来源与 Windows/Linux 一致。
            // （旧实现把 https:// 映射成解压目录里的 file:// 本地文件：那要求资源必须落盘，
            // 而且 loadFileURL:allowingReadAccessToURL: 的读权限是"按目录"给的，多一层坑。）
            NavigateCore(AssetUrl.TryResolve(externalUrl, _app.AssetHost, out string relative)
                ? AssetUrl.ForHost(_app.AssetHost, relative)
                : externalUrl);
            return;
        }

        // 内嵌资产首页
        if (_assets is not null)
        {
            NavigateCore(AssetUrl.DefaultDocument(_app.AssetHost));
        }
    }

    private void NavigateCore(string url)
    {
        // URLWithString: 要 NSString*，不是 char*（把 char* 当 NSString* 传会在
        // CoreFoundation 的 __CF_IS_OBJC 里 trap，真机 CI 上实测过）。
        var nsUrl = ObjCRuntime.SendIdObj(
            ObjCRuntime.GetClass("NSURL"),
            ObjCRuntime.Sel("URLWithString:"),
            ObjCRuntime.MakeNSString(url));
        var request = ObjCRuntime.SendIdObj(ObjCRuntime.GetClass("NSURLRequest"), ObjCRuntime.Sel("requestWithURL:"), nsUrl);
        ObjCRuntime.SendVoidObj(_webview, ObjCRuntime.Sel("loadRequest:"), request);
    }

    internal void OnNavigationStarted()
    {
        // 导航开始即原文档开始卸载：它注入的回环脚本不会再回执，先清掉挂起的 eval。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：页面正在离开原文档，回环结果不会再返回。");

        RaiseNavigationStarting(CurrentUri());
    }

    /// <summary>
    /// 导航结束（成功或失败）。失败由 WKNavigationDelegate 的 didFail* 回调传来，
    /// 带 NSError 压成的一行文本。
    /// </summary>
    internal void OnNavigationCompleted(bool success, string? error = null)
    {
        if (success)
        {
            // 新文档不知道当前最大化状态，且下面的 Loaded 只触发首次——所以补推放在早退之前，
            // 每次导航完成都无条件补推一次当前值（与 Windows 的 OnNavigationCompleted 对齐）。
            _pageReady = true;
            SyncMaximizedState(force: true);
        }

        // 失败时 webView.URL 仍然指向旧文档（取不到失败地址），所以失败一律给空串 URL。
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(
            success, success ? CurrentUri() : string.Empty, error));
        if (!success)
        {
            return;
        }

        if (!_loadedRaised)
        {
            _loadedRaised = true;
            Loaded?.Invoke();

            // 文档标题 → 窗口标题
            var title = ObjCRuntime.ToManagedString(ObjCRuntime.SendId(_webview, ObjCRuntime.Sel("title")));
            if (!string.IsNullOrEmpty(title))
            {
                RaiseTitleChanged(title);
            }
        }
    }

    /// <summary>
    /// 向页面推送一个 oriel 事件（页面侧 window.oriel.on(name, handler) 接收）。
    /// 与 PostWebMessageOnUi 的回执通道分开——那条只认 {"id","ok"} 回执形态。
    /// 页面未就绪（桥接脚本尚未注入）时直接丢弃：加载完成会强制补推当前状态，不会丢状态。
    /// </summary>
    private void PushEventOnUi(string name, string jsonValue)
    {
        // **必须切回 UI 线程**：事件可能从任意线程发出（B9 的通道结束事件就来自
        // ConfigureAwait(false) 之后的线程池线程）。WebKit 的求值调用在 UI 线程之外
        // 行为未定义——不能因为"这次碰巧能跑"就省掉这一步。回执通道一直有这条守卫。
        if (!IsOnUiThread())
        {
            PostToUiThread(() => PushEventOnUi(name, jsonValue));
            return;
        }

        if (_webview == 0 || !_pageReady)
        {
            return;
        }
        try
        {
            var js = "window.oriel._onEvent(" + JsonText.EncodeString(name) + ", " + jsonValue + ")";
            ObjCRuntime.SendVoidObjNint(
                _webview,
                ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                ObjCRuntime.MakeNSString(js),
                0);
        }
        catch
        {
            // 窗口销毁后的迟到事件，忽略
        }
    }

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 取值是"取不到就返回 0"的形式（框架未加载时类查不到），由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底。注意 <c>+[NSEvent doubleClickInterval]</c>
    /// 给的是**秒**，注入给页面的是毫秒。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
    {
        nint nsEventClass = ObjCRuntime.GetClass("NSEvent");
        double intervalSeconds = nsEventClass == 0
            ? 0
            : ObjCRuntime.SendDouble(nsEventClass, ObjCRuntime.Sel("doubleClickInterval"));

        return OrielSystemSnapshot.Normalize((int)Math.Round(intervalSeconds * 1000));
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _evalSeq);
        _messageHandler.RegisterEval(id, completion);

        // 页内求值并把结果经消息处理器回传（避免 ObjC block 完成回调）
        var scriptJson = JsonText.EncodeString(script);
        var js = "window.oriel._evalScriptDone(" + id + ", JSON.stringify((function(){try{return eval(" + scriptJson +
                 ")}catch(e){return 'E:'+String(e)}})()))";
        PostToMainThread(() =>
        {
            try
            {
                ObjCRuntime.SendVoidObjNint(
                    _webview,
                    ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                    ObjCRuntime.MakeNSString(js),
                    0);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    public void PostMessageAsJson(string json) => PostWebMessageOnUi(json);

    internal void PostWebMessageOnUi(string json)
    {
        try
        {
            if (_webview == 0)
            {
                return;
            }
            // 把回执**原样**求值进页面：`_onResult` 现在收整个回执对象。
            //
            // 早先是宿主侧把回执拆成 `_onResult(id, ok, payload)`——成功取值、失败取文案。
            // 改成整对象传是为了让 code / data 自然流过：拆的话三平台各要抽一遍、
            // 各可能漏一个字段，而漏掉的表现是"某个平台拿不到错误码"。
            //
            // 直接把 JSON 嵌进 JS 表达式是安全的：JSON 字面量本身就是合法的 JS 表达式，
            // 而这份 JSON 由宿主自己的 Utf8JsonWriter 产出（不是拼接字符串）。
            var js = $"window.oriel._onResult({json})";
            ObjCRuntime.SendVoidObjNint(
                _webview,
                ObjCRuntime.Sel("evaluateJavaScript:completionHandler:"),
                ObjCRuntime.MakeNSString(js),
                0);
        }
        catch
        {
            // 窗口销毁后的迟到回执，忽略
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        // 显式设过标题就不再跟随页面标题（理由见 _titleSetExplicitly 的说明）
        if (_titleSetExplicitly)
        {
            return;
        }

        _title = title;
        TitleChanged?.Invoke(title);
    }

    internal void RaiseNavigationStarting(string url)
    {
        // 记下当前文档：入站 IPC 要做来源校验，而消息本身不带来源，
        // 只能由"这个窗口现在停在哪个 URL"来回答。
        _currentUrl = url;
        NavigationStarting?.Invoke(url);
        PushEventOnUi("navigation.starting", $"{{\"url\":{JsonText.EncodeString(url)}}}");
    }

    /// <summary>当前文档的 URL（导航开始时更新）。入站 IPC 的来源校验用它。</summary>
    internal string? CurrentUrl => _currentUrl;

    internal void RaiseNavigationCompleted(OrielNavigationCompletedEventArgs args)
    {
        NavigationCompleted?.Invoke(args);
        string error = args.Error is null ? "null" : JsonText.EncodeString(args.Error);
        PushEventOnUi(
            "navigation.completed",
            $"{{\"success\":{(args.Success ? "true" : "false")},\"url\":{JsonText.EncodeString(args.Url)},\"error\":{error}}}");
    }

    internal void RaiseConsoleMessage(string level, string text)
        => ConsoleMessage?.Invoke(new OrielConsoleMessageEventArgs(level, text));

    internal void RaiseMessageReceived(string name, string json)
        => MessageReceived?.Invoke(new OrielMessageReceivedEventArgs(name, json));
}
