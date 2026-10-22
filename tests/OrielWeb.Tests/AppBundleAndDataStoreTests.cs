using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// B23 的五项应用级能力：产物类型、默认图标、Dock 显隐、数据存储标识符、返回键。
/// </summary>
/// <remarks>
/// <para>
/// 这一批有两处"纯逻辑"值得单测（不依赖真机）：产物类型的推断规则与图标文件的魔数判断。
/// 其余四处在测试进程里都走不到平台实现（应用没 <c>Run()</c>），所以断言到
/// "门禁放行 → 进到实现的前置检查"为止——真实的平台行为由 demo 的 <c>--selftest</c> 取证。
/// </para>
/// <para>
/// <b>门禁姿态是这批最容易在后续重构里被简化掉的东西</b>：三条只读命令进 always-allowed，
/// 两条会改状态/删数据的必须显式 Allow。这里把那条线钉住。
/// </para>
/// </remarks>
public sealed class AppBundleAndDataStoreTests
{
    // ---------------------------------------------------------------- 产物类型

    [Theory]
    [InlineData("nsis", "nsis")]
    [InlineData("  MSI ", "msi")]
    [InlineData("App", "app")]
    [InlineData("deb", "deb")]
    [InlineData("rpm", "rpm")]
    [InlineData("appimage", "appimage")]
    [InlineData("dmg", null)]      // Tauri 2 的枚举里没有 dmg
    [InlineData("exe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_OnlyAcceptsKnownValues(string? declared, string? expected)
        => Assert.Equal(expected, OrielBundleType.Normalize(declared));

    [Fact]
    public void Detect_DeclaredValueWinsOverObservation()
    {
        // 显式声明比任何推断都准：应用自己知道是怎么打包的
        string? result = OrielBundleType.Detect(
            "msi",
            "/Applications/Example.app/Contents/MacOS",
            isMacOS: true,
            isWindows: false,
            appImagePath: "/tmp/Example.AppImage");

        Assert.Equal("msi", result);
    }

    [Fact]
    public void Detect_UnknownDeclaredValueFallsBackToObservation()
    {
        string? result = OrielBundleType.Detect(
            "exe",
            "/Applications/Example.app/Contents/MacOS",
            isMacOS: true,
            isWindows: false,
            appImagePath: null);

        Assert.Equal("app", result);
    }

    [Fact]
    public void Detect_MacBundle()
    {
        Assert.Equal(
            "app",
            OrielBundleType.Detect(null, "/Applications/Example.app/Contents/MacOS/", true, false, null));

        // 不在 .app 里（开发期直接跑 publish 输出）→ 观察不出来
        Assert.Null(OrielBundleType.Detect(null, "/Users/dev/app/bin/Debug/net10.0/", true, false, null));
    }

    [Fact]
    public void Detect_AppImage()
    {
        Assert.Equal(
            "appimage",
            OrielBundleType.Detect(null, "/tmp/.mount_xxxx/", false, false, "/home/u/Example.AppImage"));

        // 空字符串不算（有的容器会把它设成空串）
        Assert.Null(OrielBundleType.Detect(null, "/tmp/", false, false, ""));
    }

    [Fact]
    public void Detect_VelopackLayout_IsNsis()
    {
        string root = Directory.CreateTempSubdirectory("oriel-velopack-").FullName;
        try
        {
            string current = Path.Combine(root, "current");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(root, "Update.exe"), string.Empty);

            // 安装后 exe 在 current/ 下，Update.exe 在上一级
            Assert.Equal("nsis", OrielBundleType.Detect(null, current, false, true, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_PlainWindowsBuild_IsNull()
    {
        string root = Directory.CreateTempSubdirectory("oriel-plain-").FullName;
        try
        {
            // 没有 Update.exe：开发期/手工解压
            Assert.Null(OrielBundleType.Detect(null, root, false, true, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/Applications/X.app/Contents/MacOS", true)]
    [InlineData("C:\\Apps\\X.app\\Contents\\MacOS", true)]
    [InlineData("/Applications/X.app/Contents/Resources", false)]
    [InlineData("/opt/x/bin", false)]
    [InlineData("", false)]
    public void LooksLikeMacBundle_MatchesOnlyContentMacOs(string directory, bool expected)
        => Assert.Equal(expected, OrielBundleType.LooksLikeMacBundle(directory));

    // ---------------------------------------------------------------- 默认图标

    [Fact]
    public void LooksLikeImage_AcceptsPngAndIcoOnly()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
        byte[] ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10];
        byte[] emptyIco = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
        byte[] text = "not an image"u8.ToArray();

        Assert.True(OrielAppIcon.LooksLikeImage(png));
        Assert.True(OrielAppIcon.LooksLikeImage(ico));
        Assert.False(OrielAppIcon.LooksLikeImage(emptyIco), "0 个图像的 ICO 解析不出尺寸");
        Assert.False(OrielAppIcon.LooksLikeImage(text));
        Assert.False(OrielAppIcon.LooksLikeImage([]));
    }

    [Fact]
    public void FindIn_PrefersPngAndSkipsInvalidFiles()
    {
        string root = Directory.CreateTempSubdirectory("oriel-icon-").FullName;
        try
        {
            // 一个谎称是 png 的文本文件：必须被拒绝（否则页面侧解析尺寸时才炸）
            File.WriteAllText(Path.Combine(root, "app.png"), "definitely not a png");
            File.WriteAllBytes(
                Path.Combine(root, "app.ico"),
                [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x20, 0x20]);

            byte[]? icon = OrielAppIcon.FindIn(root);
            Assert.NotNull(icon);
            Assert.Equal(0x01, icon![4]); // 拿到的是那个 ICO（PNG 被拒后继续找）

            File.WriteAllBytes(
                Path.Combine(root, "app.png"),
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Assert.Equal(8, OrielAppIcon.FindIn(root)!.Length); // PNG 优先
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindIn_NoFiles_ReturnsNull()
    {
        string root = Directory.CreateTempSubdirectory("oriel-icon-empty-").FullName;
        try
        {
            Assert.Null(OrielAppIcon.FindIn(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- 数据存储登记表

    [Fact]
    public void IdentifierBytes_RoundTripInRfcOrder()
    {
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        byte[] bytes = OrielDataStoreRegistry.ToBytes(identifier);

        // RFC 4122 顺序：字符串里的字节逐个写出来，而不是 Guid.ToByteArray() 的小端排列
        Assert.Equal(
            new byte[]
            {
                0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
                0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
            },
            bytes);

        Assert.True(OrielDataStoreRegistry.TryFromBytes(bytes, out Guid parsed));
        Assert.Equal(identifier, parsed);

        Assert.False(OrielDataStoreRegistry.TryFromBytes(bytes.AsSpan(0, 15), out _));
        Assert.False(OrielDataStoreRegistry.TryFromBytes([], out _));
    }

    [Fact]
    public void Registry_PersistsAcrossInstances()
    {
        string root = Directory.CreateTempSubdirectory("oriel-datastores-").FullName;
        try
        {
            string path = Path.Combine(root, "nested", OrielDataStoreRegistry.FileName);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            var writer = new OrielDataStoreRegistry(path);
            Assert.True(writer.Add(first));
            Assert.False(writer.Add(first)); // 重复登记是幂等的
            Assert.True(writer.Add(second));

            // 新实例（模拟重启）应读回同样的两个标识符
            var reader = new OrielDataStoreRegistry(path);
            Assert.Equal([first, second], reader.Identifiers);
            Assert.True(reader.Contains(first));

            Assert.True(reader.Remove(first));
            Assert.False(reader.Remove(first));
            Assert.Equal([second], new OrielDataStoreRegistry(path).Identifiers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_CorruptFileIsTreatedAsEmpty()
    {
        string root = Directory.CreateTempSubdirectory("oriel-datastores-bad-").FullName;
        try
        {
            string path = Path.Combine(root, OrielDataStoreRegistry.FileName);
            File.WriteAllText(path, "{ not json");

            var registry = new OrielDataStoreRegistry(path);
            Assert.Empty(registry.Identifiers);

            // 损坏的表不该阻止后续登记（下一次写会覆盖掉那个坏文件）
            var identifier = Guid.NewGuid();
            Assert.True(registry.Add(identifier));
            Assert.Equal([identifier], new OrielDataStoreRegistry(path).Identifiers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_WithoutPath_WorksInMemory()
    {
        var registry = new OrielDataStoreRegistry(filePath: null);
        var identifier = Guid.NewGuid();

        Assert.True(registry.Add(identifier));
        Assert.Equal([identifier], registry.Identifiers);
        Assert.Null(registry.FilePath);
    }

    // ---------------------------------------------------------------- 门禁与命令

    private static readonly string[] ReadOnlyCommands =
    [
        "app.getBundleType",
        "app.defaultWindowIcon",
        "app.fetchDataStoreIdentifiers",
    ];

    private static readonly string[] StatefulCommands =
    [
        "app.setDockVisibility",
        "app.removeDataStore",
    ];

    [Fact]
    public void OnlyReadOnlyCommands_AreAlwaysAllowed()
    {
        foreach (string name in ReadOnlyCommands)
        {
            Assert.True(
                OrielCapabilityRules.IsAlwaysAllowedAppCommand(name),
                $"{name} 是只读元信息，应当始终放行");
        }

        foreach (string name in StatefulCommands)
        {
            Assert.False(
                OrielCapabilityRules.IsAlwaysAllowedAppCommand(name),
                $"{name} 改应用状态或删数据，必须由应用显式 Allow");
        }
    }

    [Fact]
    public void ExactCommandNames_AreAuthorized()
    {
        // 与 demo 同一写法：两条精确命令（不是 app.* 前缀）
        var options = new OrielCapabilityOptions();
        options.Allow("window.setShadow", "app.setDockVisibility", "app.removeDataStore");

        var guard = new OrielIpcGuard(options, isDebugBuild: false, "app.oriel");

        Assert.True(guard.TryAuthorize("app.setDockVisibility", out string? reason), reason);
        Assert.True(guard.TryAuthorize("app.removeDataStore", out reason), reason);
        Assert.True(guard.TryAuthorize("window.setShadow", out reason), reason);
    }

    private static OrielCommandDispatcher CreateDispatcher(bool allowState)
        => new(
            [],
            TestJsonContext.Default,
            new OrielIpcGuard(
                allowState ? new OrielCapabilityOptions().Allow("app.*") : new OrielCapabilityOptions().Allow("todo.*"),
                isDebugBuild: false,
                "app.oriel"));

    [Theory]
    [InlineData("app.setDockVisibility")]
    [InlineData("app.removeDataStore")]
    public async Task StatefulCommands_WithoutCapability_AreRejected(string command)
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: false),
            command,
            new { value = command == "app.setDockVisibility" ? (object)true : Convert.ToBase64String(new byte[16]) },
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("not_allowed", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetBundleType_ReturnsTheAppValue()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: false), "app.getBundleType", null,
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());

        // 测试进程不是打包产物：值必须是 null（"不编值"）
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task DefaultWindowIcon_WithoutFileIsNull()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: false), "app.defaultWindowIcon", null,
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task FetchDataStoreIdentifiers_ReachesImplementation()
    {
        // 只读命令无需 Allow，但应用没 Run() —— 应当停在"应用尚未运行"，而不是 not_allowed
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: false), "app.fetchDataStoreIdentifiers", null,
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Run", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SetDockVisibility_RequiresBoolean()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: true), "app.setDockVisibility", new { value = "false" },
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("missing_argument", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoveDataStore_RejectsNonUuidPayload()
    {
        // 3 字节：长度不对是调用方错误，不能当"删了个不存在的"静默成功
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: true), "app.removeDataStore",
            new { value = Convert.ToBase64String(new byte[3]) },
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoveDataStore_WithValidUuid_ReachesImplementation()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: true), "app.removeDataStore",
            new { value = Convert.ToBase64String(new byte[16]) },
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Run", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SetDockVisibility_WithCapability_ReachesImplementation()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowState: true), "app.setDockVisibility", new { value = false },
            window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Run", reply.RootElement.GetProperty("error").GetString());
    }
}
