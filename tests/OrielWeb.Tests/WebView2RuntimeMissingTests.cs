using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 「WebView2 运行时不可用」公开 API 的接线与默认约定。
/// 触发路径本身依赖 Windows + 已安装的 WebView2，只能在真机验证；
/// 这里覆盖的是可以在任意平台断言的契约（注册、透传、默认值、下载地址）。
/// </summary>
public sealed class WebView2RuntimeMissingTests
{
    [Fact]
    public void OnWebView2RuntimeMissing_RegistersHandlerAndReturnsSameBuilder()
    {
        var builder = Oriel.CreateBuilder();
        Action<OrielWebView2RuntimeMissingEventArgs> handler = _ => { };

        var returned = builder.OnWebView2RuntimeMissing(handler);

        Assert.Same(builder, returned);
        Assert.Same(handler, builder.WebView2RuntimeMissingHandler);
    }

    [Fact]
    public void WithoutRegistration_HandlerIsNull()
        => Assert.Null(Oriel.CreateBuilder().WebView2RuntimeMissingHandler);

    [Fact]
    public void NullHandler_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => Oriel.CreateBuilder().OnWebView2RuntimeMissing(null!));

    [Fact]
    public void HandlerIsVisibleToTheApp()
    {
        var builder = Oriel.CreateBuilder();
        Action<OrielWebView2RuntimeMissingEventArgs> handler = _ => { };
        builder.OnWebView2RuntimeMissing(handler);

        using var app = builder.Build();

        Assert.Same(handler, app.WebView2RuntimeMissingHandler);
    }

    [Fact]
    public void EventArgs_CarriesWindowAndFolder_AndDefaultsToClosingTheWindow()
    {
        var window = new WebviewWindow();

        var args = new OrielWebView2RuntimeMissingEventArgs(window, @"C:\fixed-version-runtime");

        Assert.Same(window, args.Window);
        Assert.Equal(@"C:\fixed-version-runtime", args.BrowserExecutableFolder);
        Assert.False(args.KeepWindowOpen);
    }

    [Fact]
    public void EventArgs_NullFolder_MeansSystemEvergreenRuntime()
        => Assert.Null(new OrielWebView2RuntimeMissingEventArgs(new WebviewWindow(), null).BrowserExecutableFolder);

    [Fact]
    public void KeepWindowOpen_IsSettable()
    {
        var args = new OrielWebView2RuntimeMissingEventArgs(new WebviewWindow(), null)
        {
            KeepWindowOpen = true,
        };

        Assert.True(args.KeepWindowOpen);
    }

    [Fact]
    public void DownloadUrl_IsAbsoluteHttps()
    {
        var url = new Uri(OrielWebView2RuntimeMissingEventArgs.DownloadUrl);

        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.False(string.IsNullOrWhiteSpace(url.Host));
    }
}
