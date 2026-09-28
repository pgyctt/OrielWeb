using System.Runtime.InteropServices;

namespace OrielWeb.Platform.Windows.Interop;

// ============================================================================
// WebView2 手工互操作层（双向均为裸指针 + vtable 槽位调用）。
//
// 为什么不用 [GeneratedComInterface]/ComWrappers？实测（.NET 10.0.401）：
//   1. CCW 方向：StrategyBasedComWrappers.GetOrCreateComInterfaceForObject
//      首次调用后一律返回空指针；WebView2 加载器查询 IReferenceTrackerTarget 时
//      ComWrappers.ManagedObjectWrapper.AsRuntimeDefined 抛 NRE 崩溃进程；
//   2. RCW 方向：ConvertToManaged 在 AOT 下触发 Access Violation。
// 因此参考 smourier/WebView2Aot 的做法，本层完全脱离 ComWrappers：
//   - 原生 → 托管：手工 vtable + [UnmanagedCallersOnly]（Win32NativeCallbacks.cs）
//   - 托管 → 原生：下面的指针包装结构（vtable 槽位序对齐官方 WebView2.h）
// ============================================================================

internal struct EventRegistrationToken
{
    public nint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WebView2Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>COM 方法槽位（0 基）= IUnknown 3 槽 + 方法在接口中的序号（1 基）+ 2。</summary>
internal static class WebView2Slots
{
    public const int Environment_CreateController = 3;

    // Controller：17 个自有方法前的占位（含 add/remove_AcceleratorKeyPressed、get/put_ParentWindow）
    public const int Controller_put_IsVisible = 4;
    public const int Controller_put_Bounds = 6;
    public const int Controller_NotifyParentWindowPositionChanged = 23;
    public const int Controller_get_CoreWebView2 = 25;

    public const int WebView2_get_Settings = 3;
    public const int WebView2_Navigate = 5;
    public const int WebView2_add_NavigationCompleted = 15;
    public const int WebView2_AddScriptToExecuteOnDocumentCreated = 27;
    public const int WebView2_ExecuteScript = 29;
    public const int WebView2_PostWebMessageAsJson = 32;
    public const int WebView2_add_WebMessageReceived = 34;
    public const int WebView2_add_DocumentTitleChanged = 46;
    public const int WebView2_get_DocumentTitle = 48;

    public const int Settings_put_AreDevToolsEnabled = 12;

    // ICoreWebView2 共 58 个方法（槽 3..60）→ _2 的 7 个方法（槽 61..67）→ _3 从 68 起
    public const int Env3_SetVirtualHostNameToFolderMapping = 71;

    public const int WebMessageArgs_get_WebMessageAsJson = 4;
    public const int NavCompletedArgs_get_IsSuccess = 3;
}

internal static class WebView2Iids
{
    public static readonly Guid ICoreWebView2_3 = new("A0D6DF20-3B92-416D-AA0C-437A9C727857");
}

// ---------------------------------------------------------------------------
// 指针包装：每个结构持有原生接口指针，方法按槽位取函数指针直接调用。
// ---------------------------------------------------------------------------

internal readonly unsafe struct WebView2EnvironmentPtr
{
    private readonly void* _self;
    public WebView2EnvironmentPtr(void* self) => _self = self;
    private void** Vtable => (void**)*(void**)_self;

    public int CreateCoreWebView2Controller(nint parentWindow, nint handler)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, nint, nint, int>)Vtable[WebView2Slots.Environment_CreateController];
        return fn(_self, parentWindow, handler);
    }
}

internal readonly unsafe struct WebView2ControllerPtr
{
    private readonly void* _self;
    public WebView2ControllerPtr(void* self) => _self = self;
    public void* Self => _self;
    private void** Vtable => (void**)*(void**)_self;

    public int put_IsVisible(int value)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, int, int>)Vtable[WebView2Slots.Controller_put_IsVisible];
        return fn(_self, value);
    }

    public int put_Bounds(WebView2Rect bounds)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, WebView2Rect, int>)Vtable[WebView2Slots.Controller_put_Bounds];
        return fn(_self, bounds);
    }

    public int NotifyParentWindowPositionChanged()
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, int>)Vtable[WebView2Slots.Controller_NotifyParentWindowPositionChanged];
        return fn(_self);
    }

    public int get_CoreWebView2(out WebView2Ptr coreWebView2)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, void**, int>)Vtable[WebView2Slots.Controller_get_CoreWebView2];
        void* result = null;
        var hr = fn(_self, &result);
        coreWebView2 = new WebView2Ptr(result);
        return hr;
    }
}

/// <summary>ICoreWebView2（含 ICoreWebView2Settings 的引用）。</summary>
internal readonly unsafe struct WebView2Ptr
{
    private readonly void* _self;
    public WebView2Ptr(void* self) => _self = self;
    public void* Self => _self;
    private void** Vtable => (void**)*(void**)_self;

    public WebView2SettingsPtr GetSettings()
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, void**, int>)Vtable[WebView2Slots.WebView2_get_Settings];
        void* settings = null;
        _ = fn(_self, &settings);
        return new WebView2SettingsPtr(settings);
    }

    public int Navigate(string uri)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort*, int>)Vtable[WebView2Slots.WebView2_Navigate];
        fixed (char* pUri = uri)
        {
            return fn(_self, (ushort*)pUri);
        }
    }

    public int AddScriptToExecuteOnDocumentCreated(string javaScript, nint handler)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort*, nint, int>)Vtable[WebView2Slots.WebView2_AddScriptToExecuteOnDocumentCreated];
        fixed (char* pScript = javaScript)
        {
            return fn(_self, (ushort*)pScript, handler);
        }
    }

    public int ExecuteScript(string javaScript, nint handler)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort*, nint, int>)Vtable[WebView2Slots.WebView2_ExecuteScript];
        fixed (char* pScript = javaScript)
        {
            return fn(_self, (ushort*)pScript, handler);
        }
    }

    public int PostWebMessageAsJson(string webMessageAsJson)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort*, int>)Vtable[WebView2Slots.WebView2_PostWebMessageAsJson];
        fixed (char* pMessage = webMessageAsJson)
        {
            return fn(_self, (ushort*)pMessage);
        }
    }

    public int add_WebMessageReceived(nint handler, out EventRegistrationToken token)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, nint, EventRegistrationToken*, int>)Vtable[WebView2Slots.WebView2_add_WebMessageReceived];
        fixed (EventRegistrationToken* pToken = &token)
        {
            return fn(_self, handler, pToken);
        }
    }

    public int add_NavigationCompleted(nint handler, out EventRegistrationToken token)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, nint, EventRegistrationToken*, int>)Vtable[WebView2Slots.WebView2_add_NavigationCompleted];
        fixed (EventRegistrationToken* pToken = &token)
        {
            return fn(_self, handler, pToken);
        }
    }

    public int add_DocumentTitleChanged(nint handler, out EventRegistrationToken token)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, nint, EventRegistrationToken*, int>)Vtable[WebView2Slots.WebView2_add_DocumentTitleChanged];
        fixed (EventRegistrationToken* pToken = &token)
        {
            return fn(_self, handler, pToken);
        }
    }

    public string GetDocumentTitle()
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort**, int>)Vtable[WebView2Slots.WebView2_get_DocumentTitle];
        ushort* result = null;
        _ = fn(_self, &result);
        return PtrToStringAndFree((nint)result);
    }

    internal static string PtrToStringAndFree(nint ptr)
    {
        if (ptr == 0)
        {
            return string.Empty;
        }
        var s = Marshal.PtrToStringUni(ptr) ?? string.Empty;
        Marshal.FreeCoTaskMem(ptr);
        return s;
    }
}

internal readonly unsafe struct WebView2SettingsPtr
{
    private readonly void* _self;
    public WebView2SettingsPtr(void* self) => _self = self;
    private void** Vtable => (void**)*(void**)_self;

    public int put_AreDevToolsEnabled(int value)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, int, int>)Vtable[WebView2Slots.Settings_put_AreDevToolsEnabled];
        return fn(_self, value);
    }
}

/// <summary>ICoreWebView2_3：虚拟主机映射（槽位序 = 基础 56 方法 + _2 的 7 方法 + _3 序号）。</summary>
internal readonly unsafe struct WebView2_3Ptr
{
    private readonly void* _self;
    public WebView2_3Ptr(void* self) => _self = self;
    private void** Vtable => (void**)*(void**)_self;

    public int SetVirtualHostNameToFolderMapping(string hostName, string folderPath, int accessKind)
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort*, ushort*, int, int>)Vtable[WebView2Slots.Env3_SetVirtualHostNameToFolderMapping];
        fixed (char* pHost = hostName)
        fixed (char* pFolder = folderPath)
        {
            return fn(_self, (ushort*)pHost, (ushort*)pFolder, accessKind);
        }
    }
}

internal readonly unsafe struct WebView2WebMessageReceivedEventArgsPtr
{
    private readonly void* _self;
    public WebView2WebMessageReceivedEventArgsPtr(void* self) => _self = self;
    private void** Vtable => (void**)*(void**)_self;

    public string GetWebMessageAsJson()
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, ushort**, int>)Vtable[WebView2Slots.WebMessageArgs_get_WebMessageAsJson];
        ushort* result = null;
        _ = fn(_self, &result);
        return WebView2Ptr.PtrToStringAndFree((nint)result);
    }
}

internal readonly unsafe struct WebView2NavigationCompletedEventArgsPtr
{
    private readonly void* _self;
    public WebView2NavigationCompletedEventArgsPtr(void* self) => _self = self;
    private void** Vtable => (void**)*(void**)_self;

    public bool GetIsSuccess()
    {
        var fn = (delegate* unmanaged[MemberFunction]<void*, int*, int>)Vtable[WebView2Slots.NavCompletedArgs_get_IsSuccess];
        int value = 0;
        _ = fn(_self, &value);
        return value != 0;
    }
}

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

internal static unsafe class WebView2Native
{
    /// <summary>对原生 COM 对象做 QueryInterface（直接走对象自身 vtable[0]）。</summary>
    public static bool TryQueryInterface(void* self, in Guid iid, out void* result)
    {
        fixed (Guid* pIid = &iid)
        {
            void* outPtr = null;
            var qi = (delegate* unmanaged[MemberFunction]<void*, Guid*, void**, int>)(*(void***)self)[0];
            var hr = qi(self, pIid, &outPtr);
            result = outPtr;
            return hr >= 0;
        }
    }

    /// <summary>
    /// AddRef 一个回调参数传入的 COM 对象。
    /// 回调参数只在调用期间有效：要把对象存过回调生命周期，必须先 AddRef
    /// （否则 WebView2 在回调返回后 Release，对象析构、浏览器进程树随之关闭）。
    /// </summary>
    public static void AddRefComObject(void* self)
    {
        var addRef = (delegate* unmanaged[MemberFunction]<void*, uint>)(*(void***)self)[1];
        _ = addRef(self);
    }
}

// ---------------------------------------------------------------------------
// WebView2Loader.dll 入口（native 库随 Microsoft.Web.WebView2 包分发到应用目录）
// ---------------------------------------------------------------------------

internal static unsafe partial class WebView2LoaderNative
{
    private const string LibraryName = "WebView2Loader";

    [LibraryImport(LibraryName, EntryPoint = "CreateCoreWebView2EnvironmentWithOptions", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateCoreWebView2EnvironmentWithOptions(
        string? browserExecutableFolder,
        string userDataFolder,
        nint environmentOptions,
        nint environmentCreatedHandler);
}

/// <summary>HRESULT 检查。</summary>
internal static class WebView2ComHelper
{
    internal static void ThrowIfFailed(int hr, string context)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"{context} 失败：HRESULT 0x{hr:X8}。请确认已安装 WebView2 运行时（https://go.microsoft.com/fwlink/p/?LinkId=2124703）。");
        }
    }
}
