using System.Reflection;
using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// WebKit soname 回退的**解析规则**测试。
/// </summary>
/// <remarks>
/// 这里能验的只有"要不要接管"这一半：真正加载 4.0 需要那台机器的库，只能在 Linux 上跑。
/// 但"不接管其它库"这一条恰恰是最容易写出内伤的——一旦对所有库名返回非 0 的句柄，
/// GTK / GDK / GLib 会走错加载路径，而症状是启动时莫名其妙的 DllNotFound。
/// </remarks>
public sealed class LinuxLibraryResolverTests
{
    [Theory]
    [InlineData("libgtk-3.so.0")]
    [InlineData("libgdk-3.so.0")]
    [InlineData("libglib-2.0.so.0")]
    [InlineData("libcairo.so.2")]
    [InlineData("whatever-unknown.so")]
    public void NonWebKitLibraries_AreNotIntercepted(string name)
    {
        // 返回 0 = "不接管，走默认解析"
        Assert.Equal(
            0,
            LinuxLibraryResolver.Resolve(name, typeof(LinuxLibraryResolver).Assembly, null));
    }

    [Fact]
    public void Register_IsIdempotentAndDoesNotThrowOffLinux()
    {
        // 非 Linux 上应当直接返回（Windows 的 CI 就是这种情况）；
        // 在 Linux 上它会尝试 SetDllImportResolver，也只能设一次——两次调用都不得抛。
        LinuxLibraryResolver.Register();
        LinuxLibraryResolver.Register();
    }
}
