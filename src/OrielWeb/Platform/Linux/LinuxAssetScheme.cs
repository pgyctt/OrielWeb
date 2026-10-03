using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 侧的 <c>oriel://</c> 应答器：把内嵌资源直接喂给引擎，不经过磁盘。
/// </summary>
/// <remarks>
/// <para>
/// 取代了旧的"先解压到 <c>%LocalAppData%</c>，再把 <c>https://</c> 改写成 <c>file://</c>"。
/// 三件事在这里定死，每一条都是踩过或极易踩的：
/// </para>
/// <list type="number">
///   <item><b>注册时机</b>：<c>webkit_web_context_register_uri_scheme</c> 必须在**任何 webview 创建之前**
///     调用，因此调用点在建 view 前一行（见 <c>LinuxWindowHost.Create</c>），而不是在导航时。</item>
///   <item><b>内存所有权</b>：缓冲区用 <c>Marshal.AllocHGlobal</c> 分配、交给
///     <c>g_memory_input_stream_new_from_data</c> 的 destroy 回调释放。读完就 free 是 use-after-free
///     （WebKit 异步读流），永不释放是每次请求漏一块。</item>
///   <item><b>404 必须走 finish_error</b>：自定义 scheme 的 <c>finish</c> 没有状态码，用它应答"不存在"
///     会把一次失败导航变成**成功**导航——而自检里"导航到不存在的页面应当失败"那一步正靠这个失败
///     （见 samples/OrielDemo/NavSelfTest.cs）。</item>
/// </list>
/// </remarks>
internal static unsafe class LinuxAssetScheme
{
    /// <summary>WEBKIT_NETWORK_ERROR_FILE_DOES_NOT_EXIST（WebKitError.h 里的取值）。</summary>
    private const int NetworkErrorFileDoesNotExist = 303;

    private static readonly object Gate = new();

    private static EmbeddedAssetStore? s_store;
    private static string? s_host;
    private static bool s_registered;

    /// <summary>
    /// 注册（进程内只注册一次）并记录当前应用的资源表与 host。
    /// </summary>
    /// <remarks>
    /// 表与 host **每次调用都更新**：同一进程内重建 <c>OrielApp</c>（测试宿主、或"关掉再开"的应用）
    /// 时，回调必须答当前这一份。scheme 本身只能注册一次，所以"注册"与"更新"在这里分开。
    /// </remarks>
    internal static void Register(EmbeddedAssetStore store, string host)
    {
        lock (Gate)
        {
            s_store = store;
            s_host = host;

            if (s_registered)
            {
                return;
            }

            s_registered = true;
        }

        nint context = GtkNative.WebkitWebContextGetDefault();
        if (context == 0)
        {
            throw new InvalidOperationException(
                "取不到默认 WebKitWebContext，无法注册 oriel:// ——内嵌资源将无法加载。");
        }

        GtkNative.WebkitWebContextRegisterUriScheme(
            context,
            AssetUrl.Scheme,
            (void*)(delegate* unmanaged<nint, nint, void>)&OnRequest,
            0,
            0);

        // 标记为安全来源：否则 window.isSecureContext 为假，crypto.subtle 与部分存储 API 不可用。
        // 拿不到 security manager 不是致命错误（只是页面失去这些能力），所以不抛。
        nint security = GtkNative.WebkitWebContextGetSecurityManager(context);
        if (security != 0)
        {
            GtkNative.WebkitSecurityManagerRegisterUriSchemeAsSecure(security, AssetUrl.Scheme);
        }
    }

    /// <summary>引擎来取一件内嵌资源（主线程回调）。</summary>
    [UnmanagedCallersOnly]
    private static void OnRequest(nint request, nint userData)
    {
        EmbeddedAssetStore? store;
        string? host;
        lock (Gate)
        {
            store = s_store;
            host = s_host;
        }

        nint rawUri = GtkNative.WebkitUriSchemeRequestGetUri(request);
        string uri = rawUri == 0 ? string.Empty : Marshal.PtrToStringUTF8(rawUri) ?? string.Empty;

        if (store is null || host is null || !AssetUrl.TryResolve(uri, host, out string relative))
        {
            // scheme 处理器只该收到本库自己的地址；真收到别的（host 被改、scheme 认错）时
            // 报出实际地址，而不是让引擎一直等一个永不到来的应答。
            FinishWithError(request, $"不认得的资源地址：{uri}");
            return;
        }

        if (!store.TryGet(relative, out EmbeddedAsset asset))
        {
            FinishWithError(request, $"内嵌资源不存在：{relative}");
            return;
        }

        byte[] payload;
        try
        {
            using Stream source = store.Open(asset);
            payload = new byte[source.Length];
            source.ReadExactly(payload);
        }
        catch (Exception ex)
        {
            FinishWithError(request, $"读取内嵌资源失败：{asset.ResourceName}（{ex.Message}）");
            return;
        }

        nint buffer = Marshal.AllocHGlobal(payload.Length);
        Marshal.Copy(payload, 0, buffer, payload.Length);

        nint stream = GtkNative.GMemoryInputStreamNewFromData(
            buffer,
            payload.Length,
            (nint)(delegate* unmanaged<nint, void>)&FreeBuffer);

        if (stream == 0)
        {
            // 流没建起来，destroy 回调也就不会跑，这段内存得自己收回来。
            Marshal.FreeHGlobal(buffer);
            FinishWithError(request, "创建内存流失败");
            return;
        }

        GtkNative.WebkitUriSchemeRequestFinish(request, stream, payload.Length, asset.ContentType);

        // 交出去之后放掉我们自己那份引用：WebKit 会持有它读完流（异步），
        // 而缓冲区由 destroy 回调在流销毁时释放。
        GtkNative.GObjectUnref(stream);
    }

    /// <summary>由 GLib 在流销毁时回调，释放上面那段非托管内存。</summary>
    [UnmanagedCallersOnly]
    private static void FreeBuffer(nint data) => Marshal.FreeHGlobal(data);

    private static void FinishWithError(nint request, string message)
    {
        nint error = GtkNative.GErrorNewLiteral(
            GtkNative.WebkitNetworkErrorQuark(), NetworkErrorFileDoesNotExist, message);

        GtkNative.WebkitUriSchemeRequestFinishError(request, error);

        // GIR 里这个参数是 transfer-ownership="none"（"in"）：WebKit 不接管，我们自己释放。
        GtkNative.GErrorFree(error);
    }
}
