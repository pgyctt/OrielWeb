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
                _navigationStartingEvent?.Invoke(this, parsed.Object);
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

    private void SubscribeNavigationCompleted()
    {
        _navigationCompleted = new NavigationCompletedHandler((_, args) =>
        {
            using var parsed = ComObject.FromPointer<ICoreWebView2NavigationCompletedEventArgs>(args);
            if (parsed is not null)
            {
                _navigationCompletedEvent?.Invoke(this, parsed.Object);
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
            _documentTitleChangedEvent?.Invoke(this, EventArgs.Empty);
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
                _webMessageReceivedEvent?.Invoke(this, parsed.Object);
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
                _contextMenuRequestedEvent?.Invoke(this, parsed.Object);
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
            _cursorChangedEvent?.Invoke(this, null);
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
