using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS;
using OrielWeb.Platform.MacOS.Interop;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// macOS 互操作层的**签名/所有权契约**断言（2026-11-11 评审 M6 / H4 的回归闸）。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例全部跑在 Windows 上：它们检查的是"托管侧声明的形状"对不对，不触碰任何原生库。
/// 之所以值得测——M6 与 H4 的共同点是**编译期完全合法、只有真机上才出事**：
/// BOOL 用 <c>bool</c> 接、release 回调传 <c>0</c>，两种写法都能通过编译与既有单测。
/// 把"声明形状"钉成断言，是这类缺陷在本机能拿到的最强证据。
/// </para>
/// <para>
/// <b>不能证明的部分</b>：真机上 BOOL 高位是否真有垃圾、CoreGraphics 是否真的会回调
/// release 函数、NSImage 是否延迟解码。见文件末尾的"待真机验证"清单。
/// </para>
/// </remarks>
public sealed class MacOSInteropContractTests
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // ---- T8（M6）：ObjC BOOL 返回值一律 int + 判非零 ----

    [Theory]
    [InlineData("SendIntRet", 2)]
    [InlineData("SendIntRetObj", 3)]
    [InlineData("SendIntRetObjObjObj", 5)]
    public void ObjCBoolReturningMessages_AreDeclaredAsInt(string methodName, int parameterCount)
    {
        var method = typeof(ObjCRuntime).GetMethod(methodName, AnyStatic);
        Assert.NotNull(method);

        // 返回值必须是 int：BOOL 在 macOS 上是 signed char，只保证低字节有效。
        // 用 bool（[MarshalAs(UnmanagedType.Bool)]）接等于在声明里断言"返回 4 字节"，
        // 契约本身就是错的；这个断言挡住"有人图省事改回 bool"。
        Assert.Equal(typeof(int), method!.ReturnType);
        Assert.Equal(parameterCount, method.GetParameters().Length);

        // 返回位上不允许再出现 MarshalAs(UnmanagedType.Bool)：那正是被整改掉的写法
        var returnMarshal = method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
        Assert.Null(returnMarshal);
    }

    [Fact]
    public void ObjCBoolReturningMessages_AreEntryPointedAtObjcMsgSend()
    {
        // 三个签名共用同一个导出，靠 EntryPoint 指向 objc_msgSend。名字写错会静默变成
        // "找不到入口点"的 DllNotFoundException——只在 macOS 上炸。
        foreach (string name in new[] { "SendIntRet", "SendIntRetObj", "SendIntRetObjObjObj" })
        {
            var method = typeof(ObjCRuntime).GetMethod(name, AnyStatic);
            Assert.NotNull(method);
            Assert.All(
                method!.GetCustomAttributes<DllImportAttribute>(),
                import => Assert.Equal("objc_msgSend", import.EntryPoint));
        }
    }

    [Fact]
    public void NoObjCBoolReturningDeclarationUsesManagedBool()
    {
        // 全类型扫描：任何返回 bool 的 objc_msgSend 声明都是 M6 的复发。
        // 写死方法名列表挡不住"新加一个 SendBoolRetXxx"，所以按特征扫。
        var offenders = typeof(ObjCRuntime)
            .GetMethods(AnyStatic)
            .Where(m => m.ReturnType == typeof(bool))
            .Where(m => m.GetCustomAttributes<DllImportAttribute>()
                .Any(a => a.EntryPoint == "objc_msgSend"))
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    // ---- T10（H4）：provider 的 release 回调与 pin 所有权 ----

    [Fact]
    public void CGDataProviderCreateWithData_TakesAFunctionPointerReleaseCallback()
    {
        var method = typeof(ObjCRuntime).GetMethod("CGDataProviderCreateWithData", AnyStatic);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(4, parameters.Length);

        // 前三个槽是 info / data / size
        Assert.Equal(typeof(nint), parameters[0].ParameterType);
        Assert.Equal(typeof(nint), parameters[1].ParameterType);
        Assert.Equal(typeof(nuint), parameters[2].ParameterType);

        // 第四槽必须是函数指针（不是 nint）：CGDataProviderReleaseDataCallback 是
        // void (*)(void *, const void *, size_t)。用 nint 接的话"传 0"能编译，
        // 于是又退回"永不回调"的旧行为——这条断言把类型钉死。
        Assert.True(
            parameters[3].ParameterType.IsFunctionPointer,
            "releaseData 必须是函数指针，否则调用点可以传 0 把 pin 泄漏回去");

        var signature = parameters[3].ParameterType;
        Assert.Equal(typeof(void), signature.GetFunctionPointerReturnType());

        var callbackArgs = signature.GetFunctionPointerParameterTypes();
        Assert.Equal(3, callbackArgs.Length);
        Assert.Equal(typeof(nint), callbackArgs[0]);   // void* info（GCHandle 的整数形式）
        Assert.Equal(typeof(nint), callbackArgs[1]);   // const void* data
        Assert.Equal(typeof(nuint), callbackArgs[2]);  // size_t size
    }

    [Fact]
    public void ReleasePixelData_IsAnUnmanagedCallersOnlyCallback()
    {
        var method = typeof(MacOSImageFactory).GetMethod("ReleasePixelData", AnyStatic);
        Assert.NotNull(method);

        // 原生直接调用的函数指针：必须是 [UnmanagedCallersOnly]（且不能是实例方法/泛型）。
        // 少了这个特性，取 &Method 在 Native AOT 下编译不过；而在 JIT 下取到的是
        // 一个会走托管封送的 thunk，签名对不上就是栈错乱。
        Assert.NotNull(method!.GetCustomAttribute<UnmanagedCallersOnlyAttribute>());
        Assert.True(method.IsStatic);
        Assert.Equal(typeof(void), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(nint), parameters[0].ParameterType);
        Assert.Equal(typeof(nint), parameters[1].ParameterType);
        Assert.Equal(typeof(nuint), parameters[2].ParameterType);
    }

    [Fact]
    public void ReleasePinnedPixels_FreesTheGCHandleGivenAsContext()
    {
        // 非 macOS 也能验的部分：release 回调的"实际动作"就是解掉 context 里的 GCHandle。
        // 判据不能是"再 Free 一次会抛"——实测 GCHandle.Free 不校验重复释放（见下一条用例），
        // 所以这里用更硬的证据：解钉之后放弃强引用，数组必须能被 GC 回收；
        // 若 pin 还在，弱引用会一直存活。
        WeakReference weak = PinAndReleaseViaCallback();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive, "release 回调之后数组仍被 pin 住，说明 context 没有被解钉");
    }

    /// <summary>
    /// 在独立方法里造 pin、交给 release 回调实现、然后丢掉全部强引用。
    /// </summary>
    /// <remarks>
    /// 必须 <see cref="MethodImplOptions.NoInlining"/>：内联进调用方时，JIT 可能把数组的存活期
    /// 延长到方法结束之后，弱引用断言就会随机红——那不是缺陷，是测试自己保活了对象。
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PinAndReleaseViaCallback()
    {
        byte[] pixels = new byte[64];
        var weak = new WeakReference(pixels);
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        // 与真机路径同形：context 就是 GCHandle 的整数形式
        Assert.True(MacOSImageFactory.ReleasePinnedPixels(GCHandle.ToIntPtr(handle)));
        return weak;
    }

    [Fact]
    public void ReleasePinnedPixels_IgnoresZeroContext()
    {
        // CoreGraphics 在某些失败路径上可能带着 0 info 回调（也用于占位），不能因此抛异常：
        // 从原生帧抛出去就是进程 fail-fast。
        Assert.False(MacOSImageFactory.ReleasePinnedPixels(0));
    }

    [Fact]
    public void ReleasePinnedPixels_DoesNotThrowWhenCalledTwice()
    {
        // 原生回调可能因为"provider 被两条释放路径各放一次"之类的原因重复触发。
        // 硬契约是**绝不抛**（从原生帧抛托管异常 = 进程 fail-fast）。
        // 实测 GCHandle.Free 不检测重复释放，所以第二次调用不会给出可用的失败信号；
        // 这也正是实现里不承诺幂等、注释里要求"只调一次"的原因。
        byte[] pixels = new byte[8];
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        nint context = GCHandle.ToIntPtr(handle);

        Assert.True(MacOSImageFactory.ReleasePinnedPixels(context));

        // 这里不断言返回值：Free 的重复调用行为由运行时决定，断言它等于把测试绑到实现细节上。
        MacOSImageFactory.ReleasePinnedPixels(context);
    }

    // ---- 待真机验证（本机无法断言，列出以免被误读成"已验证"）----
    //
    // 1. BOOL 的高位是否真的带垃圾：M6 的整改把"用 bool 接"改成了"用 int 接并判非零"，
    //    后者不依赖 ABI 对 1 字节返回值的清零约定。要彻底按 1 字节读需改用 sbyte，
    //    属另一次评估（见 ObjCRuntime.SendIntRet 的注释）。
    // 2. CoreGraphics 是否在 CGImage/NSImage 全部释放后回调 ReleasePixelData（本机无 CoreGraphics）。
    // 3. NSImage initWithCGImage:size: 是否即时拷贝像素（H4 的触发条件），
    //    以及延迟解码下图标是否损坏——需真机建图标 → GC.Collect + 压实 → 截图比对。
    // 4. 重复创建/销毁图标时 provider 回调计数与 pin 计数是否配平（无句柄泄漏）。
}
