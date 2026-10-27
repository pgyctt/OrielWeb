using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Interop;
using ComObject = OrielWeb.Platform.Windows.WebView2.Com.ComObject;

namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>
/// 裸接口上的便利扩展。
/// </summary>
/// <remarks>
/// 与 <see cref="WebView2Extensions"/> 的 <c>IComObject</c> 版本一一对应：宿主代码里两种持有方式都有
/// （字段常存 <c>IComObject</c>，局部变量常是裸接口），两套并存才能保持调用点不变。
/// </remarks>
internal static unsafe class NativeExtensions
{
    extension(ICoreWebView2Controller controller)
    {
        public bool IsVisible
        {
            get
            {
                controller.get_IsVisible(out int value).ThrowOnError();
                return value != 0;
            }
            set => controller.put_IsVisible(value ? 1 : 0).ThrowOnError();
        }

        public WV2RECT Bounds
        {
            get
            {
                controller.get_Bounds(out WV2RECT value).ThrowOnError();
                return value;
            }
            set => controller.put_Bounds(value).ThrowOnError();
        }

        public double ZoomFactor
        {
            get
            {
                controller.get_ZoomFactor(out double value).ThrowOnError();
                return value;
            }
            set => controller.put_ZoomFactor(value).ThrowOnError();
        }

        public IComObject<ICoreWebView2>? CoreWebView2
        {
            get
            {
                controller.get_CoreWebView2(out nint value).ThrowOnError();
                return ComObject.FromPointer<ICoreWebView2>(value);
            }
        }

        public void MoveFocus(int reason) => controller.MoveFocus(reason).ThrowOnError();

        public void Close() => controller.Close().ThrowOnError();
    }

    extension(ICoreWebView2Controller4 controller)
    {
        public bool AllowExternalDrop
        {
            get
            {
                controller.get_AllowExternalDrop(out int value).ThrowOnError();
                return value != 0;
            }
            set => controller.put_AllowExternalDrop(value ? 1 : 0).ThrowOnError();
        }
    }

    extension(ICoreWebView2Controller2 controller)
    {
        public COREWEBVIEW2_COLOR DefaultBackgroundColor
        {
            get
            {
                controller.get_DefaultBackgroundColor(out COREWEBVIEW2_COLOR value).ThrowOnError();
                return value;
            }
            set => controller.put_DefaultBackgroundColor(value).ThrowOnError();
        }
    }

    extension(ICoreWebView2Environment environment)
    {
        public IComObject<ICoreWebView2WebResourceResponse>? CreateWebResourceResponse(
            Stream content, int statusCode, string reasonPhrase, string headers)
        {
            var managed = new Handlers.ManagedIStream(content);
            nint streamPointer = (nint)System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IStream>
                .ConvertToUnmanaged(managed);
            try
            {
                environment
                    .CreateWebResourceResponse(streamPointer, statusCode, reasonPhrase, headers, out nint response)
                    .ThrowOnError();

                return ComObject.FromPointer<ICoreWebView2WebResourceResponse>(response);
            }
            finally
            {
                System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IStream>.Free((void*)streamPointer);
            }
        }
    }

    extension(ICoreWebView2Settings5 settings)
    {
        public bool AreDevToolsEnabled
        {
            get
            {
                settings.get_AreDevToolsEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.put_AreDevToolsEnabled(value ? 1 : 0).ThrowOnError();
        }

        public bool IsZoomControlEnabled
        {
            get
            {
                settings.get_IsZoomControlEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.put_IsZoomControlEnabled(value ? 1 : 0).ThrowOnError();
        }

        public bool IsPinchZoomEnabled
        {
            get
            {
                settings.get_IsPinchZoomEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.put_IsPinchZoomEnabled(value ? 1 : 0).ThrowOnError();
        }
    }

    extension(ICoreWebView2CompositionController controller)
    {
        /// <summary>挂到 DirectComposition 树上的根视觉（原生是 IUnknown*）。</summary>
        public object? RootVisualTarget
        {
            set => controller.put_RootVisualTarget(value is null ? 0 : GetUnknown(value)).ThrowOnError();
        }

        public nint Cursor
        {
            get
            {
                controller.get_Cursor(out nint value).ThrowOnError();
                return value;
            }
        }

        // 这里刻意不放 SendMouseInput 的枚举版包装。
        // 接口自带 SendMouseInput(int, int, uint, WV2POINT)：再定义一个同名同义的扩展（参数是枚举、
        // 返回 void）会让"传枚举"的调用点选中扩展，HRESULT 被扩展内部 ThrowOnError 吞掉，
        // 排查注入失败时完全看不到错误。调用方需要就自己显式 (int) 转换。
    }

    extension(ICoreWebView2 webView)
    {
        public void AddWebResourceRequestedFilter(string uri, COREWEBVIEW2_WEB_RESOURCE_CONTEXT context)
            => webView.AddWebResourceRequestedFilter(uri, (int)context).ThrowOnError();

        /// <summary>订阅资源拦截。handler 的生命周期由调用方负责。</summary>
        public void SubscribeWebResourceRequested(CoreWebView2WebResourceRequestedEventHandler handler, out EventRegistrationToken token)
        {
            nint pointer = (nint)System.Runtime.InteropServices.Marshalling
                .ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.ConvertToUnmanaged(handler);
            try
            {
                webView.add_WebResourceRequested(pointer, out token).ThrowOnError();
            }
            finally
            {
                System.Runtime.InteropServices.Marshalling
                    .ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.Free((void*)pointer);
            }
        }
    }

    extension(ICoreWebView2WebResourceRequestedEventArgs args)
    {
        public ICoreWebView2WebResourceRequest? Request
        {
            get
            {
                args.get_Request(out nint value).ThrowOnError();
                return ComObject.FromPointer<ICoreWebView2WebResourceRequest>(value)?.Object;
            }
        }

        public ICoreWebView2WebResourceResponse? Response
        {
            set => args.put_Response(value is null ? 0 : GetUnknown(value)).ThrowOnError();
        }
    }

    extension(ICoreWebView2WebResourceRequest request)
    {
        public string? Uri
        {
            get
            {
                request.get_Uri(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }
    }

    extension(ICoreWebView2WebMessageReceivedEventArgs args)
    {
        public string? Source
        {
            get
            {
                args.get_Source(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }

        public string? WebMessageAsJson
        {
            get
            {
                args.get_WebMessageAsJson(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }
    }

    extension(ICoreWebView2NavigationStartingEventArgs args)
    {
        public string? Uri
        {
            get
            {
                args.get_Uri(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }
    }

    extension(ICoreWebView2NavigationCompletedEventArgs args)
    {
        public bool IsSuccess
        {
            get
            {
                args.get_IsSuccess(out int value).ThrowOnError();
                return value != 0;
            }
        }

        public int WebErrorStatus
        {
            get
            {
                args.get_WebErrorStatus(out int value).ThrowOnError();
                return value;
            }
        }
    }

    extension(ICoreWebView2ContextMenuRequestedEventArgs args)
    {
        public ICoreWebView2ContextMenuItemCollection? MenuItems
        {
            get
            {
                args.get_MenuItems(out nint value).ThrowOnError();
                return ComObject.FromPointer<ICoreWebView2ContextMenuItemCollection>(value)?.Object;
            }
        }

        public bool Handled
        {
            get
            {
                args.get_Handled(out int value).ThrowOnError();
                return value != 0;
            }
            set => args.put_Handled(value ? 1 : 0).ThrowOnError();
        }

        public int SelectedCommandId
        {
            get
            {
                args.get_SelectedCommandId(out int value).ThrowOnError();
                return value;
            }
            set => args.put_SelectedCommandId(value).ThrowOnError();
        }
    }

    extension(ICoreWebView2ContextMenuItem item)
    {
        public string? Name
        {
            get
            {
                item.get_Name(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }

        public int CommandId
        {
            get
            {
                item.get_CommandId(out int value).ThrowOnError();
                return value;
            }
        }
    }

    extension(ICoreWebView2ContextMenuItemCollection collection)
    {
        public uint Count
        {
            get
            {
                collection.get_Count(out uint value).ThrowOnError();
                return value;
            }
        }

        public ICoreWebView2ContextMenuItem? GetValueAtIndex(uint index)
        {
            collection.GetValueAtIndex(index, out nint value).ThrowOnError();
            return ComObject.FromPointer<ICoreWebView2ContextMenuItem>(value)?.Object;
        }

        public void RemoveValueAtIndex(uint index) => collection.RemoveValueAtIndex(index).ThrowOnError();
    }

    private static nint GetUnknown(object instance)
    {
        if (ComWrappers.TryGetComInstance(instance, out nint unknown) && unknown != 0)
        {
            return unknown;
        }

        return Marshal.GetIUnknownForObject(instance);
    }
}
