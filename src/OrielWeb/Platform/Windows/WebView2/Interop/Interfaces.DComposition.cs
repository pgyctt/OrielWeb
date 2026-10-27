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
/// <remarks>本库只把它当作"可以挂到目标上的对象"传递，从不调用它的方法。</remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("4d93059d-097b-4651-9a60-f0f25116e2f3")]
internal partial interface IDCompositionVisual
{
    [PreserveSig] int SetOffset(float offsetX, float offsetY);
    [PreserveSig] int SetOffset(nint animation, float offsetY);
    [PreserveSig] int SetOffsetY(float offsetY);
    [PreserveSig] int SetOffsetY(nint animation);
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
