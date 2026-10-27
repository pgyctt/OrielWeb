using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Handlers;
using OrielWeb.Platform.Windows.WebView2.Interop;
using ComObject = OrielWeb.Platform.Windows.WebView2.Com.ComObject;

namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>
/// 面向 .NET 的便利层：把原生形状（HRESULT + 裸指针）包成属性、<c>Task</c> 与 <c>string</c>。
/// </summary>
/// <remarks>
/// 方法与属性的**名字和形状**刻意与 WebView2Aot 的 <c>WebView2.Utilities</c> 保持一致，
/// 这样替换绑定时上层只需换 using，不必重写调用点。
/// </remarks>
internal static unsafe class WebView2Extensions
{
    extension(IComObject<ICoreWebView2Environment> environment)
    {
        /// <summary>创建组合控制器（WebView2 作为 DirectComposition 视觉）。</summary>
        public Task<IComObject<ICoreWebView2CompositionController>?> CreateCoreWebView2CompositionControllerAsync(nint parentWindow)
        {
            var environment3 = WebView2Utilities.GetInterface<ICoreWebView2Environment3>(environment.Object)
                ?? throw new InvalidOperationException(
                    "当前 WebView2 运行时不支持组合控制器：ICoreWebView2Environment3 不可用。"
                    + "请确认已安装 WebView2 Evergreen 运行时。");

            var completion = new TaskCompletionSource<IComObject<ICoreWebView2CompositionController>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            nint handlerPointer = 0;
            var handler = new CompositionControllerCompletedHandler((hr, createdController) =>
            {
                PendingHandlers.Remove(handlerPointer);

                if (hr < 0)
                {
                    completion.TrySetException(Marshal.GetExceptionForHR(hr)
                        ?? new COMException($"创建组合控制器失败（HRESULT 0x{hr:X8}）。", hr));
                }
                else
                {
                    completion.TrySetResult(ComObject.FromPointer<ICoreWebView2CompositionController>(createdController));
                }

                return 0;
            });

            try
            {
                handlerPointer = (nint)ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler>
                    .ConvertToUnmanaged(handler);
                PendingHandlers.Add(handlerPointer, handler);

                int hr = environment3.CreateCoreWebView2CompositionController(parentWindow, handlerPointer);
                if (hr < 0)
                {
                    PendingHandlers.Remove(handlerPointer);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch (Exception ex)
            {
                PendingHandlers.Remove(handlerPointer);
                completion.TrySetException(ex);
            }
            finally
            {
                if (handlerPointer != 0)
                {
                    ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler>
                        .Free((void*)handlerPointer);
                }
            }

            return completion.Task;
        }

        /// <summary>用托管流构造一个资源响应。</summary>
        public IComObject<ICoreWebView2WebResourceResponse>? CreateWebResourceResponse(
            Stream content, int statusCode, string reasonPhrase, string headers)
        {
            var managed = new ManagedIStream(content);
            nint streamPointer = (nint)ComInterfaceMarshaller<IStream>.ConvertToUnmanaged(managed);
            try
            {
                environment.Object
                    .CreateWebResourceResponse(streamPointer, statusCode, reasonPhrase, headers, out nint response)
                    .ThrowOnError();

                return ComObject.FromPointer<ICoreWebView2WebResourceResponse>(response);
            }
            finally
            {
                ComInterfaceMarshaller<IStream>.Free((void*)streamPointer);
            }
        }
    }

    extension(IComObject<ICoreWebView2Controller> controller)
    {
        public bool IsVisible
        {
            get
            {
                controller.Object.get_IsVisible(out int value).ThrowOnError();
                return value != 0;
            }
            set => controller.Object.put_IsVisible(value ? 1 : 0).ThrowOnError();
        }

        public WV2RECT Bounds
        {
            get
            {
                controller.Object.get_Bounds(out WV2RECT value).ThrowOnError();
                return value;
            }
            set => controller.Object.put_Bounds(value).ThrowOnError();
        }

        public double ZoomFactor
        {
            get
            {
                controller.Object.get_ZoomFactor(out double value).ThrowOnError();
                return value;
            }
            set => controller.Object.put_ZoomFactor(value).ThrowOnError();
        }

        public IComObject<ICoreWebView2>? CoreWebView2
        {
            get
            {
                controller.Object.get_CoreWebView2(out nint value).ThrowOnError();
                return ComObject.FromPointer<ICoreWebView2>(value);
            }
        }

        public void MoveFocus(int reason) => controller.Object.MoveFocus(reason).ThrowOnError();

        public void Close() => controller.Object.Close().ThrowOnError();
    }

    extension(IComObject<ICoreWebView2CompositionController> controller)
    {
        /// <summary>
        /// 挂到 DirectComposition 树上的根视觉。
        /// </summary>
        /// <remarks>
        /// 底层是 <c>IUnknown*</c>，所以这里接受任意 COM 对象并取它的 IUnknown。
        /// 取不到时抛异常而不是静默跳过——挂不上去的后果是整块白屏。
        /// </remarks>
        public object? RootVisualTarget
        {
            set
            {
                nint unknown = value is null ? 0 : GetIUnknown(value);
                controller.Object.put_RootVisualTarget(unknown).ThrowOnError();
            }
        }

        public nint Cursor
        {
            get
            {
                controller.Object.get_Cursor(out nint value).ThrowOnError();
                return value;
            }
        }

        public void SendMouseInput(int eventKind, int virtualKeys, uint mouseData, WV2POINT point)
            => controller.Object.SendMouseInput(eventKind, virtualKeys, mouseData, point).ThrowOnError();
    }

    extension(IComObject<ICoreWebView2> webView)
    {
        public void Navigate(string uri) => webView.Object.Navigate(uri).ThrowOnError();

        public void Reload() => webView.Object.Reload().ThrowOnError();

        public void GoBack() => webView.Object.GoBack().ThrowOnError();

        public void GoForward() => webView.Object.GoForward().ThrowOnError();

        public void Stop() => webView.Object.Stop().ThrowOnError();

        public bool CanGoBack
        {
            get
            {
                webView.Object.get_CanGoBack(out int value).ThrowOnError();
                return value != 0;
            }
        }

        public bool CanGoForward
        {
            get
            {
                webView.Object.get_CanGoForward(out int value).ThrowOnError();
                return value != 0;
            }
        }

        public string? DocumentTitle
        {
            get
            {
                webView.Object.get_DocumentTitle(out nint value).ThrowOnError();
                return new PWSTR(value).ToStringAndDispose();
            }
        }

        public void PostWebMessageAsJson(string json) => webView.Object.PostWebMessageAsJson(json).ThrowOnError();

        public void PostWebMessageAsString(string message) => webView.Object.PostWebMessageAsString(message).ThrowOnError();

        public void AddWebResourceRequestedFilter(string uri, COREWEBVIEW2_WEB_RESOURCE_CONTEXT context)
            => webView.Object.AddWebResourceRequestedFilter(uri, (int)context).ThrowOnError();

        public IComObject<ICoreWebView2Settings5>? Settings
        {
            get
            {
                webView.Object.get_Settings(out nint settings).ThrowOnError();
                if (settings == 0)
                {
                    return null;
                }

                using IComObject<ICoreWebView2Settings> raw = ComObject.FromRequiredPointer<ICoreWebView2Settings>(settings);
                Guid iid = typeof(ICoreWebView2Settings5).GUID;
                if (Marshal.QueryInterface(settings, in iid, out nint upgraded) < 0 || upgraded == 0)
                {
                    return null;
                }

                return ComObject.FromPointer<ICoreWebView2Settings5>(upgraded);
            }
        }

        public IComObject<ICoreWebView2Profile2>? Profile
        {
            get
            {
                ICoreWebView2_13? view13 = WebView2Utilities.GetInterface<ICoreWebView2_13>(webView.Object);
                if (view13 is null)
                {
                    return null;
                }

                view13.get_Profile(out nint profile).ThrowOnError();
                if (profile == 0)
                {
                    return null;
                }

                using IComObject<ICoreWebView2Profile> raw = ComObject.FromRequiredPointer<ICoreWebView2Profile>(profile);
                Guid iid = typeof(ICoreWebView2Profile2).GUID;
                if (Marshal.QueryInterface(profile, in iid, out nint upgraded) < 0 || upgraded == 0)
                {
                    return null;
                }

                return ComObject.FromPointer<ICoreWebView2Profile2>(upgraded);
            }
        }

        /// <summary>执行脚本并取回 JSON 结果。</summary>
        public Task<string?> ExecuteScriptAsync(string script)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            nint handlerPointer = 0;
            var handler = new ExecuteScriptCompletedHandler((hr, result) =>
            {
                PendingHandlers.Remove(handlerPointer);

                if (hr < 0)
                {
                    completion.TrySetException(Marshal.GetExceptionForHR(hr)
                        ?? new COMException($"执行脚本失败（HRESULT 0x{hr:X8}）。", hr));
                }
                else
                {
                    completion.TrySetResult(result);
                }

                return 0;
            });

            try
            {
                handlerPointer = (nint)ComInterfaceMarshaller<ICoreWebView2ExecuteScriptCompletedHandler>
                    .ConvertToUnmanaged(handler);
                PendingHandlers.Add(handlerPointer, handler);

                int hr = webView.Object.ExecuteScript(script, handlerPointer);
                if (hr < 0)
                {
                    PendingHandlers.Remove(handlerPointer);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch (Exception ex)
            {
                PendingHandlers.Remove(handlerPointer);
                completion.TrySetException(ex);
            }
            finally
            {
                if (handlerPointer != 0)
                {
                    ComInterfaceMarshaller<ICoreWebView2ExecuteScriptCompletedHandler>.Free((void*)handlerPointer);
                }
            }

            return completion.Task;
        }

        /// <summary>注入一段每条新文档都要执行的脚本，返回脚本 id。</summary>
        public Task<string?> AddScriptToExecuteOnDocumentCreatedAsync(string javaScript)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            nint handlerPointer = 0;
            var handler = new AddScriptCompletedHandler((hr, id) =>
            {
                PendingHandlers.Remove(handlerPointer);

                if (hr < 0)
                {
                    completion.TrySetException(Marshal.GetExceptionForHR(hr)
                        ?? new COMException($"注入脚本失败（HRESULT 0x{hr:X8}）。", hr));
                }
                else
                {
                    completion.TrySetResult(id);
                }

                return 0;
            });

            try
            {
                handlerPointer = (nint)ComInterfaceMarshaller<ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler>
                    .ConvertToUnmanaged(handler);
                PendingHandlers.Add(handlerPointer, handler);

                int hr = webView.Object.AddScriptToExecuteOnDocumentCreated(javaScript, handlerPointer);
                if (hr < 0)
                {
                    PendingHandlers.Remove(handlerPointer);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch (Exception ex)
            {
                PendingHandlers.Remove(handlerPointer);
                completion.TrySetException(ex);
            }
            finally
            {
                if (handlerPointer != 0)
                {
                    ComInterfaceMarshaller<ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler>
                        .Free((void*)handlerPointer);
                }
            }

            return completion.Task;
        }

        /// <summary>订阅资源拦截。handler 的生命周期由调用方负责。</summary>
        public void add_WebResourceRequested(CoreWebView2WebResourceRequestedEventHandler handler, ref EventRegistrationToken token)
        {
            nint pointer = (nint)ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>
                .ConvertToUnmanaged(handler);
            try
            {
                webView.Object.add_WebResourceRequested(pointer, out token).ThrowOnError();
            }
            finally
            {
                ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.Free((void*)pointer);
            }
        }
    }

    extension(IComObject<ICoreWebView2Settings5> settings)
    {
        public bool AreDevToolsEnabled
        {
            get
            {
                settings.Object.get_AreDevToolsEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.Object.put_AreDevToolsEnabled(value ? 1 : 0).ThrowOnError();
        }

        public bool IsZoomControlEnabled
        {
            get
            {
                settings.Object.get_IsZoomControlEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.Object.put_IsZoomControlEnabled(value ? 1 : 0).ThrowOnError();
        }

        public bool IsPinchZoomEnabled
        {
            get
            {
                settings.Object.get_IsPinchZoomEnabled(out int value).ThrowOnError();
                return value != 0;
            }
            set => settings.Object.put_IsPinchZoomEnabled(value ? 1 : 0).ThrowOnError();
        }
    }

    extension(IComObject<ICoreWebView2Profile2> profile)
    {
        /// <summary>清除全部浏览数据。</summary>
        public Task ClearBrowsingDataAllAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            nint handlerPointer = 0;
            var handler = new ClearBrowsingDataCompletedHandler(hr =>
            {
                PendingHandlers.Remove(handlerPointer);

                if (hr < 0)
                {
                    completion.TrySetException(Marshal.GetExceptionForHR(hr)
                        ?? new COMException($"清除浏览数据失败（HRESULT 0x{hr:X8}）。", hr));
                }
                else
                {
                    completion.TrySetResult();
                }

                return 0;
            });

            try
            {
                handlerPointer = (nint)ComInterfaceMarshaller<ICoreWebView2ClearBrowsingDataCompletedHandler>
                    .ConvertToUnmanaged(handler);
                PendingHandlers.Add(handlerPointer, handler);

                int hr = profile.Object.ClearBrowsingDataAll(handlerPointer);
                if (hr < 0)
                {
                    PendingHandlers.Remove(handlerPointer);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch (Exception ex)
            {
                PendingHandlers.Remove(handlerPointer);
                completion.TrySetException(ex);
            }
            finally
            {
                if (handlerPointer != 0)
                {
                    ComInterfaceMarshaller<ICoreWebView2ClearBrowsingDataCompletedHandler>.Free((void*)handlerPointer);
                }
            }

            return completion.Task;
        }
    }

    /// <summary>
    /// 取一个托管 COM 对象的 IUnknown 指针。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="ComWrappers.TryGetComInstance"/>：它对源生成器、DirectN、系统 ComWrappers
    /// 创建的对象都有效，而 <c>Marshal.GetIUnknownForObject</c> 只认内置的 <c>__ComObject</c>。
    /// </remarks>
    private static nint GetIUnknown(object instance)
    {
        if (ComWrappers.TryGetComInstance(instance, out nint unknown) && unknown != 0)
        {
            return unknown;
        }

        try
        {
            return Marshal.GetIUnknownForObject(instance);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法从 {instance.GetType().Name} 取出 IUnknown：它既不是 ComWrappers 实例，也不是内置 COM 对象。", ex);
        }
    }
}
