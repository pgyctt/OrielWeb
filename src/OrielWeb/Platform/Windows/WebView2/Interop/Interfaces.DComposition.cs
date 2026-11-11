using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>
/// DirectComposition 设备。
/// </summary>
/// <remarks>
/// 只声明到 <c>CreateVisual</c>（前 5 个 slot）：COM 的早期绑定按序号寻址，
/// 只要不调用后面的方法，就不必把 20 多个变换/画刷工厂方法全抄一遍。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3")]
internal partial interface IDCompositionDevice
{
    [PreserveSig] int Commit();
    [PreserveSig] int WaitForCommitCompletion();
    [PreserveSig] int GetFrameStatistics(nint statistics);
    [PreserveSig] int CreateTargetForHwnd(nint hwnd, int topmost, out nint target);
    [PreserveSig] int CreateVisual(out nint visual);
}

/// <summary>DirectComposition 窗口目标：把视觉树挂到 HWND 上。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89")]
internal partial interface IDCompositionTarget
{
    [PreserveSig] int SetRoot(nint visual);
}

/// <summary>
/// DirectComposition 视觉。
/// </summary>
/// <remarks>
/// <para>
/// <b>vtable 顺序必须与 <c>dcomp.h</c> 逐槽一致</b>（2026-11-11 评审 L3）。COM 的早期绑定
/// 按**序号**寻址，声明顺序错一个槽，调用就会落到别的方法上——那不会报错，只会做错事。
/// </para>
/// <para>
/// <b>真实顺序</b>（继承 <c>IUnknown</c>，下列从序号 3 起）：
/// <c>SetOffsetX(IDCompositionAnimation*)</c>、<c>SetOffsetX(float)</c>、
/// <c>SetOffsetY(IDCompositionAnimation*)</c>、<c>SetOffsetY(float)</c>、
/// <c>SetTransform(IDCompositionTransform*)</c>、<c>SetTransform(D2D_MATRIX_3X2_F*)</c>、
/// <c>SetTransformParent</c>、<c>SetEffect</c>、<c>SetBitmapInterpolationMode</c>、
/// <c>SetBorderMode</c>、<c>SetClip(IDCompositionClip*)</c>、<c>SetClip(D2D_RECT_F*)</c>、
/// <c>SetContent</c>、<c>AddVisual</c>、<c>RemoveVisual</c>、<c>RemoveAllVisuals</c>、
/// <c>SetCompositeMode</c>。
/// </para>
/// <para>
/// <b>为什么只声明到 <c>SetOffsetY(float)</c></b>：本库从不调用这个接口的方法，它只是
/// "可以挂到目标上的对象"。而 C# 不允许两个同签名成员（<c>SetTransform</c> 与 <c>SetClip</c>
/// 各自的两个重载都收指针，签名撞车），要写全就得给它们起不同名字再手工对齐槽位——
/// 那是给一个从不调用的接口付复杂度。所以这里只保留**前 4 个槽位**，够用且不会误导：
/// 谁将来要设偏移，<c>SetOffsetX(float)</c> / <c>SetOffsetY(float)</c> 就在正确的位置上；
/// 要往后用到变换/裁剪，必须**先**把上面的顺序读完并补齐槽位，注释里已经写清了。
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("4d93059d-097b-4651-9a60-f0f25116e2f3")]
internal partial interface IDCompositionVisual
{
    [PreserveSig] int SetOffsetX(nint animation);
    [PreserveSig] int SetOffsetX(float offsetX);
    [PreserveSig] int SetOffsetY(nint animation);
    [PreserveSig] int SetOffsetY(float offsetY);
}

/// <summary><c>dcomp.dll</c> 的入口函数。</summary>
internal static partial class DComposition
{
    /// <summary>创建一个 DirectComposition 设备。传 <c>null</c> 给 DXGI 设备即用默认渲染设备。</summary>
    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);

    /// <summary>创建 <see cref="IDCompositionDevice"/> 并包装成托管句柄。</summary>
    public static Com.IComObject<IDCompositionDevice> CreateDevice()
    {
        Guid iid = typeof(IDCompositionDevice).GUID;
        int hr = DCompositionCreateDevice(0, in iid, out nint pointer);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return Com.ComObject.FromRequiredPointer<IDCompositionDevice>(pointer);
    }
}
