using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// GTK/GDK 原生结构的托管映射布局。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这类测试值得存在</b>：结构偏移写错时，C# 侧编译完全通过、单测也能过，
/// 只有到 Linux 真机上才表现为"上报的尺寸和位置全都不对"。而那种症状看起来像是
/// 逻辑写错了，几乎不会有人怀疑到"字段偏移差 4 字节"上。
/// </para>
/// <para>
/// 这里断言的是**字段相对偏移**，与指针宽度无关的判据（padding 的存在）也一并钉住。
/// 一旦有人给字段换了类型（例如把 <c>byte SendEvent</c> 改成 <c>bool</c>），
/// 后续字段会整体偏移 3 字节，这些断言会立刻变红。
/// </para>
/// </remarks>
public sealed class GdkStructLayoutTests
{
    [Fact]
    public void GdkEventConfigure_FieldOrderMatchesGtk3Header()
    {
        // GTK3 gdkevents.h：
        //   struct _GdkEventConfigure {
        //       GdkEventType type;  GdkWindow *window;  gint8 send_event;
        //       gint x, y, width, height;
        //   };
        // Sequential 布局下托管字段顺序必须与头文件一致——顺序错了偏移就全错。
        Assert.Equal(0, (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Type)));
        Assert.Equal(
            (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Window)),
            (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Type)) + 4 + PointerPaddingAfterType());
    }

    [Fact]
    public void GdkEventConfigure_GeometryFieldsAreContiguous()
    {
        // x / y / width / height 是四个连续的 gint（4 字节整数、无 padding）。
        // 这正是 LinuxWindowHost.OnConfigureEvent 依赖的：读结构体字段而不是手算偏移。
        int x = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.X));
        int y = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Y));
        int width = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Width));
        int height = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.Height));

        Assert.Equal(x + 4, y);
        Assert.Equal(y + 4, width);
        Assert.Equal(width + 4, height);
    }

    [Fact]
    public void GdkEventConfigure_SendEventIsOneByte()
    {
        // 必须用 byte 而不是 bool：C 的 gint8 是 1 字节整数，而 .NET 的 bool 在默认
        // marshalling 下是 4 字节 Win32 BOOL。改成 bool 会让后续字段整体偏移 3 字节——
        // 编译照样通过，只有真机上"尺寸和位置全都不对"。
        Assert.Equal(1, Marshal.SizeOf<byte>());
        Assert.Equal(1, (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.X))
                      - (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.SendEvent)) - 3);
    }

    [Fact]
    public void GdkEventConfigure_GeometryFieldsStartAfterSendEventAndPadding()
    {
        // send_event（1 字节）之后有 3 字节 padding 才到 x——这是 C 的 int 对齐要求。
        int sendEvent = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.SendEvent));
        int x = (int)Marshal.OffsetOf<GdkEventConfigure>(nameof(GdkEventConfigure.X));

        Assert.Equal(sendEvent + 4, x);
    }

    [Fact]
    public void GdkRGBA_IsFourContiguousDoubles()
    {
        // webkit_web_view_set_background_color 的参数。与窗口背景色那条路径相关：
        // 顺序错了表现为"R 与 B 互换"，在深色底上不容易一眼看出来。
        Assert.Equal(32, Marshal.SizeOf<GdkRGBA>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GdkRGBA>(nameof(GdkRGBA.Red)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GdkRGBA>(nameof(GdkRGBA.Green)));
        Assert.Equal(16, (int)Marshal.OffsetOf<GdkRGBA>(nameof(GdkRGBA.Blue)));
        Assert.Equal(24, (int)Marshal.OffsetOf<GdkRGBA>(nameof(GdkRGBA.Alpha)));
    }

    /// <summary>
    /// <c>GdkEventType</c>（int32）之后、指针之前的 padding。
    /// </summary>
    /// <remarks>
    /// 64 位下指针要 8 字节对齐，所以 int32 之后有 4 字节 padding；
    /// 32 位下指针 4 字节对齐，没有 padding。
    /// </remarks>
    private static int PointerPaddingAfterType() => IntPtr.Size == 8 ? 4 : 0;
}
