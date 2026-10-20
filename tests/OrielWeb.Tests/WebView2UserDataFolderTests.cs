using System.Runtime.InteropServices;
using OrielWeb;
using OrielWeb.Platform.Windows;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// WebView2 用户数据目录的约定与「目录被占用」的提示文案。
/// </summary>
/// <remarks>
/// 这两条都是纯逻辑（拼路径、按 HRESULT 选文案），不需要真的把 WebView2 装起来，
/// 所以在任意平台都能断言。真机失败路径依赖 Windows + 已安装的运行时，仍属人工/自检项。
/// </remarks>
public sealed class WebView2UserDataFolderTests
{
    private static string LocalAppData => Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void DefaultFolder_IsComposedFromAppIdentifier()
    {
        string path = WindowsPlatformBackend.DefaultUserDataFolderFor("com.example.app");

        // 逐段断言而不是比对整串：另两个平台的分隔符不同，整串比对会把这条测试变成 Windows 专属。
        Assert.Equal(Path.Combine(LocalAppData, "com.example.app", "WebView2"), path);
    }

    [Fact]
    public void DefaultFolder_DiffersPerApp()
    {
        // 这就是修复的那条缺陷：此前默认目录写死成 %LOCALAPPDATA%\OrielWeb\WebView2，
        // 两个基于本库的应用同时打开必然有一个以 0x800700AA 起不来。
        string first = WindowsPlatformBackend.DefaultUserDataFolderFor("com.example.one");
        string second = WindowsPlatformBackend.DefaultUserDataFolderFor("com.example.two");

        Assert.False(
            string.Equals(first, second, StringComparison.OrdinalIgnoreCase),
            $"两个应用不能共用同一个用户数据目录：{first}");
    }

    [Fact]
    public void DefaultFolder_IsNotTheLibraryWideFolder()
    {
        string path = WindowsPlatformBackend.DefaultUserDataFolderFor("my-app");
        string? parent = Path.GetFileName(Path.GetDirectoryName(path));

        // 目录名的上一级必须是**应用标识符**，不能是库名——否则"按应用隔离"这件事只是说法。
        Assert.Equal("my-app", parent);
        Assert.Equal("WebView2", Path.GetFileName(path));
    }

    [Fact]
    public void BusyMessage_NamesTheFolderAndWhatToDo()
    {
        var ex = new COMException("请求的资源在使用中。", unchecked((int)0x800700AA));

        string message = Win32WindowHost.DescribeWebView2Failure(ex, @"C:\Users\me\AppData\Local\my-app\WebView2");

        Assert.Contains(@"C:\Users\me\AppData\Local\my-app\WebView2", message, StringComparison.Ordinal);
        Assert.Contains("占用", message, StringComparison.Ordinal);
        // 三条建议里最容易照做的那条必须在：提示的价值在于"下一步做什么"。
        Assert.Contains("另一个实例", message, StringComparison.Ordinal);
        Assert.Contains("SingleInstance()", message, StringComparison.Ordinal);
        Assert.Contains("UseUserDataFolder()", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharingViolation_IsTreatedTheSameAsBusy()
    {
        var ex = new COMException("另一个程序正在使用此文件，进程无法访问。", unchecked((int)0x80070020));

        string message = Win32WindowHost.DescribeWebView2Failure(ex, @"C:\data\WebView2");

        Assert.Contains(@"C:\data\WebView2", message, StringComparison.Ordinal);
        Assert.Contains("占用", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherFailures_ArePassedThroughUnchanged()
    {
        // 不要给所有 HRESULT 套一层"目录被占用"：那会让真正的失败原因（缺 DLL、
        // 版本不兼容、COM 注册损坏）被一句错误的解释盖掉。
        var ex = new InvalidOperationException("创建 WebView2 环境失败（返回 null）。");

        string message = Win32WindowHost.DescribeWebView2Failure(ex, @"C:\data\WebView2");

        Assert.Equal("初始化 WebView2 失败：创建 WebView2 环境失败（返回 null）。", message);
        Assert.DoesNotContain("占用", message, StringComparison.Ordinal);
    }
}
