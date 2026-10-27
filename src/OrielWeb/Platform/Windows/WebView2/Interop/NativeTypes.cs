using System.Runtime.InteropServices;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>Win32 WV2RECT。与 WebView2 的 <c>WV2RECT</c> 参数按值传递，布局必须逐字段一致。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WV2RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;

    public WV2RECT(int left, int top, int right, int bottom)
    {
        this.left = left;
        this.top = top;
        this.right = right;
        this.bottom = bottom;
    }

    public readonly int Width => right - left;

    public readonly int Height => bottom - top;
}

/// <summary>Win32 WV2POINT。鼠标消息的客户区坐标用它承载。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WV2POINT
{
    public int x;
    public int y;

    public WV2POINT(int x, int y)
    {
        this.x = x;
        this.y = y;
    }
}

/// <summary>WebView2 的 <c>COREWEBVIEW2_COLOR</c>：四个分量按 A/R/G/B 顺序，每分量一个字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct COREWEBVIEW2_COLOR
{
    public byte A;
    public byte R;
    public byte G;
    public byte B;
}

/// <summary>事件订阅句柄。add_ 方法返回，remove_ 方法消费。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EventRegistrationToken
{
    public long value;
}

/// <summary>
/// 指向 UTF-16 字符串的原生指针，语义等同于 DirectN 的 <c>PWSTR</c>。
/// </summary>
/// <remarks>
/// WebView2 的所有字符串 out 参数都由**调用方**用 <c>CoTaskMemFree</c> 释放——这是 COM 的
/// 分配器契约，不是 .NET 的 GC 契约。所以这里不把它包装成 <c>string</c> 交给封送器，
/// 而是显式持有指针并在 <see cref="Dispose"/> 里释放：封送器对 <c>out string</c> 的释放策略
/// 由源生成器决定，而这里要的是与 WebView2 文档逐字对应的行为。
/// </remarks>
internal readonly struct PWSTR : IDisposable
{
    public readonly nint Value;

    public PWSTR(nint value) => Value = value;

    public bool IsNull => Value == 0;

    /// <summary>读出字符串并释放原生内存。</summary>
    public string ToStringAndDispose()
    {
        if (Value == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(Value) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(Value);
        }
    }

    public override string ToString() => Value == 0 ? string.Empty : Marshal.PtrToStringUni(Value) ?? string.Empty;

    public void Dispose()
    {
        if (Value != 0)
        {
            Marshal.FreeCoTaskMem(Value);
        }
    }
}

/// <summary>HRESULT 的判定辅助。COM 约定：负值失败，非负成功。</summary>
internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    public static bool IsError(int hr) => hr < 0;

    public static bool IsSuccess(int hr) => hr >= 0;

    /// <summary>把失败的 HRESULT 抛成 <see cref="COMException"/>；成功时原样返回。</summary>
    public static int ThrowOnError(this int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return hr;
    }
}
