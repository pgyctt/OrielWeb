using System.Runtime.InteropServices;

namespace OrielWeb.Platform.MacOS.Interop;

/// <summary>
/// 最小的 Objective-C block：一个**全局的空操作 block**，用来给"完成后必须回调一下"的
/// WebKit / AppKit API 传一个真有东西的回调。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：<c>-removeDataOfTypes:modifiedSince:completionHandler:</c> 之类
/// 的 API 把 completionHandler 标成可空，但 WebKit 的实现会**无条件调用**它。
/// 传 <c>nil</c> 的后果不是"不回调"，而是在
/// <c>block-&gt;invoke</c>（偏移 0x10）上取指令——真机 CI 上就是
/// <c>SIGSEGV KERN_INVALID_ADDRESS at 0x0000000000000010</c>，进程直接没了。
/// 所以"我不关心完成通知"的正确表达是**传一个什么都不做的 block**，而不是传 nil。
/// </para>
/// <para>
/// <b>为什么是"全局"block</b>：libclosure 把 block 分成栈 / 堆 / 全局三种。全局 block
/// 是只读常量：<c>_Block_copy</c> 直接返回同一个指针（不分配），<c>_Block_release</c>
/// 直接返回（不释放）。于是它可以是一个**进程生命期内的单例**——不用管谁持有、谁释放，
/// 也不会因为调用方异步回调时我们已经释放了而炸。
/// </para>
/// <para>
/// <b>布局是 ABI，不是实现细节</b>：<c>Block_layout</c> 的字段顺序被 libclosure 钉死
/// （isa / flags / reserved / invoke / descriptor），任何改动都会让调用跳飞。这里的偏移
/// 与 clang 生成的 <c>^ { }</c> 逐字节一致（64 位）。
/// </para>
/// </remarks>
internal static unsafe class ObjCBlock
{
    /// <summary>libclosure 的 <c>Block_layout</c>（64 位）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLayout
    {
        /// <summary><c>isa</c>：指向 <c>_NSConcreteGlobalBlock</c>。</summary>
        public nint Isa;

        /// <summary><c>flags</c>：这里只用 <c>BLOCK_IS_GLOBAL</c>。</summary>
        public int Flags;

        /// <summary><c>reserved</c>：保留位。</summary>
        public int Reserved;

        /// <summary><c>invoke</c>：block 被调用时跳的函数指针（**偏移 0x10**）。</summary>
        public nint Invoke;

        /// <summary><c>descriptor</c>：大小与拷贝辅助（全局 block 用不到拷贝辅助）。</summary>
        public nint Descriptor;
    }

    /// <summary>libclosure 的 <c>Block_descriptor_1</c>（没有捕获、没有签名的最小形态）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        /// <summary>保留位（libclosure 要求 0）。</summary>
        public nuint Reserved;

        /// <summary>block 结构自身的大小。</summary>
        public nuint Size;
    }

    /// <summary><c>BLOCK_IS_GLOBAL</c>（<c>1 &lt;&lt; 28</c>）：告诉运行时"这是常量，别拷贝也别释放"。</summary>
    private const int BlockIsGlobal = 1 << 28;

    /// <summary>已建好的那一份 block（0 = 还没建）。进程生命期内不释放。</summary>
    private static nint s_block;

    /// <summary>
    /// 一个 <c>void (^)(void)</c> 形状的全局空 block；可以反复传给任何同形状的参数。
    /// </summary>
    /// <remarks>
    /// 建不出来时抛 <see cref="NotSupportedException"/> 而不是返回 0——返回 0 等于回到
    /// "传 nil 然后被 WebKit 调飞"的老路上，那种失败没有诊断可言。
    /// </remarks>
    internal static nint NoOp
    {
        get
        {
            nint existing = Volatile.Read(ref s_block);
            if (existing != 0)
            {
                return existing;
            }

            nint isa = ObjCRuntime.dlsym(ObjCRuntime.RtldDefault, "_NSConcreteGlobalBlock");
            if (isa == 0)
            {
                // 备用路径：`_NSConcreteGlobalBlock` 就是类 `__NSGlobalBlock__` 的对象本身，
                // 所以从 ObjC 运行时也能拿到同一个地址。两条路都走不通才放弃。
                isa = ObjCRuntime.objc_getClass("__NSGlobalBlock__");
            }

            if (isa == 0)
            {
                throw new NotSupportedException(
                    "ObjCBlock: 拿不到 _NSConcreteGlobalBlock（libclosure 未加载？）。"
                    + "没有它就不能构造全局 block，相关 API 只能放弃调用。");
            }

            var descriptor = NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            var layout = NativeMemory.AllocZeroed((nuint)sizeof(BlockLayout));

            try
            {
                var descriptorView = (BlockDescriptor*)descriptor;
                descriptorView->Reserved = 0;
                descriptorView->Size = (nuint)sizeof(BlockLayout);

                var layoutView = (BlockLayout*)layout;
                layoutView->Isa = isa;
                layoutView->Flags = BlockIsGlobal;
                layoutView->Reserved = 0;
                layoutView->Invoke = (nint)(delegate* unmanaged<nint, void>)&InvokeNoOp;
                layoutView->Descriptor = (nint)descriptor;
            }
            catch (Exception)
            {
                NativeMemory.Free(layout);
                NativeMemory.Free(descriptor);
                throw;
            }

            nint winner = Interlocked.CompareExchange(ref s_block, (nint)layout, 0);
            if (winner != 0)
            {
                // 并发下另一个线程先建好了：用它的，把自己这份还回去
                NativeMemory.Free(layout);
                NativeMemory.Free(descriptor);
                return winner;
            }

            return (nint)layout;
        }
    }

    /// <summary>空操作 block 的实现：什么都不做，只是"通知一声"。</summary>
    /// <remarks>
    /// 签名必须是 <c>void (*)(void *block)</c>：调用方传进来的第一个参数是 block 自身，
    /// 没有别的参数——这正是 <c>void (^)(void)</c> 的 ABI 形态。
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void InvokeNoOp(nint block) => _ = block;
}
