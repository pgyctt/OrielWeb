namespace OrielWeb;

/// <summary>
/// <c>convertFileSrc</c> 的服务端：把 <c>oriel://file.oriel/…</c> 的请求变成磁盘上的字节。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里是 <c>convertFileSrc</c> 唯一的强制点。</b> 页面侧那个函数只是拼 URL
/// （同步、不查任何东西，Tauri 亦然），所以"能不能读这个文件"必须在这里判——
/// 否则 <c>convertFileSrc</c> 就成了一个绕过 <c>fs.*</c> scope 的读取通道：
/// 页面只要拿到一个 URL 就能把任意文件读进内存（<c>fetch</c> 之后转成 base64 发出去）。
/// </para>
/// <para>
/// <b>它有独立的 scope（<c>asset.read</c>），不共用 <c>fs.*</c> 的。</b>
/// 这是照 Tauri 的设计（那边 <c>assetProtocol.scope</c> 与 fs 的 scope 是两套配置）：
/// "把图片显示出来"与"以程序的方式读写文件"是两种意图，共用一个 scope 会让
/// 只想显示一张图的应用顺带获得读任意文本的能力。代价是配置要多写一行，那是值得的。
/// </para>
/// <para>
/// <b>只服务文件，不服务目录</b>：目录请求返回 403，而不是列出内容——
/// 列目录是 <c>fs.readDir</c> 的职责，那条路有自己的授权与形状。
/// </para>
/// </remarks>
public static class OrielAssetFileServer
{
    /// <summary>
    /// 本服务对应的"命令名"——`convertFileSrc` 的路径 scope 用**这一个名字**声明。
    /// </summary>
    /// <remarks>
    /// 它是公开契约的一部分：应用要写
    /// <c>c.Allow(OrielAssetFileServer.ScopeCommand, s =&gt; s.AllowPath("$APPDATA/img/**"))</c>。
    /// 所以这个类型是 public 的（而 <see cref="Serve"/> 保持 internal——
    /// 那个是引擎回调用的，不是给应用调的）。
    /// </remarks>
    public const string ScopeCommand = "asset.read";

    /// <summary>一次应答的结果。</summary>
    /// <param name="Payload">响应体；拒绝时是空流。</param>
    /// <param name="ContentType">响应的 MIME 类型。</param>
    /// <param name="Status">HTTP 状态码。</param>
    /// <param name="Reason">非 200 时写进日志的原因（页面看不到，避免泄露路径是否存在）。</param>
    internal readonly record struct Result(Stream Payload, string ContentType, int Status, string? Reason);

    /// <summary>
    /// 处理一次文件请求。
    /// </summary>
    /// <param name="requestPath">URL 里解出来的路径（可能是相对路径）。</param>
    /// <param name="authorize">判定这个绝对路径能不能读；由应用侧接上 scope 与标识符。</param>
    /// <returns>应答；任何情况下都不抛（调用方跑在引擎的回调里，抛出等于让引擎等一个永不到来的应答）。</returns>
    /// <remarks>
    /// <para>
    /// <b>顺序：先归一，再判 scope，最后才碰磁盘。</b> 反过来的话
    /// <c>$APPDATA/../../etc/passwd</c> 会先被判成"在 $APPDATA 之内"（字面量确实在内），
    /// 然后归一后指到别处。同样的顺序要求在 <c>fs.*</c> 那边也有一条（见 OrielBuiltInFsCommands）。
    /// </para>
    /// <para>
    /// <b>拒绝一律给 403，理由只进日志、不进响应。</b> 把理由写进响应体看起来对排障友好，
    /// 但那会变成一个探测器：页面能用它区分"文件不存在"与"路径不在 scope 内"，
    /// 进而枚举出这台机器上有哪些路径。
    /// </para>
    /// </remarks>
    internal static Result Serve(string requestPath, Func<string, bool> authorize)
    {
        string full;

        try
        {
            full = Path.GetFullPath(requestPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Deny($"路径不合法：{ex.Message}");
        }

        if (!authorize(full))
        {
            return Deny($"路径不在 {ScopeCommand} 的 scope 内：{full}");
        }

        try
        {
            if (Directory.Exists(full))
            {
                // 目录不通过这条通道列出内容：列目录是 fs.readDir 的职责
                return Deny($"是目录，本通道只服务文件：{full}");
            }

            if (!File.Exists(full))
            {
                return Deny($"文件不存在：{full}");
            }

            // 先整个读进内存再交出去：引擎是**异步**读这个流的（回调返回之后还在读），
            // 所以不能在这里 using/dispose 一个 FileStream。
            // 代价是文件全量进内存——对"显示图片/视频首帧"这类用法是可以接受的，
            // 而大文件本来也不该走这条通道（见文档里的说明）。
            byte[] payload = File.ReadAllBytes(full);
            return new Result(
                new MemoryStream(payload, writable: false),
                MimeTypes.ForPath(full),
                200,
                null);
        }
        catch (Exception ex)
        {
            // 包括 UnauthorizedAccessException（文件被占用/无权限）——对页面一律是 403
            return Deny($"读取失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Result Deny(string reason)
        => new(Stream.Null, "text/plain; charset=utf-8", 403, reason);
}
