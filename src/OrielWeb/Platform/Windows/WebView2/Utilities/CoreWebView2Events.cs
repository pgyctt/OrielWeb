using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Handlers;
using OrielWeb.Platform.Windows.WebView2.Interop;
using ComObject = OrielWeb.Platform.Windows.WebView2.Com.ComObject;

namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>WebView2 的资源上下文枚举，与原生值一一对应。</summary>
internal enum COREWEBVIEW2_WEB_RESOURCE_CONTEXT
{
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL = 0,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT = 1,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_STYLESHEET = 2,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_IMAGE = 3,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MEDIA = 4,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FONT = 5,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_SCRIPT = 6,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_XML_HTTP_REQUEST = 7,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FETCH = 8,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_TEXT_TRACK = 9,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_EVENT_SOURCE = 10,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_WEBSOCKET = 11,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MANIFEST = 12,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_SIGNED_EXCHANGE = 13,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_PING = 14,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_CSP_VIOLATION_REPORT = 15,
    COREWEBVIEW2_WEB_RESOURCE_CONTEXT_OTHER = 16,
}

/// <summary>
/// 资源拦截回调的 CCW 实现。调用方必须持有它——被 GC 回收后拦截会静默失效。
/// </summary>
[GeneratedComClass]
internal sealed partial class CoreWebView2WebResourceRequestedEventHandler(
    Action<object?, ICoreWebView2WebResourceRequestedEventArgs> callback)
    : ICoreWebView2WebResourceRequestedEventHandler
{
    public int Invoke(nint sender, nint args)
    {
        try
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2WebResourceRequestedEventArgs>(args);
            if (parsed is not null)
            {
                callback(null, parsed.Object);
            }
        }
        catch (Exception)
        {
            // 回调里抛异常会穿过 COM 边界：WebView2 在等一个应答，而调用方已经不存在了。
        }

        return HResult.S_OK;
    }
}

/// <summary>
/// <see cref="ICoreWebView2"/> 上的事件集合：首次订阅时挂到原生，<see cref="Dispose"/> 时全部注销。
/// </summary>
/// <remarks>
/// 用懒订阅而不是"构造即订阅"：上层是 <c>new CoreWebView2Events(view)</c> 之后逐个 <c>+=</c>，
/// 构造函数执行时这些委托还都是 null。若在构造里统一订阅，会漏掉全部事件且悄无声息。
/// </remarks>
internal sealed unsafe class CoreWebView2Events : IDisposable
{
    private readonly ICoreWebView2 _webView;
    private readonly ICoreWebView2_11? _webView11;
    private readonly List<Action> _unsubscribe = [];

    private EventHandler<ICoreWebView2NavigationStartingEventArgs>? _navigationStartingEvent;
    private EventHandler<ICoreWebView2NavigationCompletedEventArgs>? _navigationCompletedEvent;
    private EventHandler? _documentTitleChangedEvent;
    private EventHandler<ICoreWebView2WebMessageReceivedEventArgs>? _webMessageReceivedEvent;
    private EventHandler<ICoreWebView2ContextMenuRequestedEventArgs>? _contextMenuRequestedEvent;

    private NavigationStartingHandler? _navigationStarting;

    /// <summary>新窗口请求的处理器：固定策略（弃绝），由 <see cref="RejectNewWindows"/> 订阅。</summary>
    private NewWindowRequestedHandler? _newWindowRequested;
    private NavigationCompletedHandler? _navigationCompleted;
    private DocumentTitleChangedHandler? _documentTitleChanged;
    private WebMessageReceivedHandler? _webMessageReceived;
    private ContextMenuRequestedHandler? _contextMenuRequested;

    private bool _disposed;

    public CoreWebView2Events(IComObject<ICoreWebView2> webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        _webView = webView.Object;
        _webView11 = WebView2Utilities.GetInterface<ICoreWebView2_11>(_webView);
    }

    public event EventHandler<ICoreWebView2NavigationStartingEventArgs>? NavigationStarting
    {
        add
        {
            _navigationStartingEvent += value;
            if (_navigationStarting is null)
            {
                SubscribeNavigationStarting();
            }
        }
        remove => _navigationStartingEvent -= value;
    }

    public event EventHandler<ICoreWebView2NavigationCompletedEventArgs>? NavigationCompleted
    {
        add
        {
            _navigationCompletedEvent += value;
            if (_navigationCompleted is null)
            {
                SubscribeNavigationCompleted();
            }
        }
        remove => _navigationCompletedEvent -= value;
    }

    public event EventHandler? DocumentTitleChanged
    {
        add
        {
            _documentTitleChangedEvent += value;
            if (_documentTitleChanged is null)
            {
                SubscribeDocumentTitleChanged();
            }
        }
        remove => _documentTitleChangedEvent -= value;
    }

    public event EventHandler<ICoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived
    {
        add
        {
            _webMessageReceivedEvent += value;
            if (_webMessageReceived is null)
            {
                SubscribeWebMessageReceived();
            }
        }
        remove => _webMessageReceivedEvent -= value;
    }

    public event EventHandler<ICoreWebView2ContextMenuRequestedEventArgs>? ContextMenuRequested
    {
        add
        {
            _contextMenuRequestedEvent += value;
            if (_contextMenuRequested is null)
            {
                SubscribeContextMenuRequested();
            }
        }
        remove => _contextMenuRequestedEvent -= value;
    }

    private void SubscribeNavigationStarting()
    {
        _navigationStarting = new NavigationStartingHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2NavigationStartingEventArgs>(args);
            if (parsed is not null)
            {
                ComCallbackGuard.Raise(() => _navigationStartingEvent?.Invoke(this, parsed.Object), "NavigationStarting");
            }

            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2NavigationStartingEventHandler>
            .ConvertToUnmanaged(_navigationStarting);
        try
        {
            _webView.add_NavigationStarting(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => _webView.remove_NavigationStarting(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2NavigationStartingEventHandler>.Free((void*)pointer);
        }
    }

    /// <summary>
    /// 弃绝页面发起的所有新窗口请求（<c>window.open</c> / <c>target="_blank"</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是固定策略，不是可订阅的事件</b>：库明确定义为"忽略"，三平台一致（见 API.md
    /// 的导航一节）。所以它在这里订阅一次、不由上层开关控制，也不暴露 <c>event</c>。
    /// </para>
    /// <para>
    /// <b>为什么必须显式处理，而不是"不理会"</b>：不订阅时 <c>NewWindowRequested</c> 无人接，
    /// 引擎按自己的默认处置——而三个引擎的默认各不相同。Development 时表现为
    /// "点击 target=_blank 链接没反应"，而代码看起来完全正确；那种静默的不一致最难查。
    /// 显式把 <c>Handled</c> 置真并<em>不</em>给 <c>NewWindow</c>，请求就被弃绝。
    /// </para>
    /// <para>
    /// <b>不会静默丢掉导航意图</b>：需要打开外链时应当走 <c>app.OpenExternal</c>
    /// （受 API.md §7 的 scheme 白名单约束）——新窗口请求不是那条路。
    /// </para>
    /// </remarks>
    internal void RejectNewWindows()
    {
        _newWindowRequested = new NewWindowRequestedHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2NewWindowRequestedEventArgs>(args);
            if (parsed is not null)
            {
                // 只置 Handled、不给 NewWindow：这就是"弃绝"。反过来若给了 NewWindow，
                // 请求会被导向那个 WebView（本库的窗口都是自己创建的，没有可复用的）。
                parsed.Object.put_Handled(1);

                // 记一条诊断，带上被丢弃的目标 URL。
                // **这条日志是"库弃绝"与"引擎弹窗拦截"的唯一区分依据**——两者对页面都表现为
                // window.open 返回 null，没有它就没法判断究竟是哪一层拦的（真机自检
                // --selftest newwindow 断言的正是这一点）。
                OrielDiagnostics.Report(
                    "[OrielWeb] 已忽略页面发起的新窗口请求（window.open / target=\"_blank\"）：" +
                    $"{ReadNewWindowUri(parsed.Object)}。" +
                    "需要打开外链请用 app.OpenExternal。");
            }

            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2NewWindowRequestedEventHandler>
            .ConvertToUnmanaged(_newWindowRequested);
        try
        {
            _webView.add_NewWindowRequested(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => _webView.remove_NewWindowRequested(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2NewWindowRequestedEventHandler>.Free((void*)pointer);
        }
    }

    /// <summary>从事件参数里取被弃绝的目标 URL（取不到时给占位符，不抛）。</summary>
    /// <remarks>
    /// <c>get_Uri</c> 返回的是 <c>LPWSTR*</c>，**由调用方负责 <c>CoTaskMemFree</c>**
    /// （见 WebView2.h 的 API 约定）。漏了这一步就是每次弃绝泄漏一个字符串——
    /// 低频事件，但没理由留着。
    /// </remarks>
    private static string ReadNewWindowUri(ICoreWebView2NewWindowRequestedEventArgs args)
    {
        nint uri = 0;
        try
        {
            if (args.get_Uri(out uri) < 0 || uri == 0)
            {
                return "<未知目标>";
            }

            return Marshal.PtrToStringUni(uri) ?? "<未知目标>";
        }
        catch (Exception ex)
        {
            return $"<取 URL 失败：{ex.GetType().Name}>";
        }
        finally
        {
            if (uri != 0)
            {
                Marshal.FreeCoTaskMem(uri);
            }
        }
    }

    private void SubscribeNavigationCompleted()
    {
        _navigationCompleted = new NavigationCompletedHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2NavigationCompletedEventArgs>(args);
            if (parsed is not null)
            {
                ComCallbackGuard.Raise(() => _navigationCompletedEvent?.Invoke(this, parsed.Object), "NavigationCompleted");
            }

            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>
            .ConvertToUnmanaged(_navigationCompleted);
        try
        {
            _webView.add_NavigationCompleted(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => _webView.remove_NavigationCompleted(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>.Free((void*)pointer);
        }
    }

    private void SubscribeDocumentTitleChanged()
    {
        _documentTitleChanged = new DocumentTitleChangedHandler((_, _) =>
        {
            ComCallbackGuard.Raise(() => _documentTitleChangedEvent?.Invoke(this, EventArgs.Empty), "DocumentTitleChanged");
            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2DocumentTitleChangedEventHandler>
            .ConvertToUnmanaged(_documentTitleChanged);
        try
        {
            _webView.add_DocumentTitleChanged(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => _webView.remove_DocumentTitleChanged(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2DocumentTitleChangedEventHandler>.Free((void*)pointer);
        }
    }

    private void SubscribeWebMessageReceived()
    {
        _webMessageReceived = new WebMessageReceivedHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2WebMessageReceivedEventArgs>(args);
            if (parsed is not null)
            {
                ComCallbackGuard.Raise(() => _webMessageReceivedEvent?.Invoke(this, parsed.Object), "WebMessageReceived");
            }

            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2WebMessageReceivedEventHandler>
            .ConvertToUnmanaged(_webMessageReceived);
        try
        {
            _webView.add_WebMessageReceived(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => _webView.remove_WebMessageReceived(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2WebMessageReceivedEventHandler>.Free((void*)pointer);
        }
    }

    private void SubscribeContextMenuRequested()
    {
        if (_webView11 is null)
        {
            return;
        }

        _contextMenuRequested = new ContextMenuRequestedHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2ContextMenuRequestedEventArgs>(args);
            if (parsed is not null)
            {
                ComCallbackGuard.Raise(() => _contextMenuRequestedEvent?.Invoke(this, parsed.Object), "ContextMenuRequested");
            }

            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2ContextMenuRequestedEventHandler>
            .ConvertToUnmanaged(_contextMenuRequested);
        ICoreWebView2_11 view11 = _webView11;
        try
        {
            view11.add_ContextMenuRequested(pointer, out EventRegistrationToken token).ThrowOnError();
            _unsubscribe.Add(() => view11.remove_ContextMenuRequested(token));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2ContextMenuRequestedEventHandler>.Free((void*)pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Action unsubscribe in _unsubscribe)
        {
            try
            {
                unsubscribe();
            }
            catch (Exception)
            {
                // 注销失败只影响句柄回收，不该打断销毁流程。
            }
        }

        _unsubscribe.Clear();
        _navigationStartingEvent = null;
        _navigationCompletedEvent = null;
        _documentTitleChangedEvent = null;
        _webMessageReceivedEvent = null;
        _contextMenuRequestedEvent = null;
    }
}

/// <summary>组合控制器的事件集合：目前只有光标变化。</summary>
internal sealed unsafe class CoreWebView2CompositionControllerEvents : IDisposable
{
    private readonly ICoreWebView2CompositionController _controller;
    private readonly List<EventRegistrationToken> _tokens = [];
    private EventHandler<object?>? _cursorChangedEvent;
    private CursorChangedHandler? _cursorChanged;
    private bool _disposed;

    public CoreWebView2CompositionControllerEvents(IComObject<ICoreWebView2CompositionController> controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _controller = controller.Object;
    }

    public event EventHandler<object?>? CursorChanged
    {
        add
        {
            _cursorChangedEvent += value;
            if (_cursorChanged is null)
            {
                Subscribe();
            }
        }
        remove => _cursorChangedEvent -= value;
    }

    private void Subscribe()
    {
        _cursorChanged = new CursorChangedHandler((_, _) =>
        {
            ComCallbackGuard.Raise(() => _cursorChangedEvent?.Invoke(this, null), "CursorChanged");
            return HResult.S_OK;
        });

        nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2CursorChangedEventHandler>
            .ConvertToUnmanaged(_cursorChanged);
        try
        {
            _controller.add_CursorChanged(pointer, out EventRegistrationToken token).ThrowOnError();
            _tokens.Add(token);
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CursorChangedEventHandler>.Free((void*)pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (EventRegistrationToken token in _tokens)
        {
            try
            {
                _controller.remove_CursorChanged(token);
            }
            catch (Exception)
            {
            }
        }

        _tokens.Clear();
        _cursorChangedEvent = null;
    }
}
