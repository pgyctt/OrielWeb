using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows;
using OrielWeb.Platform.Windows.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// OLE 拖放目标的结构布局与 vtable 槽位。
/// </summary>
/// <remarks>
/// 这两样都是"错了不报错、只表现为诡异行为"的东西：结构字段错位会让 <c>GetData</c> 返回
/// <c>DV_E_FORMATETC</c>（看起来像"拖的东西没有文件"），vtable 槽位写反会让 enter/drop 互换。
/// 真正的拖动流程没法在无头环境里跑（造不出 OLE 会话），所以这里只钉住能静态验证的那部分。
/// </remarks>
public sealed class Win32DropTargetTests
{
    [Fact]
    public void PointL_IsTwoInts()
    {
        Assert.Equal(8, Marshal.SizeOf<POINTL>());
        Assert.Equal(0, Marshal.OffsetOf<POINTL>(nameof(POINTL.x)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<POINTL>(nameof(POINTL.y)).ToInt32());
    }

    [Fact]
    public void FormatEtc_MatchesNativeLayout()
    {
        if (IntPtr.Size != 8)
        {
            return; // 下面的偏移是 x64 的；本库只发布 64 位
        }

        // 32 = 2(cfFormat) + 6(填充) + 8(ptd) + 4(dwAspect) + 4(lindex) + 4(tymed) + 4(尾部填充)
        Assert.Equal(32, Marshal.SizeOf<FORMATETC>());
        Assert.Equal(0, Marshal.OffsetOf<FORMATETC>(nameof(FORMATETC.cfFormat)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<FORMATETC>(nameof(FORMATETC.ptd)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FORMATETC>(nameof(FORMATETC.dwAspect)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<FORMATETC>(nameof(FORMATETC.lindex)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<FORMATETC>(nameof(FORMATETC.tymed)).ToInt32());
    }

    [Fact]
    public void StgMedium_MatchesNativeLayout()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        // 24 = 4(tymed) + 4(填充) + 8(union 里的 hGlobal) + 8(pUnkForRelease)
        Assert.Equal(24, Marshal.SizeOf<STGMEDIUM>());
        Assert.Equal(0, Marshal.OffsetOf<STGMEDIUM>(nameof(STGMEDIUM.tymed)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<STGMEDIUM>(nameof(STGMEDIUM.hGlobal)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<STGMEDIUM>(nameof(STGMEDIUM.pUnkForRelease)).ToInt32());
    }

    [Fact]
    public void HdropReader_NullIsEmpty()
        => Assert.Empty(Win32FileDropReader.Read(0));

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // 2026-10-25 评审 P2：二次 Dispose 曾会再递减一次引用计数。这里没有 OLE 会话
        // （不 Register），能钉住的是"幂等契约的表面"——两次调用都不抛、不崩；
        // 注册路径上那次多余的递减会提前 FreeHandle、与在途 COM Release 构成
        // use-after-free，那部分由 conformance 的拖放自检在真机上守。
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = new Win32DropTarget(0, static _ => { });
        target.Dispose();
        target.Dispose();
    }

    [Fact]
    public void Vtbl_HasSevenDistinctNonZeroSlots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 取函数指针本身跨平台可用，但这张表只有 Windows 会真的被 OLE 调用
        }

        ReadOnlySpan<nint> slots = Win32DropTarget.VtblSlotsForTest;

        Assert.Equal(7, slots.Length);
        var seen = new HashSet<nint>();
        foreach (nint slot in slots)
        {
            Assert.NotEqual(0, slot);

            // 槽位重复意味着某个接口方法被同一个实现填了两次——典型是复制粘贴后忘了改
            Assert.True(seen.Add(slot), "vtable 里出现了重复的函数指针，说明有槽位写错。");
        }
    }
}
