using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建应用命令族（<c>app.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/api/app</c> 里的**只读元信息**部分。
/// </summary>
/// <remarks>
/// <para>
/// <b>本族只做只读元信息</b>，不含 <c>exit</c> / <c>show</c> / <c>hide</c> / <c>setTheme</c> /
/// <c>getBundleType</c>。理由分别是：
/// </para>
/// <list type="bullet">
///   <item><description><c>exit</c> / <c>show</c> / <c>hide</c> 是**能力**（会改变应用状态），
///     不该混进一个"读元信息"的族里顺带放行。它们各自需要单独判断门禁姿态。</description></item>
///   <item><description><c>getBundleType</c> 与 <c>BundleType</c> 枚举**不适用**：它返回的是
///     Tauri 打包器的产物类型（<c>nsis</c> / <c>msi</c> / <c>deb</c> / <c>appimage</c> …）。
///     本库是库不是打包器，给不出这个值——编一个反而会让迁移代码基于错信息分支。
///     将来若真有"打包形态"的概念再补，见 docs/TAURI2-PLAN.md。</description></item>
/// </list>
/// <para>
/// <b>门禁姿态：整族始终放行。</b> 见 <see cref="OrielCapabilityRules.AlwaysAllowedAppCommands"/>——
/// 这几条读的都是**宿主本来就会告诉页面**的东西（页面来源、窗口 label 都在它的环境里），
/// 没有一条泄露应用不该知道的信息。
/// </para>
/// </remarks>
internal static class OrielBuiltInAppCommands
{
    /// <summary>命令名前缀。</summary>
    internal const string Prefix = "app.";

    /// <summary>取某个 <c>app.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    /// <remarks>
    /// 处理函数收 <see cref="IOrielWindowControl"/> 是**为了拿到应用实例**（<c>window.App</c>），
    /// 不是为了操作那个窗口——所以下面没有一条命令用它的 label 或几何。
    /// </remarks>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        switch (name)
        {
            case "app.getName":
                handler = static (window, _) => window.App.Info.Name;
                return true;

            case "app.getVersion":
                handler = static (window, _) => window.App.Info.Version;
                return true;

            case "app.getIdentifier":
                handler = static (window, _) => window.App.Info.Identifier;
                return true;

            case "app.getTauriVersion":
                // 对标 Tauri 的 getTauriVersion()：那边给**框架**版本，这边给**库**版本。
                // 页面拿它做能力探测，所以必须与 oriel.version 同源——
                // 两个来源会让"我该信哪个"变成一个没有答案的问题。
                handler = static (_, _) => OrielLibrary.Version;
                return true;

            case "app.supportsMultipleWindows":
                // 三平台都支持多窗口，所以恒 true。保留这条命令是为了让页面能原样迁移：
                // Tauri 上它是查询（移动端返回 false），调用方会据此分支——
                // 直接删掉会让那些分支编译不过。
                handler = static (_, _) => true;
                return true;

            default:
                handler = null!;
                return false;
        }
    }
}
