using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>COM <c>STATSTG</c>。IStream.Stat 的输出结构。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct STATSTG
{
    public nint pwcsName;
    public uint type;
    public ulong cbSize;
    public long mtime;
    public long ctime;
    public long atime;
    public uint grfMode;
    public uint grfLocksSupported;
    public uint clsid;
    public uint grfStateBits;
    public uint reserved;
}

/// <summary>
/// 标准 COM <c>IStream</c>。
/// </summary>
/// <remarks>
/// 固定 IID（<c>0000000C-0000-0000-C000-000000000046</c>）来自 COM 基础规范，不是 WebView2 自定义的。
/// WebView2 只在 <c>CreateWebResourceResponse</c> 与 <c>CapturePreview</c> 这两处用到它。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000000c-0000-0000-c000-000000000046")]
internal unsafe partial interface IStream
{
    [PreserveSig] int Read(void* pv, uint cb, out uint pcbRead);
    [PreserveSig] int Write(void* pv, uint cb, out uint pcbWritten);
    [PreserveSig] int Seek(long dlibMove, uint dwOrigin, out ulong plibNewPosition);
    [PreserveSig] int SetSize(ulong libNewSize);
    [PreserveSig] int CopyTo(nint pstm, ulong cb, out ulong pcbRead, out ulong pcbWritten);
    [PreserveSig] int Commit(uint grfCommitFlags);
    [PreserveSig] int Revert();
    [PreserveSig] int LockRegion(ulong libOffset, ulong cb, uint dwLockType);
    [PreserveSig] int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType);
    [PreserveSig] int Stat(out STATSTG pstatstg, uint grfStatFlag);
    [PreserveSig] int Clone(out nint ppstm);
}
