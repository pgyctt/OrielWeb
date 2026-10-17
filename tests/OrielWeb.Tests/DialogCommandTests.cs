using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 对话框命令族（F3）：<c>dialog.open</c> / <c>dialog.save</c> / <c>dialog.message</c>。
/// </summary>
/// <remarks>
/// <para>
/// 用记录式窗口（<see cref="BuiltInWindowApiCommandTests.RecordingWindow"/>）而不是真弹对话框——
/// 单测里弹模态窗口会把 CI 挂住。所以这里验的是**映射**：Tauri 形状的 options 有没有
/// 正确落到原生层的那几个字段上。
/// </para>
/// <para>
/// 最值得钉的是三处"明确拒绝"：<c>canCreateDirectories</c> / <c>recursive</c>（没有对应能力）、
/// <c>directory + multiple</c>（目录选择器只支持单选）。静默忽略它们的后果都是
/// "调用方以为某个选项生效了"。
/// </para>
/// </remarks>
public sealed class DialogCommandTests
{
    private static readonly string[] AllCommands =
    [
        "dialog.open", "dialog.save", "dialog.message",
    ];

    [Theory]
    [InlineData("dialog.open")]
    [InlineData("dialog.save")]
    [InlineData("dialog.message")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInDialogCommands.TryGet(name, out _), $"未注册：{name}");

    [Fact]
    public void UnknownCommandIsNotHandled()
        => Assert.False(OrielBuiltInDialogCommands.TryGet("dialog.nonexistent", out _));

    [Fact]
    public void DialogCommandsAreNotAlwaysAllowed()
    {
        foreach (string command in AllCommands)
        {
            Assert.False(OrielCapabilityRules.IsAlwaysAllowedPathCommand(command), command);
            Assert.False(OrielCapabilityRules.IsAlwaysAllowedAppCommand(command), command);
        }
    }

    // ---- open ----

    [Fact]
    public void OpenMapsMultipleAndFiltersToTheNativeOptions()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.open", new
        {
            options = new
            {
                title = "选择图片",
                multiple = true,
                defaultPath = "/tmp",
                filters = new[] { new { name = "图片", extensions = new[] { "png", "jpg" } } },
            },
        });

        string call = Assert.Single(window.Calls);
        Assert.StartsWith("showOpenFileDialogOptions:选择图片|True|", call, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenReturnsAnArrayEvenForSingleSelection()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        // 记录式窗口回空数组（等于"用户取消了"）——命令层统一给数组，
        // 单选/多选的摊平在页面侧做（那样命令层的返回值形状稳定）
        object? result = Invoke<object?>(window, "dialog.open", new { options = new { } });

        Assert.IsType<string[]>(result);
    }

    [Fact]
    public void OpenWithDirectoryUsesTheFolderDialog()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.open", new { options = new { directory = true, title = "选目录" } });

        Assert.Contains("showFolderDialog:选目录|", window.Calls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("showOpenFileDialogOptions", window.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRejectsDirectoryPlusMultiple()
    {
        // 目录选择器只支持单选（与 Tauri 一致）。静默只取第一个会让调用方
        // 以为"用户选了多个目录"——而它只拿到一个。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<object?>(window, "dialog.open", new { options = new { directory = true, multiple = true } }));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("multiple", ex.Message, StringComparison.Ordinal);

        // 报错之前不该已经弹过对话框
        Assert.Empty(window.Calls);
    }

    [Theory]
    [InlineData("canCreateDirectories")]
    [InlineData("recursive")]
    public void OpenRejectsUnimplementedOptions(string option)
    {
        // 静默忽略一个布尔选项最危险的地方：调用方以为它生效了。
        // 比如 canCreateDirectories: false 被忽略后用户仍能新建目录，
        // 而调用方已经不再校验那条路径是否存在了。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();
        object args = option == "canCreateDirectories"
            ? new { options = new { canCreateDirectories = false } }
            : new { options = new { recursive = true } };

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<object?>(window, "dialog.open", args));

        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
        Assert.Contains(option, ex.Message, StringComparison.Ordinal);
        Assert.Empty(window.Calls);
    }

    [Fact]
    public void OpenWithoutOptionsWorks()
    {
        // Tauri 的 open() 默认参数就是空对象，所以不给 options 应当等价
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.open", new { });

        Assert.Single(window.Calls);
    }

    // ---- save ----

    [Fact]
    public void SaveSplitsDefaultPathIntoDirectoryAndFileName()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.save", new
        {
            options = new { title = "另存为", defaultPath = Path.Combine("/tmp", "报告.txt") },
        });

        string call = Assert.Single(window.Calls);

        // 本库的原生层把"初始目录"与"预填文件名"分成两个字段，
        // 而 Tauri 的 defaultPath 是一个路径——所以要拆
        Assert.Contains("showSaveFileDialog:另存为|", call, StringComparison.Ordinal);
        Assert.Contains("报告.txt", call, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAcceptsAnExistingDirectoryAsDefaultPath()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();
        string directory = Path.GetTempPath();

        Invoke<object?>(window, "dialog.save", new { options = new { defaultPath = directory } });

        // 已存在的目录当目录用，不再拆文件名
        string call = Assert.Single(window.Calls);
        Assert.EndsWith(directory + "|", call, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveRejectsCanCreateDirectories()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<object?>(window, "dialog.save", new { options = new { canCreateDirectories = true } }));

        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
    }

    // ---- message ----

    [Theory]
    [InlineData(MessageResult.Ok, "ok")]
    [InlineData(MessageResult.Cancel, "cancel")]
    [InlineData(MessageResult.Yes, "yes")]
    [InlineData(MessageResult.No, "no")]
    public void MessageReturnsTheSemanticButton(MessageResult native, string expected)
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow { NextMessageResult = native };

        object? result = Invoke<object?>(window, "dialog.message", new { message = "继续吗？" });

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MessageReturnsNullWhenClosedExternally()
    {
        // 被外部关掉（Esc / 关窗）时没有结果。给 null 而不是"当成取消"：
        // 调用方要能区分"用户点了否"与"对话框根本没出结果"。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow { NextMessageResult = null };

        Assert.Null(Invoke<object?>(window, "dialog.message", new { message = "x" }));
    }

    [Fact]
    public void MessageRequiresTheMessageArgument()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<object?>(window, "dialog.message", new { title = "没有正文" }));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    [Theory]
    [InlineData("Ok", MessageButtons.Ok)]
    [InlineData("OkCancel", MessageButtons.OkCancel)]
    [InlineData("YesNo", MessageButtons.YesNo)]
    [InlineData("YesNoCancel", MessageButtons.YesNoCancel)]
    public void MessageMapsStringButtonSets(string buttons, MessageButtons expected)
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.message", new { message = "x", buttons });

        Assert.Contains($"|{expected}|", window.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MessageMapsCustomButtonLabels()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.message", new
        {
            message = "x",
            buttons = new { yes = "好", no = "算了" },
        });

        // 自定义文案要原样到原生层（Linux/macOS 用它显示；Windows 会忽略，见那里的说明）
        Assert.Contains($"|{MessageButtons.YesNo}|好|算了", window.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MessageDistinguishesYesNoCancelFromYesNo()
    {
        // 两者只差一个 cancel 文案——区分错了会让"三按钮"变成"两按钮"，
        // 而调用方就再也区分不出"用户选了否"与"用户放弃了"
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.message", new
        {
            message = "x",
            buttons = new { yes = "是", no = "否", cancel = "取消" },
        });

        Assert.Contains($"|{MessageButtons.YesNoCancel}|", window.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MessageDefaultsToASingleOkButton()
    {
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Invoke<object?>(window, "dialog.message", new { message = "x" });

        Assert.Contains($"|{MessageButtons.Ok}|确定|", window.Calls[0], StringComparison.Ordinal);
    }

    // ---- 与既有 pickFile 的关系 ----

    [Fact]
    public void ExistingPickFileSignatureIsUntouched()
    {
        // **这条是本批那个决定的核心断言**：win.pickFile / window.pickFile 的既有契约
        // （只收 title/filter、返回单个路径）必须原样可用，本批不能改动它。
        // 若哪天有人把 dialog.* 的 options 直接塞进那条路径，这条会红。
        var window = new BuiltInWindowApiCommandTests.RecordingWindow();

        Assert.True(OrielBuiltInWindowCommands.TryGet("win.pickFile", out var handler));

        using var doc = JsonDocument.Parse("""{ "title": "选个文件" }""");
        handler(window, doc.RootElement);

        // 走的是**宽松签名**那条路，不是 options 那条
        Assert.Contains("showOpenFileDialog:选个文件|", window.Calls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("showOpenFileDialogOptions", window.Calls[0], StringComparison.Ordinal);
    }

    // ---- 夹具 ----

    private static T Invoke<T>(BuiltInWindowApiCommandTests.RecordingWindow window, string command, object args)
    {
        Assert.True(OrielBuiltInDialogCommands.TryGet(command, out var handler), $"未注册：{command}");

        using var doc = JsonDocument.Parse(TestArgs.Serialize(args));
        return (T)handler(window, command, doc.RootElement)!;
    }
}
