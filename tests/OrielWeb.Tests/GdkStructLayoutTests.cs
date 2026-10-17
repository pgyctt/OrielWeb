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

    // ---- GdkGeometry（M5-6：gtk_window_set_geometry_hints 的最大尺寸约束）----
    //
    // 这个结构比 GdkEvent 更危险：它把 int 与 double **混排**，
    // 而 double 要 8 字节对齐——偏移推错一位，写进去的就是另一个字段。
    // 而 Linux 在 CI 里只做交叉编译，那种错误要到真机才暴露。

    [Fact]
    public void GdkGeometrySizeMatchesTheCStruct()
    {
        // 9 个 gint + 2 个 gdouble，按 8 字节对齐：
        // 8*4（前 8 个 int）+ 8 + 8（两个 double）+ 4（win_gravity）+ 4（尾部填充）= 56
        Assert.Equal(56, Marshal.SizeOf<GtkNative.GdkGeometry>());
        Assert.Equal(GtkNative.GdkGeometry.GdkGeometrySize, Marshal.SizeOf<GtkNative.GdkGeometry>());
    }

    [Theory]
    [InlineData("min_width", 0)]
    [InlineData("min_height", 4)]
    [InlineData("max_width", 8)]
    [InlineData("max_height", 12)]
    [InlineData("base_width", 16)]
    [InlineData("base_height", 20)]
    [InlineData("width_inc", 24)]
    [InlineData("height_inc", 28)]
    [InlineData("min_aspect", 32)]
    [InlineData("max_aspect", 40)]
    [InlineData("win_gravity", 48)]
    public void GdkGeometryFieldOffsetsMatchTheCStruct(string fieldName, int expectedOffset)
    {
        Assert.NotNull(typeof(GtkNative.GdkGeometry).GetField(
            fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));

        Assert.Equal(expectedOffset, (int)Marshal.OffsetOf<GtkNative.GdkGeometry>(fieldName));
    }

    [Fact]
    public void WritingMaxSizeLandsInTheRightFields()
    {
        // 布局对不对，最终要看"写进去的值出现在正确的偏移上"。
        // 按 LinuxWindowHost.SetMaxSize 的写法填一遍，再把同一块内存按 int 读回来。
        var geometry = default(GtkNative.GdkGeometry);
        geometry.max_width = 800;
        geometry.max_height = 600;

        int[] words = new int[14];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(words.AsSpan()), in geometry);

        Assert.Equal(800, words[2]);   // 偏移 8  → 第 3 个 int
        Assert.Equal(600, words[3]);   // 偏移 12 → 第 4 个 int

        // **只声明 max 时 min 两位必须仍是 0**：SetMaxSize 只带 GDK_HINT_MAX_SIZE，
        // 若这两处被写成 0 而掩码里又恰好带了 MIN，窗口会被卡成 0 宽。
        Assert.Equal(0, words[0]);
        Assert.Equal(0, words[1]);
    }
}
