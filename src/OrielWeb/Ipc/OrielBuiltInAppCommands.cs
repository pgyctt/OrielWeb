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
/// <b>B23 补齐了 Tauri <c>app</c> 模块的最后五项</b>：<c>getBundleType</c>（产物类型，
/// 观察不出来时返回 <c>null</c>——本库不是打包器，但能看见"自己在 .app 里 / 旁边有
/// Velopack 的 Update.exe / 跑在 AppImage 里"，见 <see cref="OrielBundleType"/>）、
/// <c>defaultWindowIcon</c>（可执行文件旁的 <c>app.png</c> / <c>app.ico</c>）、
/// <c>setDockVisibility</c>（macOS 的 Dock 显隐）、以及数据存储的
/// <c>fetchDataStoreIdentifiers</c> / <c>removeDataStore</c>（macOS 的 WKWebsiteDataStore）。
/// </para>
/// <para>
/// <b>门禁姿态分两档</b>：只读的三条（<c>getBundleType</c> / <c>defaultWindowIcon</c> /
/// <c>fetchDataStoreIdentifiers</c>）进 always-allowed；改应用状态的
/// <c>setDockVisibility</c> 与破坏性的 <c>removeDataStore</c> 必须应用显式 Allow。
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

            case "app.setTheme":
                // 对标 Tauri 的 App.setTheme。值是 'light' / 'dark' / null（null = 跟随系统）。
                // 作用域是"所有没自己覆写的窗口"——已设过 window.setTheme 的窗口不动
                // （见 OrielApp.SetTheme 的三级模型说明）。
                handler = static (window, args) =>
                {
                    string? raw = args.ValueKind == JsonValueKind.Object
                                  && args.TryGetProperty("value", out JsonElement value)
                                  && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;

                    OrielTheme? theme = raw switch
                    {
                        null => null,
                        "light" => OrielTheme.Light,
                        "dark" => OrielTheme.Dark,
                        _ => throw new OrielIpcException(
                            $"app.setTheme: 未知的 value「{raw}」（只认 light / dark / null）。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        },
                    };

                    window.App.SetTheme(theme);
                    return null;
                };
                return true;

            case "app.getBundleType":
                // 对标 Tauri 的 getBundleType()：不是打包产物时返回 null（那边也是 null）。
                // 值在应用构造期就算好了——这些运行期事实在进程生命期内不会变。
                handler = static (window, _) => window.App.BundleType;
                return true;

            case "app.defaultWindowIcon":
                // 对标 Tauri 的 defaultWindowIcon()：返回 Image | null。
                // 本库的图标来源是"可执行文件旁边的 app.png / app.ico"（见 OrielAppIcon），
                // 没有资源表。**线格式是 base64 字符串**（null = 没有图标），
                // 页面侧再包成 Image——Image 在页面侧本来就是字节容器。
                handler = static (_, _) => OrielAppIcon.Find() is { } bytes
                    ? Convert.ToBase64String(bytes)
                    : null;
                return true;

            case "app.setDockVisibility":
                // 对标 Tauri 的 setDockVisibility()（macOS 专有）。
                // 非 macOS 由后端抛 NotSupportedException，分发器翻成 not_supported。
                handler = static (window, args) =>
                {
                    window.App.SetDockVisibility(RequiredBool(args, "visible", "app.setDockVisibility"));
                    return null;
                };
                return true;

            case "app.fetchDataStoreIdentifiers":
                // 每个标识符是 16 字节（RFC 4122 顺序）的 base64：线格式用字符串数组，
                // 因为 OrielJson 对 IEnumerable<string> 有零反射的直接支持。
                handler = static (window, _) => window.App
                    .FetchDataStoreIdentifiers()
                    .Select(static id => Convert.ToBase64String(OrielDataStoreRegistry.ToBytes(id)))
                    .ToArray();
                return true;

            case "app.removeDataStore":
                // 对标 Tauri 的 removeDataStore(uuid: Uint8Array)。
                // 参数是 16 字节的 base64；长度不对是**调用方错误**（invalid_argument），
                // 不能当作"删了个不存在的东西"静默成功。
                handler = static (window, args) =>
                {
                    byte[] bytes = ReadIdentifierBytes(args, "app.removeDataStore");
                    if (!OrielDataStoreRegistry.TryFromBytes(bytes, out Guid identifier))
                    {
                        throw new OrielIpcException(
                            "app.removeDataStore: value 必须是 16 字节的 UUID。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        };
                    }

                    window.App.RemoveDataStore(identifier);
                    return null;
                };
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>
    /// 读一个布尔参数：先认 <c>value</c>（Tauri 的 JS 形状），再认语义名。
    /// </summary>
    /// <remarks>
    /// 与 <c>window.*</c> 那边的 <c>BoolValue</c> 同一套约定，理由也一样：
    /// Tauri 的 JS 统一发 <c>{ label, value }</c>，而手写代码常写 <c>{ visible: false }</c>。
    /// </remarks>
    private static bool RequiredBool(JsonElement args, string property, string commandName)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            string[] names = ["value", property];
            foreach (string name in names)
            {
                if (args.TryGetProperty(name, out JsonElement element)
                    && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return element.GetBoolean();
                }
            }
        }

        throw new OrielIpcException($"命令 '{commandName}' 缺少布尔参数 '{property}'。")
        {
            Code = OrielIpcErrorCodes.MissingArgument,
        };
    }

    /// <summary>读 <c>value</c> 里的 16 字节标识符（base64）。</summary>
    private static byte[] ReadIdentifierBytes(JsonElement args, string commandName)
    {
        string? encoded = args.ValueKind == JsonValueKind.Object
                          && args.TryGetProperty("value", out JsonElement value)
                          && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        if (encoded is null)
        {
            throw new OrielIpcException($"命令 '{commandName}' 缺少参数 'value'（UUID 的 base64）。")
            {
                Code = OrielIpcErrorCodes.MissingArgument,
            };
        }

        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new OrielIpcException($"命令 '{commandName}' 的 value 不是合法的 base64。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
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
