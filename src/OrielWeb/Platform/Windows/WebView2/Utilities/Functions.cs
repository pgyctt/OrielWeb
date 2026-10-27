using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Handlers;
using OrielWeb.Platform.Windows.WebView2.Interop;
using ComObject = OrielWeb.Platform.Windows.WebView2.Com.ComObject;

namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>
/// WebView2 的入口函数，形状与 WebView2Aot 的 <c>WebView2.Functions</c> 一致。
/// </summary>
internal static unsafe class Functions
{
    /// <summary>
    /// 创建（或复用）进程级 WebView2 环境。
    /// </summary>
    /// <param name="browserExecutableFolder">固定版本的运行时目录；<c>null</c> 表示用系统安装的 Evergreen 运行时。</param>
    /// <param name="userDataFolder">用户数据目录。同一目录不能同时存在两个环境。</param>
    /// <param name="environmentOptions">环境选项（自定义 scheme 注册走这里）。</param>
    public static Task<IComObject<ICoreWebView2Environment>?> CreateCoreWebView2EnvironmentWithOptionsAsync(
        string? browserExecutableFolder,
        string userDataFolder,
        ICoreWebView2EnvironmentOptions? environmentOptions)
    {
        var completion = new TaskCompletionSource<IComObject<ICoreWebView2Environment>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        nint handlerPointer = 0;
        nint optionsPointer = 0;

        var handler = new EnvironmentCompletedHandler((hr, createdEnvironment) =>
        {
            PendingHandlers.Remove(handlerPointer);

            if (hr < 0)
            {
                completion.TrySetException(Marshal.GetExceptionForHR(hr)
                    ?? new COMException($"创建 WebView2 环境失败（HRESULT 0x{hr:X8}）。", hr));
            }
            else
            {
                completion.TrySetResult(ComObject.FromPointer<ICoreWebView2Environment>(createdEnvironment));
            }

            return 0;
        });

        try
        {
            handlerPointer = (nint)ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>
                .ConvertToUnmanaged(handler);
            PendingHandlers.Add(handlerPointer, handler);

            if (environmentOptions is not null)
            {
                optionsPointer = (nint)ComInterfaceMarshaller<ICoreWebView2EnvironmentOptions>
                    .ConvertToUnmanaged(environmentOptions);
                PendingHandlers.Add(optionsPointer, environmentOptions);
            }

            int hr = WebView2Utilities.CreateEnvironmentWithOptions(
                browserExecutableFolder, userDataFolder, optionsPointer, handlerPointer);

            if (hr < 0)
            {
                PendingHandlers.Remove(handlerPointer);
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        catch (Exception ex)
        {
            if (handlerPointer != 0)
            {
                PendingHandlers.Remove(handlerPointer);
            }

            completion.TrySetException(ex);
        }
        finally
        {
            // 原生侧已经 AddRef 过了，这里放掉我们交出去的那一份。
            // 托管对象由 PendingHandlers 保活到回调触发为止。
            if (handlerPointer != 0)
            {
                ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>
                    .Free((void*)handlerPointer);
            }

            if (optionsPointer != 0)
            {
                ComInterfaceMarshaller<ICoreWebView2EnvironmentOptions>.Free((void*)optionsPointer);
                PendingHandlers.Remove(optionsPointer);
            }
        }

        return completion.Task;
    }
}

/// <summary>
/// 回调期间保活 CCW 的托管对象。
/// </summary>
/// <remarks>
/// <c>[GeneratedComClass]</c> 的 CCW 只是原生侧的一个引用计数外壳：托管对象一旦被 GC 回收，
/// 原生再回调就会拿到失败。异步调用返回后局部变量就没了，所以必须显式挂住，
/// 直到回调真正触发或调用失败为止。
/// </remarks>
internal static class PendingHandlers
{
    private static readonly ConcurrentDictionary<nint, object> s_items = new();

    public static void Add(nint pointer, object handler) => s_items[pointer] = handler;

    public static void Remove(nint pointer)
    {
        if (pointer != 0)
        {
            s_items.TryRemove(pointer, out _);
        }
    }
}
