using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

// ============================================================================
// 手工实现的 COM 回调对象（CCW）。
//
// 为什么不用 [GeneratedComClass] + ComWrappers？实测（.NET 10.0.401）：
//   1. StrategyBasedComWrappers.GetOrCreateComInterfaceForObject 首次调用之后，
//      后续调用一律返回空指针；
//   2. WebView2 加载器会查询 IReferenceTrackerTarget，
//      ComWrappers.ManagedObjectWrapper.AsRuntimeDefined 在该路径上
//      抛出 NullReferenceException 直接崩溃进程；
//   3. ConvertToManaged（RCW 方向）在 AOT 下触发 Access Violation。
// 因此按 COM 规则手工构建 vtable（IUnknown + Invoke），用
// [UnmanagedCallersOnly] thunk 分发到托管回调，引用计数手动维护。
// ============================================================================

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeComObject
{
    public void** Vtable;
    public Guid* Iid;
    public void* State; // GCHandle.ToIntPtr，指向托管回调状态
    public int RefCount;
}

internal static unsafe class WebView2NativeCallbacks
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_EnvironmentHandler = new("4E8A3389-C9D8-4BD2-B6B5-124FEE6CC14D");
    private static readonly Guid IID_ControllerHandler = new("6C4819F3-C9B7-4260-8127-C9F5BDE7F68C");
    private static readonly Guid IID_WebMessageHandler = new("57213F19-00E6-49FA-8E07-898EA01ECBD2");
    private static readonly Guid IID_NavigationCompletedHandler = new("D33A35BF-1C49-4F98-93AB-006E0533FE1C");
    private static readonly Guid IID_DocumentTitleHandler = new("F5F2B923-953E-4042-9F95-F3A118E1AFD4");
    private static readonly Guid IID_ExecuteScriptHandler = new("49511172-CC67-4BCA-9923-137112F4C4CC");
    private static readonly Guid IID_AddScriptHandler = new("B99369F3-9B11-47B5-BC6F-8E7895FCEA17");

    // ------------------------------------------------------------------
    // 工厂
    // ------------------------------------------------------------------

    public static nint CreateEnvironmentHandler(Win32WindowHost host) => Create(host, IID_EnvironmentHandler, EnvironmentVtable());
    public static nint CreateControllerHandler(Win32WindowHost host) => Create(host, IID_ControllerHandler, ControllerVtable());
    public static nint CreateWebMessageHandler(WebMessageReceivedHandler handler) => Create(handler, IID_WebMessageHandler, WebMessageVtable());
    public static nint CreateNavigationCompletedHandler(Win32WindowHost host) => Create(host, IID_NavigationCompletedHandler, NavigationCompletedVtable());
    public static nint CreateDocumentTitleHandler(Win32WindowHost host) => Create(host, IID_DocumentTitleHandler, DocumentTitleVtable());
    public static nint CreateExecuteScriptHandler(object state) => Create(state, IID_ExecuteScriptHandler, ExecuteScriptVtable());
    public static nint CreateAddScriptHandler() => Create(null, IID_AddScriptHandler, AddScriptVtable());

    private static unsafe nint Create(object? state, Guid iid, void** vtable)
    {
        var obj = (NativeComObject*)NativeMemory.Alloc((nuint)sizeof(NativeComObject));
        obj->Vtable = vtable;
        obj->Iid = CopyGuid(iid);
        obj->State = state is null ? null : (void*)GCHandle.ToIntPtr(GCHandle.Alloc(state));
        obj->RefCount = 1;
        return (nint)obj;
    }

    private static unsafe Guid* CopyGuid(Guid guid)
    {
        var p = (Guid*)NativeMemory.Alloc((nuint)sizeof(Guid));
        *p = guid;
        return p;
    }

    private static unsafe void** AllocVtable(int slotCount)
    {
        var vtable = (void**)NativeMemory.Alloc((nuint)(sizeof(void*) * slotCount));
        vtable[0] = (void*)(delegate* unmanaged<NativeComObject*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (void*)(delegate* unmanaged<NativeComObject*, uint>)&AddRef;
        vtable[2] = (void*)(delegate* unmanaged<NativeComObject*, uint>)&Release;
        return vtable;
    }

    // 每个回调接口在 IUnknown（3 槽）之后只有一个 Invoke 方法
    private static void** s_environmentVtable;
    private static void** EnvironmentVtable() => Ensure(ref s_environmentVtable,
        (void*)(delegate* unmanaged<NativeComObject*, int, void*, int>)&EnvironmentInvoke);
    private static void** s_controllerVtable;
    private static void** ControllerVtable() => Ensure(ref s_controllerVtable,
        (void*)(delegate* unmanaged<NativeComObject*, int, void*, int>)&ControllerInvoke);
    private static void** s_webMessageVtable;
    private static void** WebMessageVtable() => Ensure(ref s_webMessageVtable,
        (void*)(delegate* unmanaged<NativeComObject*, void*, void*, int>)&WebMessageInvoke);
    private static void** s_navigationCompletedVtable;
    private static void** NavigationCompletedVtable() => Ensure(ref s_navigationCompletedVtable,
        (void*)(delegate* unmanaged<NativeComObject*, void*, void*, int>)&NavigationCompletedInvoke);
    private static void** s_documentTitleVtable;
    private static void** DocumentTitleVtable() => Ensure(ref s_documentTitleVtable,
        (void*)(delegate* unmanaged<NativeComObject*, void*, void*, int>)&DocumentTitleInvoke);
    private static void** s_executeScriptVtable;
    private static void** ExecuteScriptVtable() => Ensure(ref s_executeScriptVtable,
        (void*)(delegate* unmanaged<NativeComObject*, int, ushort*, int>)&ExecuteScriptInvoke);
    private static void** s_addScriptVtable;
    private static void** AddScriptVtable() => Ensure(ref s_addScriptVtable,
        (void*)(delegate* unmanaged<NativeComObject*, int, ushort*, int>)&AddScriptInvoke);

    private static void** Ensure(ref void** cached, void* invoke)
    {
        if (cached != null)
        {
            return cached;
        }
        var vtable = AllocVtable(4);
        vtable[3] = invoke;
        cached = vtable;
        return vtable;
    }

    // ------------------------------------------------------------------
    // IUnknown
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int QueryInterface(NativeComObject* self, Guid* iid, void** ppvObject)
    {
        if (ppvObject is null)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }
        *ppvObject = null;
        if (*iid == IID_IUnknown || *iid == *self->Iid)
        {
            *ppvObject = self;
            _ = Interlocked.Increment(ref self->RefCount);
            return 0; // S_OK
        }
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(NativeComObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly]
    private static uint Release(NativeComObject* self)
    {
        var count = Interlocked.Decrement(ref self->RefCount);
        if (count == 0)
        {
            if (self->State is not null)
            {
                GCHandle.FromIntPtr((nint)self->State).Free();
            }
            NativeMemory.Free(self->Iid);
            NativeMemory.Free(self);
        }
        return (uint)count;
    }

    // ------------------------------------------------------------------
    // 接口 thunk（thunk 内不得让异常逃逸，全部转成 HRESULT）
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int EnvironmentInvoke(NativeComObject* self, int errorCode, void* createdEnvironment)
    {
        try
        {
            var host = (Win32WindowHost)GCHandle.FromIntPtr((nint)self->State).Target!;
            return host.OnEnvironmentCreated(errorCode, createdEnvironment);
        }
        catch
        {
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    [UnmanagedCallersOnly]
    private static int ControllerInvoke(NativeComObject* self, int errorCode, void* createdController)
    {
        try
        {
            var host = (Win32WindowHost)GCHandle.FromIntPtr((nint)self->State).Target!;
            return host.OnControllerCreated(errorCode, createdController);
        }
        catch
        {
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    [UnmanagedCallersOnly]
    private static int WebMessageInvoke(NativeComObject* self, void* sender, void* args)
    {
        try
        {
            var handler = (WebMessageReceivedHandler)GCHandle.FromIntPtr((nint)self->State).Target!;
            return handler.OnWebMessageReceived(new WebView2WebMessageReceivedEventArgsPtr(args));
        }
        catch
        {
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    [UnmanagedCallersOnly]
    private static int NavigationCompletedInvoke(NativeComObject* self, void* sender, void* args)
    {
        try
        {
            var host = (Win32WindowHost)GCHandle.FromIntPtr((nint)self->State).Target!;
            if (new WebView2NavigationCompletedEventArgsPtr(args).GetIsSuccess())
            {
                host.RaiseLoadedIfFirst();
            }
        }
        catch
        {
            // 事件回调异常不外泄
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int DocumentTitleInvoke(NativeComObject* self, void* sender, void* args)
    {
        try
        {
            var host = (Win32WindowHost)GCHandle.FromIntPtr((nint)self->State).Target!;
            var webview = new WebView2Ptr(sender);
            var title = webview.GetDocumentTitle();
            if (!string.IsNullOrEmpty(title))
            {
                host.RaiseTitleChanged(title);
            }
        }
        catch
        {
            // 事件回调异常不外泄
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int ExecuteScriptInvoke(NativeComObject* self, int errorCode, ushort* resultObjectAsJson)
    {
        try
        {
            var completion = (TaskCompletionSource<string>)GCHandle.FromIntPtr((nint)self->State).Target!;
            string? result = resultObjectAsJson is null ? null : Marshal.PtrToStringUni((nint)resultObjectAsJson);
            if (errorCode < 0)
            {
                completion.TrySetException(new InvalidOperationException($"ExecuteScript 失败（HRESULT 0x{errorCode:X8}）。"));
            }
            else
            {
                completion.TrySetResult(result ?? "null");
            }
        }
        catch
        {
            // 异常不外泄
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int AddScriptInvoke(NativeComObject* self, int errorCode, ushort* id) => 0;
}
