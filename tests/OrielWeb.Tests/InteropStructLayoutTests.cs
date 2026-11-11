using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.WebView2.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// Windows COM 互操作结构的托管映射布局。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="GdkStructLayoutTests"/> 同一理由：偏移写错时 C# 编译通过、其它单测也能过，
/// 只有到真机上才表现为"字段值莫名其妙"。而那种症状几乎不会让人怀疑到"偏移差 8 字节"上。
/// </para>
/// <para>
/// 这里断言的是**逐字段相对偏移**：一旦有人给某个字段换了类型（例如把 16 字节的
/// <see cref="Guid"/> 换回 <c>uint</c>），后续字段会整体错位，这些断言会立刻变红。
/// </para>
/// </remarks>
public sealed class InteropStructLayoutTests
{
    /// <summary>本库只发布 64 位 RID，所以布局断言直接按 64 位写、不做条件分支。</summary>
    private static void Require64Bit() => Assert.True(IntPtr.Size == 8, "本库只发布 64 位平台（win-x64 / win-arm64）");

    [Fact]
    public void Statstg_TotalSizeMatchesWin32On64Bit()
    {
        Require64Bit();

        // 8(pwcsName) + 4(type) + 4(padding) + 8(cbSize) + 8*3(FILETIME) + 4*2(grfMode/grfLocks)
        // + 16(clsid) + 4*2(grfStateBits/reserved) = 80
        Assert.Equal(80, Marshal.SizeOf<STATSTG>());
    }

    [Fact]
    public void Statstg_FieldOffsetsMatchTheNativeLayout()
    {
        Require64Bit();

        Assert.Equal(0, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.pwcsName)));
        Assert.Equal(8, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.type)));

        // type 之后有 4 字节隐式填充：cbSize 是 8 字节对齐的 ULARGE_INTEGER
        Assert.Equal(16, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.cbSize)));

        Assert.Equal(24, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.mtime)));
        Assert.Equal(32, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.ctime)));
        Assert.Equal(40, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.atime)));
        Assert.Equal(48, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.grfMode)));
        Assert.Equal(52, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.grfLocksSupported)));
        Assert.Equal(56, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.clsid)));
        Assert.Equal(72, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.grfStateBits)));
        Assert.Equal(76, (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.reserved)));
    }

    [Fact]
    public void Statstg_ClsidIsSixteenBytesWide()
    {
        Require64Bit();

        // 2026-11-11 评审 M1：CLSID 是 16 字节的 GUID，不是 4 字节的 uint。
        // 按 uint 布局时整个结构是 72B，clsid 之后的所有字段错位——
        // IStream.Stat 的封送会按错误偏移写入（调用点少不代表不是缺陷）。
        int clsid = (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.clsid));
        int grfStateBits = (int)Marshal.OffsetOf<STATSTG>(nameof(STATSTG.grfStateBits));

        Assert.Equal(16, Marshal.SizeOf<Guid>());
        Assert.Equal(clsid + 16, grfStateBits);
    }
}
