using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建应用命令族（<c>app.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/api/app</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>两类命令，门禁姿态不同</b>：只读元信息（<c>getName</c> / <c>getVersion</c> …）
/// 整组始终放行；有状态的 <c>exit</c> / <c>show</c> / <c>hide</c> 会改变应用状态，
/// **必须由应用显式 Allow**——见 <see cref="OrielCapabilityRules.AlwaysAllowedAppCommands"/>。
/// 门禁按命令名判定，所以同族里分得开。
/// </para>
/// <para>
/// <b>刻意不做的</b>：<c>getBundleType</c> 与 <c>BundleType</c> 枚举——它返回的是 Tauri
/// 打包器的产物类型（<c>nsis</c> / <c>msi</c> / <c>deb</c> / <c>appimage</c> …）。
/// 本库是库不是打包器，给不出这个值；编一个反而会让迁移代码基于错信息分支。
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

            case "app.exit":
                handler = static (window, args) =>
                {
                    int? exitCode = OptionalExitCode(args);

                    // Quit 的线程相关性是硬约束（见 OrielApp.Quit 的注释）：这里统一编组，
                    // 已在 UI 线程时 InvokeOnUiThread 会同步执行，不会多绕一圈。
                    window.App.InvokeOnUiThread(() =>
                    {
                        // 退出码写进 Environment.ExitCode：进程在 Main 正常返回时按它退出。
                        // 走看门狗强制结束时用的是 UseForceExitCode（那条路不看这里）——
                        // 已经在 API.md 写明，免得有人以为被强杀时退出码也可控。
                        if (exitCode is int code)
                        {
                            Environment.ExitCode = code;
                        }

                        window.App.Quit();
                    });

                    return null;
                };
                return true;

            case "app.show":
                handler = static (window, _) =>
                {
                    window.App.ShowAllWindows();
                    return null;
                };
                return true;

            case "app.hide":
                handler = static (window, _) =>
                {
                    window.App.HideAllWindows();
                    return null;
                };
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>读可选的退出码；缺省或不是整数时按"没给"处理（进程退出码保持原样）。</summary>
    private static int? OptionalExitCode(JsonElement args)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty("code", out JsonElement element)
           && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out int value)
            ? value
            : null;
}
