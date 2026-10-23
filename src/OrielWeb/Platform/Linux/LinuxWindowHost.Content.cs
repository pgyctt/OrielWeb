using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>webview 内容面：装配、导航、脚本、消息往返与页面事件。</summary>
/// <remarks>本文件是 <see cref="LinuxWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class LinuxWindowHost
{
    internal void PostToMainThread(Action action) => _backend.PostToMainThread(action);

    /// <summary>清除全部浏览数据（见 <see cref="IWindowBackend.ClearAllBrowsingData"/>）。</summary>
    /// <remarks>
    /// <para>
    /// 落点是 <c>webkit_website_data_manager_clear(ALL, 0, NULL callback)</c>：
    /// 管理器从 webview 上取（2.16+），<c>timeSpan = 0</c> 表示清全部时间的数据。
    /// </para>
    /// <para>
    /// 数据类型的位值**显式列出**而不是用 <c>WEBKIT_WEBSITE_DATA_ALL</c> 常量：
    /// 那个组合常量自 2.40 才有、位值随版本增加，显式列出我们想清的十二类
    /// （2.16–4.1 的全部成员）在所有支持版本上语义一致。
    /// 清除是异步 GTask，callback 传 NULL（GLib 允许）——发起后立刻返回，
    /// 完成通知拿不到，见接口上的说明。
    /// </para>
    /// </remarks>
    public void ClearAllBrowsingData()
    {
        if (!WindowAlive || _webview == 0)
        {
            return;
        }

        nint manager = GtkNative.WebkitWebViewGetWebsiteDataManager(_webview);
        if (manager == 0)
        {
            return;
        }

        GtkNative.WebkitWebsiteDataManagerClear(manager, WebkitWebsiteDataAll, 0, 0, 0, 0);
    }

    // ---- 导航操作（WebKitGTK 内建历史；无历史时调用是空操作）----
    // _webview 在 destroy 之后被置 0，判空是必需的：这些调用不能对已销毁的 webview 下手。

    public bool CanGoBack => _webview != 0 && GtkNative.WebkitWebViewCanGoBack(_webview) != 0;
    public bool CanGoForward => _webview != 0 && GtkNative.WebkitWebViewCanGoForward(_webview) != 0;

    public void GoBack()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewGoBack(_webview);
        }
    }

    public void GoForward()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewGoForward(_webview);
        }
    }

    public void Reload()
    {
        if (_webview != 0)
        {
            GtkNative.WebkitWebViewReload(_webview);
        }
    }

    public bool PostToUiThread(Action action) => _backend.PostToMainThread(action);

    public void EmitEvent(string name, string jsonPayload) => PushEventOnUi(name, jsonPayload);

    /// <summary>
    /// 应用"内核就绪前的底色"（见 <see cref="OrielWindowOptions.BackgroundColor"/>）。
    /// </summary>
    /// <remarks>
    /// WebKitGTK 的 <c>webkit_web_view_set_background_color</c>：页面 CSS 生效之前那一帧只有它能管。
    /// 该 API 在 2.40 起被标记 deprecated（推荐 CSS），但没有移除——已在 WSL 上确认符号存在，
    /// 行为待真机确认（见 ROADMAP 的真机清单）。
    /// </remarks>
    private void ApplyBackgroundColor()
    {
        // 运行期设过就以它为准（后设的赢）；没设过才回落到创建期选项。
        uint? effective = _runtimeBackgroundArgb;
        if (effective is null && _options.TryGetBackgroundArgb(out byte ca, out byte cr, out byte cg, out byte cb))
        {
            effective = ((uint)ca << 24) | ((uint)cr << 16) | ((uint)cg << 8) | cb;
        }

        if (effective is not { } argb || _webview == 0)
        {
            return;
        }

        OrielWindowOptions.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);

        var rgba = new GdkRGBA
        {
            Red = r / 255.0,
            Green = g / 255.0,
            Blue = b / 255.0,
            Alpha = a / 255.0,
        };

        GtkNative.WebkitWebViewSetBackgroundColor(_webview, in rgba);
    }

    /// <summary>
    /// 注入给拖动实现（桥接脚本）的宿主事实快照（见 <see cref="OrielSystemSnapshot"/>）：系统双击间隔。
    /// </summary>
    /// <remarks>
    /// 取的是 <c>gtk-double-click-time</c>；取不到时返回 0，由
    /// <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底。快照里**没有**缩放——GDK 的坐标已经是
    /// 应用像素（见 API.md §2 的 HiDPI 说明），拖动不需要再折一次。
    /// </remarks>
    private static OrielSystemSnapshot ReadSystemSnapshot()
        => OrielSystemSnapshot.Normalize(LinuxPlatformBackend.ReadDoubleClickTimeMs());

    private void Navigate()
    {
        if (_options.Url is { Length: > 0 } externalUrl)
        {
            // 内嵌资源的 URL 交给引擎里的 oriel:// 处理器（见 LinuxAssetScheme），宿主不做"改写成
            // 本地文件"那一步：那种改写要求资源落盘，而且会把页面来源变成 file://（不透明来源）。
            // 兼容别名 https://<host>/… 也在这里被归一成 oriel://，于是页面来源三平台一致。
            if (AssetUrl.TryResolve(externalUrl, _app.AssetHost, out string relative))
            {
                GtkNative.WebkitWebViewLoadUri(_webview, AssetUrl.ForHost(_app.AssetHost, relative));
                return;
            }

            GtkNative.WebkitWebViewLoadUri(_webview, externalUrl);
            return;
        }

        if (_assets is not null)
        {
            GtkNative.WebkitWebViewLoadUri(_webview, AssetUrl.DefaultDocument(_app.AssetHost));
        }
    }

    internal void OnLoadStarted()
    {
        _failedSinceLoadStart = false;

        // 导航开始即原文档开始卸载：它注入的回环脚本不会再回执，先清掉挂起的 eval。
        _messageHandler.FailPendingEvals("ExecuteScript 中止：页面正在离开原文档，回环结果不会再返回。");

        RaiseNavigationStarting(CurrentUri());
    }

    /// <summary>
    /// 整页加载失败（WebKitGTK 的 load-failed）。失败之后 WebKit 仍会发一次 load-changed 的
    /// FINISHED（加载的是错误页），所以这里只上报"失败"这一次，不把它也算成一次成功导航。
    /// </summary>
    internal void OnLoadFailed(string failingUri, string? error)
    {
        _failedSinceLoadStart = true;
        RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(false, failingUri, error));
    }

    internal void OnLoadFinished()
    {
        // 新文档不知道当前最大化状态，且下面的 Loaded 只触发首次——所以补推放在早退之前，
        // 每次加载完成都无条件补推一次当前值（与 Windows 的 OnNavigationCompleted 对齐）。
        _pageReady = true;
        SyncMaximizedState(force: true);

        // 失败之后这一趟 FINISHED 加载的是错误页：失败已经上报过，不再重复报成功（否则调用方
        // 会先收到"失败"、再收到一条矛盾的"成功"）。
        if (!_failedSinceLoadStart)
        {
            RaiseNavigationCompleted(new OrielNavigationCompletedEventArgs(true, CurrentUri(), null));
        }

        if (_loadedRaised)
        {
            return;
        }
        _loadedRaised = true;
        Loaded?.Invoke();

        var titlePtr = GtkNative.WebkitWebViewGetTitle(_webview);
        if (titlePtr != 0)
        {
            var title = Marshal.PtrToStringUTF8(titlePtr);
            if (!string.IsNullOrEmpty(title))
            {
                RaiseTitleChanged(title);
            }
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
        GtkNative.GtkWindowSetTitle(_gtkWindow, title);
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
            GtkNative.WebkitWebViewEvaluateJavaScript(_webview, js, -1, 0, 0, 0, 0, 0);
        }
        catch
        {
            // 窗口销毁后的迟到事件，忽略
        }
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _evalSeq);
        _messageHandler.RegisterEval(id, completion);

        // 页内求值并把结果经消息处理器回传（避免 GAsyncReadyCallback）
        var scriptJson = JsonText.EncodeString(script);
        var js = "window.oriel._evalScriptDone(" + id + ", JSON.stringify((function(){try{return eval(" + scriptJson +
                 ")}catch(e){return 'E:'+String(e)}})()))";
        PostToMainThread(() =>
        {
            // 投递与执行之间窗口可能已被销毁：带着 0 句柄去调 GTK 只会得到 critical 警告
            // （GTK 的函数都要求非空对象）。与 PushEventOnUi / PostWebMessageOnUi 同款判空——
            // 挂起的 eval 已由销毁/导航路径清掉，调用方的 Task 不会因此永久挂起。
            if (_webview == 0)
            {
                return;
            }

            GtkNative.WebkitWebViewEvaluateJavaScript(
                _webview, js, -1, 0, 0, 0, 0, 0);
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
            GtkNative.WebkitWebViewEvaluateJavaScript(
                _webview, js, -1, 0, 0, 0, 0, 0);
        }
        catch
        {
            // 窗口销毁后的迟到回执，忽略
        }
    }
}
