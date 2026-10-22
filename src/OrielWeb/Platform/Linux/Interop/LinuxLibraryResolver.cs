using System.Reflection;
using System.Runtime.InteropServices;

namespace OrielWeb.Platform.Linux.Interop;

/// <summary>
/// Linux 上 WebKitGTK / JavaScriptCore 的**库名解析**：4.1 找不到时回退到 4.0。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：WebKitGTK 在 2.40 把 GTK3 那一版的 soname 从
/// <c>libwebkit2gtk-4.0.so.37</c> 改成了 <c>libwebkit2gtk-4.1.so.0</c>（4.0 这个名字让给
/// 了 GTK4 那一支）。但**发行版并不跟着改**：Ubuntu 24.04 / Fedora 都发 4.1，
/// 而 Ubuntu 22.04 至今仍在 4.0 这个名字下持续发安全更新——它里面的 WebKit 是新的，
/// 只是文件名沿用了旧的。
/// </para>
/// <para>
/// 后果是硬的：只按 4.1 加载时，<c>DllNotFoundException: libwebkit2gtk-4.1.so.0</c>
/// 会在**建第一个窗口**时抛出，应用一行界面都出不来。而 CI 的 <c>ubuntu-latest</c>
/// 是 24.04，永远看不到这条——这个缺口是在 Hyper-V 的 Ubuntu 22.04 真机上跑出来的
/// （见 docs/CROSS-PLATFORM-VERIFICATION.md）。
/// </para>
/// <para>
/// <b>为什么能直接换库</b>：本库用到的 32 个 <c>webkit_*</c> 与 1 个 <c>jsc_*</c> 符号在 4.0 里
/// **一个不缺**（逐个用 ctypes 探过），签名与语义一致——这两个名字背后是同一份 C API。
/// 换的只是"问系统要哪个文件名"，不是"用哪套 API"。
/// </para>
/// <para>
/// <b>为什么候选名里带 .37 / .18</b>：4.0 那一支的 libtool 版本号与 4.1 不同——
/// WebKit 是 <c>.so.37</c>、JavaScriptCore 是 <c>.so.18</c>，而 4.1 是 <c>.so.0</c>。
/// 只写 <c>.so.0</c> 会"看起来回退了、其实还是找不到"（本库的第一版就是这么错的，
/// 真机上一试便知：<c>ls /usr/lib/x86_64-linux-gnu/libwebkit2gtk-4.0*</c>）。
/// </para>
/// <para>
/// <b>为什么不干脆两个都试、按顺序拼 DllImport</b>：<c>DllImport</c> 的库名是编译期常量，
/// 一个 <c>EntryPoint</c> 只能挂一个名字。官方给的扩展点就是
/// <see cref="NativeLibrary.SetDllImportResolver"/>——每个程序集一次，把"名字 → 句柄"
/// 的解释权交给调用方。
/// </para>
/// </remarks>
internal static class LinuxLibraryResolver
{
    /// <summary>WebKit 的候选名，**按优先级**：声明的那一支在前，旧 soname 在后。</summary>
    /// <remarks>
    /// 第二、三项都是 4.0：<c>.so.37</c> 是 Ubuntu 22.04 / Debian 11 的实际文件名，
    /// <c>.so.0</c> 是"万一某个发行版重新编号"的保险——不会有害，找不到就跳过。
    /// </remarks>
    private static readonly string[] WebKitCandidates =
    [
        "libwebkit2gtk-4.1.so.0",
        "libwebkit2gtk-4.0.so.37",
        "libwebkit2gtk-4.0.so.0",
    ];

    /// <summary>JavaScriptCore 的候选名（<c>.so.18</c> 同上是 4.0 的 libtool 版本）。</summary>
    private static readonly string[] JavaScriptCoreCandidates =
    [
        "libjavascriptcoregtk-4.1.so.0",
        "libjavascriptcoregtk-4.0.so.18",
        "libjavascriptcoregtk-4.0.so.0",
    ];

    /// <summary>是否已经报告过"用了旧 soname"（只报一次：建多个窗口时不该刷屏）。</summary>
    private static int s_reported;

    /// <summary>是否已经注册过解析器（<see cref="Register"/> 幂等）。</summary>
    private static int s_registered;

    /// <summary>
    /// 注册解析器（幂等）。**由 <c>GtkNative</c> 的静态构造函数调用**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 时机是硬的：<c>DllImport</c> 的解析发生在**第一次调用那个方法**时，晚一步注册就没用。
    /// 所以这里不用 <c>[ModuleInitializer]</c>（那会让 CA2255 报警：库不该用模块初始化器），
    /// 而是挂在 <c>GtkNative</c> 的静态构造函数上——那是最早能保证"WebKit 还没被调过"的位置，
    /// 因为所有 <c>webkit_*</c> 声明都在那个类型里。
    /// </para>
    /// <para>
    /// <b>移动 WebKit 声明时注意</b>：这条依赖"WebKit 的 P/Invoke 与静态构造函数在同一个类型"。
    /// 哪天把 <c>webkit_*</c> 拆到别的类型，注册也要跟着搬，否则会退回"只认 4.1"的老状态。
    /// </para>
    /// </remarks>
    internal static void Register()
    {
        if (!OperatingSystem.IsLinux() || Interlocked.Exchange(ref s_registered, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LinuxLibraryResolver).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // 每个程序集只能设一次解析器。已经有人设过（宿主自己装了），
            // 那说明它知道怎么找库——用它的，不抢。
        }
        catch (Exception ex)
        {
            // 静态构造函数里抛异常会变成 TypeInitializationException，把真正的原因埋掉。
            // 这里只降级为"没有回退"，不影响 4.1 的正常加载。
            OrielDiagnostics.Report(
                $"[OrielWeb] 注册 Linux 库名解析器失败（{ex.GetType().Name}: {ex.Message}）："
                + "只会按 4.1 的 soname 加载 WebKitGTK。");
        }
    }

    /// <summary>
    /// 把 WebKit 的库名解析成实际加载的文件；返回 0 表示"不接管，走默认解析"。
    /// </summary>
    internal static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[]? candidates = libraryName switch
        {
            "libwebkit2gtk-4.1.so.0" => WebKitCandidates,
            "libjavascriptcoregtk-4.1.so.0" => JavaScriptCoreCandidates,
            _ => null,
        };

        if (candidates is null)
        {
            // 其余库（GTK / GDK / GLib / cairo…）的 soname 是稳定的，不接管。
            return 0;
        }

        foreach (string candidate in candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, assembly, searchPath, out nint handle))
            {
                continue;
            }

            if (candidate != candidates[0] && Interlocked.Exchange(ref s_reported, 1) == 0)
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] 本机没有 {candidates[0]}，已改用 {candidate}。"
                    + "两个名字背后是同一套 WebKitGTK C API（本库用到的符号在 4.0 里齐全），"
                    + "功能不受影响；要消掉这条提示，装 WebKitGTK 4.1（Ubuntu 24.04+ / Fedora 39+）。");
            }

            return handle;
        }

        // 一个都没找到 —— 交回默认解析，让报错信息里出现我们声明过的那个名字
        // （而不是内部回退用的那几个），用户按提示装包即可。
        return 0;
    }
}
