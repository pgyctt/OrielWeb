using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>拖放事件的种类（宿主侧）。</summary>
internal enum Win32DragKind
{
    /// <summary>拖入窗口：载荷可用，路径已解析。</summary>
    Enter,

    /// <summary>在窗口内移动：只有坐标（可能每秒几十条）。</summary>
    Over,

    /// <summary>拖出窗口：没有坐标，也没有载荷。</summary>
    Leave,

    /// <summary>在窗口内松手：载荷可用，路径已解析。</summary>
    Drop,
}

/// <summary>
/// 一次拖放事件：种类 + 路径（仅 enter/drop 有）+ **客户区物理像素**坐标（leave 无意义，记 0,0）。
/// </summary>
internal readonly record struct Win32DragEvent(Win32DragKind Kind, IReadOnlyList<string> Paths, int X, int Y);

/// <summary>
/// 窗口的 OLE 拖放目标（<c>IDropTarget</c>），提供 enter / over / leave / drop 四种回调。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不得不手写 vtable。</b>本仓库的 WebView2 互操作走 <c>WebView2Aot</c> 的源生成绑定
/// （<c>[GeneratedComInterface]</c>），没有手写 COM 的先例——但 <c>IDropTarget</c> 不在那套绑定里，
/// 它是 OLE 的接口，而 .NET 5+ 已移除内置的 COM 可调用包装（<c>Marshal.GetComInterfaceForObject</c>
/// 在 Core 上直接抛 <c>PlatformNotSupportedException</c>）。于是这里用最底层也最可控的做法：
/// 手搭 vtable + <c>[UnmanagedCallersOnly]</c> 静态入口 + <c>GCHandle</c> 找回归属对象。
/// </para>
/// <para>
/// <b>COM 的 <c>this</c> 就是这个 GCHandle 的指针。</b><c>GCHandle.ToIntPtr</c> 返回的地址在
/// 句柄存活期间稳定，正好可以当作对象身份用：<c>QueryInterface</c> 原样回它，
/// 每个回调再用 <c>GCHandle.FromIntPtr</c> 换回托管对象。这样不需要额外的 identity 块。
/// </para>
/// <para>
/// <b>引用计数必须真的做。</b>OLE 会在拖放会话期间持有接口指针，引用计数到 0 之前
/// 释放 GCHandle 会让回调拿到已死的对象（表现为拖动时随机崩）。<c>Dispose</c> 只释放
/// "创建时那一个引用"，剩下的等 COM 端 Release。
/// </para>
/// <para>
/// <b>为什么不是 WM_DROPFILES。</b>那条路只在**松手那一刻**回调一次，拿不到拖动过程中的任何信息
/// （见 <c>Win32Native.cs</c> 里那节注释：当时的需求只到 drop）。要边拖边汇报，只有 OLE 这条路。
/// </para>
/// </remarks>
internal sealed unsafe class Win32DropTarget : IDisposable
{
    /// <summary>vtable 槽位数：IUnknown 三个 + IDropTarget 四个。</summary>
    private const int VtblSlots = 7;

    private static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>
    /// 共享的 vtable：所有实例的函数指针都一样，不同的只是 <c>this</c>（各自的 GCHandle）。
    /// 进程级分配一次，永不释放——它没有生命周期问题，按实例分配反而要处理"最后一个实例释放后
    /// 别的实例还在用"的竞态。
    /// </summary>
    private static readonly nint s_vtbl = CreateVtbl();

    private readonly nint _hwnd;
    private readonly Action<Win32DragEvent> _onEvent;

    /// <summary>
    /// COM 对象块：<c>[0..7]</c> 是 vtable 指针，<c>[8..15]</c> 是托管对象的 GCHandle。
    /// </summary>
    /// <remarks>
    /// <b>不能直接拿 GCHandle 指针当对象指针用。</b>COM 规定对象指针指向的内存**首字段必须是
    /// vtable 指针**，而 <c>GCHandle.ToIntPtr</c> 返回的是句柄表条目——它的第一个 qword 不是
    /// vtable。第一版就是这么写的，症状是 <c>RegisterDragDrop</c> 之后进程立刻以
    /// <c>0xC0000409</c> 消失（OLE 按我们给的首字段跳到野地址），连托管堆栈都没有。
    /// </remarks>
    private nint _identity;

    /// <summary>托管对象的保活句柄（<c>_identity</c> 里存的是它的指针）。</summary>
    private GCHandle _self;

    private int _refCount = 1;
    private bool _registered;

    /// <summary>本次拖动会话里的路径。enter 时填，drop/leave 时清空。</summary>
    private List<string> _sessionPaths = [];

    internal Win32DropTarget(nint hwnd, Action<Win32DragEvent> onEvent)
    {
        _hwnd = hwnd;
        _onEvent = onEvent;
    }

    /// <summary>创建时的那个引用是否还在（供宿主判断要不要再 Dispose）。</summary>
    internal bool IsRegistered => _registered;

    /// <summary>
    /// 把窗口注册成拖放目标。失败时返回 false 并留一条诊断——**调用方必须能降级**
    /// （退回 <c>WM_DROPFILES</c>，只有 drop 但至少能用）。
    /// </summary>
    /// <remarks>
    /// <c>DRAGDROP_E_ALREADYREGISTERED</c>（<c>0x80040101</c>）是最值得注意的失败：
    /// 说明这个窗口上已经有别人注册过（例如 WebView2 没关掉 <c>AllowExternalDrop</c> 时自己占了）。
    /// 那种情况下先 <c>RevokeDragDrop</c> 再注册是合法的——窗口是我们的。
    /// </remarks>
    internal bool Register()
    {
        if (_registered)
        {
            return true;
        }

        _self = GCHandle.Alloc(this);

        // 16 字节的对象块：首字段 vtable（COM 要求），第二字段句柄（我们自己找回归属对象）
        _identity = Marshal.AllocHGlobal(2 * sizeof(nint));
        var block = (nint*)_identity;
        block[0] = s_vtbl;
        block[1] = GCHandle.ToIntPtr(_self);

        int hr = Win32.RegisterDragDrop(_hwnd, _identity);
        if (hr < 0)
        {
            OrielDiagnostics.Report(
                $"[OrielWeb] RegisterDragDrop 失败：0x{hr:X8}——拖放的 enter/over/leave 不可用，" +
                "退回 WM_DROPFILES（只有 drop）。");
            FreeHandle();
            return false;
        }

        _registered = true;
        return true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            // 撤销之后再少一个引用：撤销保证 OLE 不会再发起新回调，但可能仍有在途的
            if (Win32.RevokeDragDrop(_hwnd) < 0)
            {
                OrielDiagnostics.Report("[OrielWeb] RevokeDragDrop 失败（窗口可能已销毁）。");
            }
        }

        ReleaseCore();
    }

    /// <summary>少一个引用；归零时释放 GCHandle。</summary>
    private void ReleaseCore()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            FreeHandle();
        }
    }

    /// <summary>释放对象块与句柄（幂等）。引用计数归零后的唯一清理动作。</summary>
    private void FreeHandle()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        if (_identity != 0)
        {
            Marshal.FreeHGlobal(_identity);
            _identity = 0;
        }
    }

    // ------------------------------------------------------------------
    // vtable
    // ------------------------------------------------------------------

    private static nint CreateVtbl()
    {
        nint memory = Marshal.AllocHGlobal(VtblSlots * sizeof(nint));
        var slots = (nint*)memory;
        slots[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterfaceImpl;
        slots[1] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&AddRefImpl;
        slots[2] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&ReleaseImpl;
        slots[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, POINTL, uint*, int>)&DragEnterImpl;
        slots[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, POINTL, uint*, int>)&DragOverImpl;
        slots[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&DragLeaveImpl;
        slots[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, POINTL, uint*, int>)&DropImpl;
        return memory;
    }

    /// <summary>
    /// 仅供单测：vtable 的 7 个槽位。
    /// </summary>
    /// <remarks>
    /// 槽位写反（例如 DragEnter 与 Drop 对调）在运行期表现为"拖动时行为诡异"，
    /// 而签名相同不会编译报错——只有断言"每个槽都非空且互不相同"能廉价地抓到它。
    /// </remarks>
    internal static ReadOnlySpan<nint> VtblSlotsForTest
        => new((void*)s_vtbl, VtblSlots);

    private static Win32DropTarget? FromSelf(nint self)
    {
        if (self == 0)
        {
            return null;
        }

        // 对象块的第二个字段才是句柄指针；首字段是 vtable（COM 的要求）
        nint handle = *(nint*)(self + sizeof(nint));
        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as Win32DropTarget;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterfaceImpl(nint self, Guid* riid, nint* ppvObject)
    {
        // 这三个入口**必须**自己兜异常：`[UnmanagedCallersOnly]` 方法里抛出未捕获异常，
        // 运行时的处置是 fail-fast（进程以 0xC0000409 直接消失），不是当成普通崩溃抛给上层。
        // 这一点在加第一版时踩过：注册后进程立刻退出，且没有任何托管堆栈可看。
        try
        {
            if (ppvObject == null)
            {
                return unchecked((int)0x80004003); // E_POINTER
            }

            if (FromSelf(self) is not { } target)
            {
                *ppvObject = 0;
                return unchecked((int)0x80004005); // E_FAIL
            }

            if (*riid == IID_IDropTarget || *riid == IID_IUnknown)
            {
                Interlocked.Increment(ref target._refCount);
                *ppvObject = self;
                return 0;
            }

            *ppvObject = 0;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] QueryInterface 处理失败：{ex}");
            if (ppvObject != null)
            {
                *ppvObject = 0;
            }

            return unchecked((int)0x80004005);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int AddRefImpl(nint self)
    {
        try
        {
            return FromSelf(self) is { } target ? Interlocked.Increment(ref target._refCount) : 0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int ReleaseImpl(nint self)
    {
        try
        {
            if (FromSelf(self) is not { } target)
            {
                return 0;
            }

            int remaining = Interlocked.Decrement(ref target._refCount);
            if (remaining == 0)
            {
                target.FreeHandle();
            }

            return remaining;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int DragEnterImpl(nint self, nint dataObject, uint keyState, POINTL pt, uint* effect)
    {
        if (effect == null)
        {
            return unchecked((int)0x80004003);
        }

        try
        {
            if (FromSelf(self) is not { } target)
            {
                *effect = 0;
                return 0;
            }

            target._sessionPaths = ReadPaths(dataObject);

            // 拖的不是文件（文本、URL 之类）就明确不受理：返回 NONE 会让源端显示禁止光标，
            // 比"受理了但什么都不发生"诚实。也不上报事件——给页面一个空 enter 只会让它去处理
            // 一个它处理不了的拖放。
            if (target._sessionPaths.Count == 0)
            {
                *effect = 0;
                return 0;
            }

            *effect = Win32.DROPEFFECT_COPY;
            target.Report(Win32DragKind.Enter, target._sessionPaths, pt);
        }
        catch (Exception ex)
        {
            // 异常绝不能穿越 unmanaged 边界：那会直接终止进程
            *effect = 0;
            OrielDiagnostics.Report($"[OrielWeb] DragEnter 处理失败：{ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int DragOverImpl(nint self, uint keyState, POINTL pt, uint* effect)
    {
        if (effect == null)
        {
            return unchecked((int)0x80004003);
        }

        try
        {
            if (FromSelf(self) is not { } target || target._sessionPaths.Count == 0)
            {
                *effect = 0;
                return 0;
            }

            // **必须回有效 effect，否则 Drop 根本不会到达**。这是本批最容易埋进去的静默失败：
            // 症状是"拖进去了、光标也是复制，松手却什么都不发生"，且不报错。
            *effect = Win32.DROPEFFECT_COPY;
            target.Report(Win32DragKind.Over, [], pt);
        }
        catch (Exception ex)
        {
            *effect = 0;
            OrielDiagnostics.Report($"[OrielWeb] DragOver 处理失败：{ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int DragLeaveImpl(nint self)
    {
        try
        {
            if (FromSelf(self) is { } target)
            {
                target._sessionPaths = [];
                target.Report(Win32DragKind.Leave, [], default);
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] DragLeave 处理失败：{ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int DropImpl(nint self, nint dataObject, uint keyState, POINTL pt, uint* effect)
    {
        if (effect == null)
        {
            return unchecked((int)0x80004003);
        }

        try
        {
            if (FromSelf(self) is not { } target)
            {
                *effect = 0;
                return 0;
            }

            // drop 时重新解析而不是复用 enter 的结果：两次 GetData 是廉价的，
            // 而"enter 之后载荷变了"虽然罕见，复用会让页面拿到与松手那一刻不一致的列表。
            List<string> paths = ReadPaths(dataObject);
            target._sessionPaths = [];

            if (paths.Count == 0)
            {
                *effect = 0;
                return 0;
            }

            *effect = Win32.DROPEFFECT_COPY;
            target.Report(Win32DragKind.Drop, paths, pt);
        }
        catch (Exception ex)
        {
            *effect = 0;
            OrielDiagnostics.Report($"[OrielWeb] Drop 处理失败：{ex}");
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // 载荷与坐标
    // ------------------------------------------------------------------

    /// <summary>
    /// 从 <c>IDataObject</c> 取 <c>CF_HDROP</c> 并读成路径。
    /// </summary>
    /// <remarks>
    /// 直接按 vtable 下标调 <c>GetData</c>（IUnknown 三个之后第 4 个，索引 3）：
    /// 那套接口没有源生成绑定，而为了一个方法去写完整的 <c>IDataObject</c> 声明不值得。
    /// </remarks>
    private static List<string> ReadPaths(nint dataObject)
    {
        if (dataObject == 0)
        {
            return [];
        }

        nint* vtbl = *(nint**)dataObject;
        var getData = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, STGMEDIUM*, int>)vtbl[3];

        var format = new FORMATETC
        {
            cfFormat = Win32.CF_HDROP,
            ptd = 0,
            dwAspect = Win32.DVASPECT_CONTENT,
            lindex = -1,
            tymed = Win32.TYMED_HGLOBAL,
        };

        STGMEDIUM medium = default;
        if (getData(dataObject, &format, &medium) < 0)
        {
            return [];
        }

        try
        {
            if (medium.tymed != Win32.TYMED_HGLOBAL || medium.hGlobal == 0)
            {
                return [];
            }

            return Win32FileDropReader.Read(medium.hGlobal);
        }
        finally
        {
            // 不 Release 就是泄漏一块全局内存：CF_HDROP 的载荷归 STGMEDIUM 所有
            Win32.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>把屏幕坐标换算成客户区物理像素后上报。</summary>
    /// <remarks>
    /// 不用 <c>ScreenToClient</c>：它的 <c>POINT</c> 无法用于源生成 P/Invoke（既有代码里已有同样说明）。
    /// 拿客户区左上角的屏幕坐标再相减，结果一样且只用 blittable 声明。
    /// </remarks>
    private void Report(Win32DragKind kind, IReadOnlyList<string> paths, POINTL screenPoint)
    {
        int x = 0;
        int y = 0;

        if (kind is not Win32DragKind.Leave)
        {
            var origin = new POINT(0, 0);
            if (Win32.ClientToScreen(_hwnd, ref origin))
            {
                x = screenPoint.x - origin.X;
                y = screenPoint.y - origin.Y;
            }
            else
            {
                // 换算失败仍然上报（丢事件比坐标不准更糟），但留痕——否则页面会拿到一个
                // "看起来像拖到了左上角"的 0,0 而没有任何线索
                OrielDiagnostics.Report("[OrielWeb] 拖放坐标换算失败（ClientToScreen），position 记为 0,0。");
            }
        }

        _onEvent(new Win32DragEvent(kind, paths, x, y));
    }
}
