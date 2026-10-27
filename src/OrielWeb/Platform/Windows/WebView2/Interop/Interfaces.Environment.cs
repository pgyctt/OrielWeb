using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Platform.Windows.WebView2.Utilities;
using OrielComObject = OrielWeb.Platform.Windows.WebView2.Com.ComObject;

namespace OrielWeb.Platform.Windows.WebView2.Interop;

/// <summary>WebView2 环境：进程级共享，负责创建控制器与资源响应。</summary>
/// <remarks>
/// 所有方法保持原生形状（<c>[PreserveSig] int</c> 返回 HRESULT、out 参数给原始指针），
/// 面向 .NET 的便利包装在 <c>OrielWeb.Platform.Windows.WebView2.Utilities</c> 里，与本层一一对应。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b96d755e-0319-4e92-a296-23436f46a1fc")]
internal partial interface ICoreWebView2Environment
{
    [PreserveSig] int CreateCoreWebView2Controller(nint parentWindow, nint handler);

    [PreserveSig] int CreateWebResourceResponse(nint content, int statusCode, string reasonPhrase, string headers, out nint response);

    [PreserveSig] int get_BrowserVersionString(out nint versionInfo);

    [PreserveSig] int add_NewBrowserVersionAvailable(nint handler, out EventRegistrationToken token);

    [PreserveSig] int remove_NewBrowserVersionAvailable(EventRegistrationToken token);
}

/// <summary>环境 2：新增浏览器版本与运行时路径查询（本项目未直接用，保留以对齐版本链）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("41f3632b-5ef4-404f-ad82-2d606c5a9a21")]
internal partial interface ICoreWebView2Environment2 : ICoreWebView2Environment
{
    [PreserveSig] int CreateWebResourceRequest(string uri, string method, nint postData, string headers, out nint request);
}

/// <summary>环境 3：组合控制器与指针信息。组合式宿主（本库）依赖它的第一个方法。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("80a22ae3-be7c-4ce2-afe1-5a50056cdeeb")]
internal partial interface ICoreWebView2Environment3 : ICoreWebView2Environment2
{
    [PreserveSig] int CreateCoreWebView2CompositionController(nint parentWindow, nint handler);

    [PreserveSig] int CreateCoreWebView2PointerInfo(out nint pointerInfo);
}

/// <summary>环境选项：传给 <c>CreateCoreWebView2EnvironmentWithOptions</c>。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("2fde08a8-1e9a-4766-8c05-95a9ceb9d1c5")]
internal partial interface ICoreWebView2EnvironmentOptions
{
    [PreserveSig] int get_AdditionalBrowserArguments(out nint value);
    [PreserveSig] int put_AdditionalBrowserArguments(string value);
    [PreserveSig] int get_Language(out nint value);
    [PreserveSig] int put_Language(string value);
    [PreserveSig] int get_TargetCompatibleBrowserVersion(out nint value);
    [PreserveSig] int put_TargetCompatibleBrowserVersion(string value);
    [PreserveSig] int get_AllowSingleSignOnUsingOSPrimaryAccount(out int value);
    [PreserveSig] int put_AllowSingleSignOnUsingOSPrimaryAccount(int value);
}

/// <summary>环境选项 4：自定义 scheme 注册。本库的 <c>oriel://</c> 走这条路。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("ac52d13f-0d38-475a-9dca-876580d6793e")]
internal partial interface ICoreWebView2EnvironmentOptions4
{
    [PreserveSig] int GetCustomSchemeRegistrations(out int count, out nint registrations);

    [PreserveSig] int SetCustomSchemeRegistrations(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] registrations);
}

/// <summary>单个自定义 scheme 的注册描述。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d60ac92c-37a6-4b26-a39e-95cfe59047bb")]
internal partial interface ICoreWebView2CustomSchemeRegistration
{
    [PreserveSig] int get_SchemeName(out nint schemeName);
    [PreserveSig] int get_TreatAsSecure(out int treatAsSecure);
    [PreserveSig] int put_TreatAsSecure(int treatAsSecure);
    [PreserveSig] int GetAllowedOrigins(out int count, out nint allowedOrigins);
    [PreserveSig] int SetAllowedOrigins(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] allowedOrigins);
    [PreserveSig] int get_HasAuthorityComponent(out int hasAuthorityComponent);
    [PreserveSig] int put_HasAuthorityComponent(int hasAuthorityComponent);
}

/// <summary>
/// <see cref="ICoreWebView2EnvironmentOptions"/> 的托管实现，供 <c>new CoreWebView2EnvironmentOptions()</c> 使用。
/// </summary>
/// <remarks>
/// 这些值只在创建环境那一刻被原生侧读取，之后不再回调，所以用普通字段即可，
/// 不需要考虑跨线程一致性。
/// </remarks>
[GeneratedComClass]
internal sealed partial class CoreWebView2EnvironmentOptions : ICoreWebView2EnvironmentOptions, ICoreWebView2EnvironmentOptions4
{
    private string _additionalBrowserArguments = string.Empty;
    private string _language = string.Empty;
    private string? _targetCompatibleBrowserVersion;
    private bool _allowSingleSignOn;
    private readonly List<CoreWebView2CustomSchemeRegistration> _registrations = [];

    public string AdditionalBrowserArguments
    {
        get => _additionalBrowserArguments;
        set => _additionalBrowserArguments = value ?? string.Empty;
    }

    public string Language
    {
        get => _language;
        set => _language = value ?? string.Empty;
    }

    /// <summary>目标兼容的运行时版本。未显式设置时取当前已安装的运行时版本。</summary>
    /// <remarks>
    /// <para>
    /// 这个字段不能留空，也不该硬编码。实测（evergreen 运行时 155.0.4283.62）WebView2 的 loader
    /// 对它的判定比文档严格：
    /// </para>
    /// <list type="bullet">
    /// <item><c>null</c> 或 <c>""</c> → <c>E_INVALIDARG</c>（0x80070057，界面上就是
    /// "Value does not fall within the expected range"）；</item>
    /// <item>低于已安装运行时（如 <c>1.0.0.0</c>）→ 0x80070002，它按固定版本去装那个运行时，找不到；</item>
    /// <item>大于等于已安装运行时 → 正常走 evergreen。</item>
    /// </list>
    /// <para>
    /// 所以默认取"当前已安装版本"：既不把版本钉死（上游 WebView2Aot 是硬编码发布时的 SDK 版本，
    /// 1.6.1 里为 152.0.4191.47），也不引入会随环境漂移的魔数。
    /// </para>
    /// <para>
    /// 求值发生在 <c>get_TargetCompatibleBrowserVersion</c> 里，此刻 loader 必然已初始化
    /// ——正是它在调用本方法。
    /// </para>
    /// </remarks>
    public string TargetCompatibleBrowserVersion
    {
        get => _targetCompatibleBrowserVersion ??= ResolveRuntimeVersion();
        set => _targetCompatibleBrowserVersion = value;
    }

    private static string? s_runtimeVersion;

    private static string ResolveRuntimeVersion()
        => s_runtimeVersion ??= QueryRuntimeVersion();

    private static string QueryRuntimeVersion()
    {
        // 运行时没装时环境创建本来就会失败，这个取值只是保证字段合法（且不低于任何真实版本）。
        // 不能用低版本占位：那会走 0x80070002 那条"找不到固定版本"的路径，把真正的
        // "运行时缺失"错误盖成一句莫名其妙的文件不存在。
        const string Fallback = "999.0.0.0";

        if (!WebView2Utilities.IsInitialized)
        {
            return Fallback;
        }

        return WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString(null) is { Length: > 0 } version
            ? version
            : Fallback;
    }

    public bool AllowSingleSignOnUsingOSPrimaryAccount
    {
        get => _allowSingleSignOn;
        set => _allowSingleSignOn = value;
    }

    public int get_AdditionalBrowserArguments(out nint value) => MarshalString(_additionalBrowserArguments, out value);

    public int put_AdditionalBrowserArguments(string value)
    {
        _additionalBrowserArguments = value ?? string.Empty;
        return HResult.S_OK;
    }

    public int get_Language(out nint value) => MarshalString(_language, out value);

    public int put_Language(string value)
    {
        _language = value ?? string.Empty;
        return HResult.S_OK;
    }

    public int get_TargetCompatibleBrowserVersion(out nint value) => MarshalString(TargetCompatibleBrowserVersion, out value);

    public int put_TargetCompatibleBrowserVersion(string value)
    {
        _targetCompatibleBrowserVersion = value ?? string.Empty;
        return HResult.S_OK;
    }

    public int get_AllowSingleSignOnUsingOSPrimaryAccount(out int value)
    {
        value = _allowSingleSignOn ? 1 : 0;
        return HResult.S_OK;
    }

    public int put_AllowSingleSignOnUsingOSPrimaryAccount(int value)
    {
        _allowSingleSignOn = value != 0;
        return HResult.S_OK;
    }

    /// <summary>登记自定义 scheme（托管侧入口）。</summary>
    public void SetCustomSchemeRegistrations(params CoreWebView2CustomSchemeRegistration[] registrations)
    {
        _registrations.Clear();
        if (registrations is not null)
        {
            _registrations.AddRange(registrations);
        }
    }

    /// <summary>
    /// 原生侧在创建环境时读这份列表。
    /// </summary>
    /// <remarks>
    /// 数组用 <c>CoTaskMemAlloc</c> 分配、每个元素是 AddRef 过的接口指针——这正是
    /// WebView2 文档约定的所有权：调用方负责 <c>CoTaskMemFree</c> 数组并逐个 Release。
    /// 用托管数组或托管对象引用来"糊弄"这里会立刻在原生侧解引用野指针。
    /// </remarks>
    public unsafe int GetCustomSchemeRegistrations(out int count, out nint registrations)
    {
        count = _registrations.Count;
        registrations = 0;

        if (count == 0)
        {
            return HResult.S_OK;
        }

        nint* array = (nint*)Marshal.AllocCoTaskMem(sizeof(nint) * count);
        for (int i = 0; i < count; i++)
        {
            array[i] = (nint)ComInterfaceMarshaller<ICoreWebView2CustomSchemeRegistration>
                .ConvertToUnmanaged(_registrations[i]);
        }

        registrations = (nint)array;
        return HResult.S_OK;
    }

    public int SetCustomSchemeRegistrations(int count, nint[] registrations)
    {
        // 原生侧只读一次，这里不保留指针，只记下对象本身。
        _registrations.Clear();
        if (count > 0 && registrations is not null)
        {
            for (int i = 0; i < count; i++)
            {
                if (OrielComObject.FromPointer<ICoreWebView2CustomSchemeRegistration>(registrations[i]) is { } item)
                {
                    _registrations.Add(new CoreWebView2CustomSchemeRegistration(item));
                }
            }
        }

        return HResult.S_OK;
    }

    internal static int MarshalString(string? text, out nint value)
    {
        // 可选字符串的常规语义：null 与 "" 都交 null 指针，由原生侧按"未设置"处理。
        // 注意 TargetCompatibleBrowserVersion 不适用这条——它必须非空，见该属性上的说明。
        value = string.IsNullOrEmpty(text) ? 0 : Marshal.StringToCoTaskMemUni(text);
        return HResult.S_OK;
    }
}

/// <summary>
/// <see cref="ICoreWebView2CustomSchemeRegistration"/> 的托管实现。
/// </summary>
[GeneratedComClass]
internal sealed partial class CoreWebView2CustomSchemeRegistration : ICoreWebView2CustomSchemeRegistration
{
    private readonly string _schemeName;
    private bool _treatAsSecure;
    private bool _hasAuthorityComponent;
    private string[] _allowedOrigins = [];

    public CoreWebView2CustomSchemeRegistration(string schemeName)
    {
        _schemeName = schemeName ?? throw new ArgumentNullException(nameof(schemeName));
    }

    internal CoreWebView2CustomSchemeRegistration(IComObject<ICoreWebView2CustomSchemeRegistration> existing)
    {
        // 从原生对象反向构造：只用于把 SetCustomSchemeRegistrations 收到的指针留住。
        _schemeName = string.Empty;
        Existing = existing;
    }

    internal IComObject<ICoreWebView2CustomSchemeRegistration>? Existing { get; }

    public string SchemeName => _schemeName;

    public bool TreatAsSecure
    {
        get => _treatAsSecure;
        set => _treatAsSecure = value;
    }

    public bool HasAuthorityComponent
    {
        get => _hasAuthorityComponent;
        set => _hasAuthorityComponent = value;
    }

    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public void SetAllowedOrigins(params string[] origins) => _allowedOrigins = origins ?? [];

    public int get_SchemeName(out nint schemeName) => CoreWebView2EnvironmentOptions.MarshalString(_schemeName, out schemeName);

    public int get_TreatAsSecure(out int treatAsSecure)
    {
        treatAsSecure = _treatAsSecure ? 1 : 0;
        return HResult.S_OK;
    }

    public int put_TreatAsSecure(int treatAsSecure)
    {
        _treatAsSecure = treatAsSecure != 0;
        return HResult.S_OK;
    }

    public int GetAllowedOrigins(out int count, out nint allowedOrigins)
    {
        count = 0;
        allowedOrigins = 0;
        return HResult.S_OK;
    }

    public int SetAllowedOrigins(int count, nint[] allowedOrigins)
    {
        if (count <= 0 || allowedOrigins is null)
        {
            _allowedOrigins = [];
            return HResult.S_OK;
        }

        var values = new string[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = Marshal.PtrToStringUni(allowedOrigins[i]) ?? string.Empty;
        }

        _allowedOrigins = values;
        return HResult.S_OK;
    }

    public int get_HasAuthorityComponent(out int hasAuthorityComponent)
    {
        hasAuthorityComponent = _hasAuthorityComponent ? 1 : 0;
        return HResult.S_OK;
    }

    public int put_HasAuthorityComponent(int hasAuthorityComponent)
    {
        _hasAuthorityComponent = hasAuthorityComponent != 0;
        return HResult.S_OK;
    }
}
