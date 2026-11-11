using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.WebView2.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 自定义 scheme 登记的 COM 回读契约（2026-11-11 评审 L4 的回归闸）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷</b>：<c>GetAllowedOrigins</c> 此前是个 stub——永远回 <c>count = 0</c>，
/// 完全不看 <c>_allowedOrigins</c>。而 <c>SetAllowedOrigins</c> 已经把值存下来了。
/// 之所以一直没暴露：origins 目前刻意留空（<c>Win32AssetScheme</c> 里页面本身就在该 scheme 下，
/// 同源请求不需要 CORS 条目），于是"回读 0"碰巧是对的。
/// </para>
/// <para>
/// 这类"实现了但没生效"的属性最麻烦的地方是它**没有任何报错**——将来谁要用它，
/// 症状是"设了跨源放行却依然被 CORS 拦"，而排查方向会被引到别处。
/// </para>
/// <para>
/// 测试同时验证**内存所有权契约**：按 WebView2 文档，返回的数组与每个字符串都由
/// <c>CoTaskMemAlloc</c> 分配，调用方负责释放。这里照着调用方的做法真的释放一遍——
/// 既证明"分配器用对了"（若不是 CoTaskMemAlloc，这次释放会崩或留下泄漏），
/// 也证明返回的确实是可直接解引用的原生内存，而不是托管引用。
/// </para>
/// </remarks>
public sealed class CustomSchemeRegistrationTests
{
    [Fact]
    public void GetAllowedOrigins_ReturnsWhatWasSet()
    {
        var registration = new CoreWebView2CustomSchemeRegistration("oriel");
        registration.SetAllowedOrigins("https://example.test/", "http://localhost:5173/");

        int hr = registration.GetAllowedOrigins(out int count, out nint pointer);

        Assert.Equal(0, hr); // S_OK
        Assert.Equal(2, count);
        Assert.NotEqual(0, pointer);

        try
        {
            // 数组元素是 LPWSTR 指针：按 nint 逐个读出，再各自解引用成字符串。
            for (int i = 0; i < count; i++)
            {
                nint item = Marshal.ReadIntPtr(pointer, i * nint.Size);
                string? text = Marshal.PtrToStringUni(item);

                Assert.Equal(i == 0 ? "https://example.test/" : "http://localhost:5173/", text);
            }
        }
        finally
        {
            // 按调用方的义务释放：先逐个释放字符串，再释放数组本身。
            for (int i = 0; i < count; i++)
            {
                Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(pointer, i * nint.Size));
            }

            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [Fact]
    public void GetAllowedOrigins_EmptyList_YieldsNullPointer()
    {
        // 没有放行任何跨源来源时的正确表达：count = 0 且指针为 0。
        // 这样原生侧不必去释放一个空数组（也不必判空指针是不是"空列表"）。
        var registration = new CoreWebView2CustomSchemeRegistration("oriel");

        int hr = registration.GetAllowedOrigins(out int count, out nint pointer);

        Assert.Equal(0, hr);
        Assert.Equal(0, count);
        Assert.Equal(0, pointer);
    }

    [Fact]
    public void SetAllowedOrigins_EmptyOrNull_ClearsInsteadOfLeavingStaleValues()
    {
        // 清空语义：设过之后再设空，回读必须是 0——而不是留着上一次的值。
        var registration = new CoreWebView2CustomSchemeRegistration("oriel");
        registration.SetAllowedOrigins("https://example.test/");

        registration.SetAllowedOrigins();
        registration.GetAllowedOrigins(out int afterEmpty, out nint emptyPointer);
        Assert.Equal(0, afterEmpty);
        Assert.Equal(0, emptyPointer);

        registration.SetAllowedOrigins("https://example.test/");
        registration.SetAllowedOrigins(null!);
        registration.GetAllowedOrigins(out int afterNull, out nint nullPointer);
        Assert.Equal(0, afterNull);
        Assert.Equal(0, nullPointer);
    }

    [Fact]
    public void SchemeNameAndAuthorityFlags_AreReadableByNativeSide()
    {
        // 同一族里的另外两条回读此前就是对的，这里一并钉住：
        // HasAuthorityComponent 少了它，Chromium 会把 oriel://app.oriel/x 解析成"无主机的自定义 scheme"，
        // 页面来源变成 oriel://，可信前缀配不上——表现是"窗口打开了、页面也在，但什么命令都没反应"。
        var registration = new CoreWebView2CustomSchemeRegistration("oriel");

        Assert.Equal("oriel", registration.SchemeName);

        registration.TreatAsSecure = true;
        registration.HasAuthorityComponent = true;

        Assert.Equal(0, registration.get_SchemeName(out nint schemeName));
        Assert.Equal("oriel", Marshal.PtrToStringUni(schemeName));

        // 这两个是入参回读（托管侧 -> 原生）：BOOL 用 int 0/1 表达，不是 C# bool。
        Assert.Equal(0, registration.get_TreatAsSecure(out int secure));
        Assert.Equal(1, secure);

        Assert.Equal(0, registration.get_HasAuthorityComponent(out int authority));
        Assert.Equal(1, authority);
    }
}
