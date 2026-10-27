using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Interop;

namespace OrielWeb.Platform.Windows.WebView2.Utilities;

/// <summary>
/// WebView2 运行时加载器：从内嵌资源里取 <c>WebView2Loader.dll</c>，落盘后加载并解析导出函数。
/// </summary>
/// <remarks>
/// 不能只靠 <c>DllImport("WebView2Loader.dll")</c>：单文件发布时那个 DLL 不在磁盘上，
/// 必须先把它从资源里写出来。写出来的位置固定到临时目录下的架构子目录，
/// 同一进程重复调用时直接复用，避免多份句柄。
/// </remarks>
internal static unsafe class WebView2Utilities
{
    private const string ResourcePrefix = "OrielWeb.runtimes.";
    private const string ResourceSuffix = ".native.WebView2Loader.dll";

    private static readonly object Gate = new();
    private static bool _initialized;
    private static nint _loaderHandle;

    private static delegate* unmanaged<char*, char*, nint, nint, int> _createWithOptions;
    private static delegate* unmanaged<char*, nint*, int> _getBrowserVersionString;

    /// <summary>加载器是否已就绪。</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// 从 <paramref name="assembly"/> 的内嵌资源里提取与当前进程架构匹配的加载器并载入。
    /// </summary>
    /// <remarks>幂等：重复调用只做一次实际加载。</remarks>
    public static void Initialize(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            string architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                var other => throw new PlatformNotSupportedException($"WebView2 加载器没有 {other} 版本。"),
            };

            string resourceName = ResourcePrefix + architecture + ResourceSuffix;
            using Stream? source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"程序集 {assembly.GetName().Name} 里找不到内嵌资源 {resourceName}；"
                    + "WebView2Loader.dll 必须由库的 buildTransitive 目标嵌入。");

            string folder = Path.Combine(Path.GetTempPath(), "OrielWeb.Platform.Windows.WebView2", architecture);
            Directory.CreateDirectory(folder);
            string loaderPath = Path.Combine(folder, "WebView2Loader.dll");

            // 已存在且大小一致就复用：避免多进程同时写同一个文件时的 sharing violation。
            bool needsWrite = !File.Exists(loaderPath) || new FileInfo(loaderPath).Length != source.Length;
            if (needsWrite)
            {
                string tempPath = loaderPath + "." + Environment.ProcessId + ".tmp";
                using (FileStream target = File.Create(tempPath))
                {
                    source.CopyTo(target);
                }

                File.Move(tempPath, loaderPath, overwrite: true);
            }

            LoadLoader(loaderPath);
        }
    }

    /// <summary>
    /// 直接从磁盘上的 <c>WebView2Loader.dll</c> 加载。
    /// </summary>
    /// <remarks>调试与冒烟测试用：跳过内嵌资源提取，直接指定一个已知路径。</remarks>
    public static void InitializeFromPath(string loaderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(loaderPath);

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            LoadLoader(loaderPath);
        }
    }

    private static unsafe void LoadLoader(string loaderPath)
    {
        _loaderHandle = NativeLibrary.Load(loaderPath);
        _createWithOptions = (delegate* unmanaged<char*, char*, nint, nint, int>)
            NativeLibrary.GetExport(_loaderHandle, "CreateCoreWebView2EnvironmentWithOptions");
        _getBrowserVersionString = (delegate* unmanaged<char*, nint*, int>)
            NativeLibrary.GetExport(_loaderHandle, "GetAvailableCoreWebView2BrowserVersionString");

        _initialized = true;
    }

    /// <summary>读取已安装的 WebView2 运行时版本；未安装返回 <c>null</c>。</summary>
    public static string? GetAvailableCoreWebView2BrowserVersionString(string? browserExecutableFolder)
    {
        if (!_initialized || _getBrowserVersionString == null)
        {
            return null;
        }

        fixed (char* folder = browserExecutableFolder)
        {
            nint version;
            int hr = _getBrowserVersionString(folder, &version);
            if (hr < 0)
            {
                return null;
            }

            var pwstr = new PWSTR(version);
            return pwstr.ToStringAndDispose();
        }
    }

    /// <summary>
    /// 把一个托管对象 QI 成 <typeparamref name="T"/>；不支持时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// WebView2 的版本化接口（<c>ICoreWebView2Controller2</c> 等）都是同一 COM 对象上的额外接口，
    /// 老运行时上 QI 会失败——这正是"可选增强"的判定方式。
    /// </remarks>
    public static T? GetInterface<T>(object? instance) where T : class
    {
        if (instance is null)
        {
            return null;
        }

        if (instance is T already)
        {
            return already;
        }

        if (!ComWrappers.TryGetComInstance(instance, out nint unknown) || unknown == 0)
        {
            return null;
        }

        Guid iid = typeof(T).GUID;
        if (Marshal.QueryInterface(unknown, in iid, out nint ptr) < 0 || ptr == 0)
        {
            return null;
        }

        return ComInterfaceMarshaller<T>.ConvertToManaged((void*)ptr);
    }

    internal static int CreateEnvironmentWithOptions(
        string? browserExecutableFolder,
        string userDataFolder,
        nint environmentOptions,
        nint completedHandler)
    {
        if (!_initialized || _createWithOptions == null)
        {
            throw new InvalidOperationException("WebView2 加载器尚未初始化，先调用 WebView2Utilities.Initialize。");
        }

        fixed (char* folder = browserExecutableFolder)
        fixed (char* userData = userDataFolder)
        {
            return _createWithOptions(folder, userData, environmentOptions, completedHandler);
        }
    }
}
