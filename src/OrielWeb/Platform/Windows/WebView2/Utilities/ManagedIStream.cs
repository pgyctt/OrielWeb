using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Interop;

namespace OrielWeb.Platform.Windows.WebView2.Handlers;

/// <summary>
/// 把托管 <see cref="Stream"/> 暴露成 COM <c>IStream</c>，供 <c>CreateWebResourceResponse</c> 使用。
/// </summary>
/// <remarks>
/// WebView2 是**异步**读这个流的：调用返回之后它还会继续 Read。所以这里既不复制也不缓存，
/// 直接把原始流交出去，由原生侧的引用计数决定它活多久。
/// </remarks>
[GeneratedComClass]
internal sealed unsafe partial class ManagedIStream(Stream stream) : IStream
{
    private const uint StreamSeekSet = 0;
    private const uint StreamSeekCur = 1;
    private const uint StreamSeekEnd = 2;
    private const uint StatFlagNoname = 0x00000001;

    private ulong _position;

    public int Read(void* pv, uint cb, out uint pcbRead)
    {
        try
        {
            Span<byte> buffer = new(pv, checked((int)cb));
            int read = stream.Read(buffer);
            _position += (ulong)read;
            pcbRead = (uint)read;
            return HResult.S_OK;
        }
        catch (Exception)
        {
            pcbRead = 0;
            return HResult.E_FAIL;
        }
    }

    public int Write(void* pv, uint cb, out uint pcbWritten)
    {
        // WebView2 只读不写；显式拒绝而不是静默成功，免得哪天真的被写时数据凭空消失。
        pcbWritten = 0;
        return HResult.E_FAIL;
    }

    public int Seek(long dlibMove, uint dwOrigin, out ulong plibNewPosition)
    {
        try
        {
            long target = dwOrigin switch
            {
                StreamSeekSet => dlibMove,
                StreamSeekCur => (long)_position + dlibMove,
                StreamSeekEnd => stream.Length + dlibMove,
                _ => throw new ArgumentOutOfRangeException(nameof(dwOrigin)),
            };

            _position = (ulong)Math.Max(0, target);
            stream.Position = (long)_position;
            plibNewPosition = _position;
            return HResult.S_OK;
        }
        catch (Exception)
        {
            plibNewPosition = _position;
            return HResult.E_FAIL;
        }
    }

    public int SetSize(ulong libNewSize)
    {
        try
        {
            stream.SetLength((long)libNewSize);
            return HResult.S_OK;
        }
        catch (Exception)
        {
            return HResult.E_FAIL;
        }
    }

    public int CopyTo(nint pstm, ulong cb, out ulong pcbRead, out ulong pcbWritten)
    {
        pcbRead = 0;
        pcbWritten = 0;
        return HResult.E_NOTIMPL;
    }

    public int Commit(uint grfCommitFlags) => HResult.S_OK;

    public int Revert() => HResult.E_NOTIMPL;

    public int LockRegion(ulong libOffset, ulong cb, uint dwLockType) => HResult.E_NOTIMPL;

    public int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) => HResult.E_NOTIMPL;

    public int Stat(out STATSTG pstatstg, uint grfStatFlag)
    {
        pstatstg = default;
        pstatstg.type = 2; // STGTY_STREAM
        pstatstg.cbSize = (ulong)stream.Length;
        return HResult.S_OK;
    }

    public int Clone(out nint ppstm)
    {
        ppstm = 0;
        return HResult.E_NOTIMPL;
    }
}
