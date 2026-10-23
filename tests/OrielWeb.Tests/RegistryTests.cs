using System.Text.Json;
using System.Text.Json.Serialization;
using OrielWeb.Ipc;
using Xunit;

// 跨命名空间的同简名类型：旧生成器用 MinimallyQualifiedFormat 取名字，两者都得到 "Dup"，
// 于是被 GroupBy 并入同一路由，TypeDisplay 只取 commands.First() → 其中一条命令指向错误类型。
// 现在改用完全限定名 + 稳定哈希，两者必须各自生成独立路由。
namespace OrielWeb.Tests.Ns1
{
    public sealed partial class Dup
    {
        [OrielCommand("dupns.ns1")]
        public string Which() => "ns1";
    }
}

namespace OrielWeb.Tests.Ns2
{
    public sealed partial class Dup
    {
        [OrielCommand("dupns.ns2")]
        public string Which() => "ns2";
    }
}

namespace OrielWeb.Tests
{
    /// <summary>
    /// 命令注册表**仍是**全局静态（注册发生在程序集加载期的 ModuleInitializer，这是刻意的设计），
    /// 因此这些用例与其它测试同集合串行——尤其是会往注册表里加伪路由的那个。
    /// JSON 上下文则已经不再是全局状态（按分发器实例持有）。
    /// </summary>
    [Collection("IpcSerial")]
    public sealed class RegistryTests
    {
        [Fact]
        public async Task SameSimpleNameInDifferentNamespaces_RoutesToCorrectTarget()
        {
            var factories = new Dictionary<Type, Func<object>>
            {
                [typeof(Ns1.Dup)] = () => new Ns1.Dup(),
                [typeof(Ns2.Dup)] = () => new Ns2.Dup(),
            };
            // 这两条命令都返回 string（基元），因此不需要 JSON 上下文
            var dispatcher = new OrielCommandDispatcher(factories, jsonContext: null, guard: TestGuards.AllowAll());

            var (first, _) = await TestHarness.DispatchAsync(dispatcher, "dupns.ns1", null);
            var (second, _) = await TestHarness.DispatchAsync(dispatcher, "dupns.ns2", null);

            Assert.Equal("ns1", first.Value().GetString());
            Assert.Equal("ns2", second.Value().GetString());
        }

        [Fact]
        public void AddRouter_DuplicateCommandName_ThrowsWithBothTypeNames()
        {
            OrielCommandRegistry.AddRouter(new DuplicateRouterA());

            var error = Assert.Throws<InvalidOperationException>(
                () => OrielCommandRegistry.AddRouter(new DuplicateRouterB()));

            Assert.Contains("dup.same", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(DuplicateRouterA), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(DuplicateRouterB), error.Message, StringComparison.Ordinal);
        }

        // ---- 2026-10-23 评审 P2：应用命令与内建精确名重合，注册期即拦 ----

        [Theory]
        [InlineData("window.close")]        // window.* 族（90 个精确名之一）
        [InlineData("fs.readTextFile")]     // fs.* 族：还要过路径 scope 的那一族
        [InlineData("event.emit")]          // event.* 族：@orielweb/api/event 依赖的内建名
        [InlineData("app.getName")]         // app.* 族（只读元信息也会遮蔽应用同名命令）
        public void AddRouter_BuiltInExactCommandName_ThrowsAtRegistration(string builtInName)
        {
            // 内建命令在分发器里**先于**应用路由解析：应用同名命令注册成功但永不执行，
            // 症状是"我明明写了这个命令，页面说找不到"，根因却在库的内建表里——
            // 编译期看不见这种重合（window./fs. 等前缀本身合法），只能在注册期拦。
            var error = Assert.Throws<InvalidOperationException>(
                () => OrielCommandRegistry.AddRouter(new SingleNameRouter(builtInName)));

            Assert.Contains(builtInName, error.Message, StringComparison.Ordinal);
            Assert.Contains("内建", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("win.anything")]        // win. 前缀：编译期 ORIELWEB106 负责，运行期放行
        [InlineData("tray.anything")]       // tray. 前缀：同上（知情压制 106 的路径必须还能走）
        [InlineData("window.myCustom")]     // 合法前缀 + 非内建精确名：完全合法的应用命令
        public void AddRouter_ReservedPrefixOrNonBuiltInName_IsAllowed(string commandName)
        {
            // 两层分工：**编译期管前缀（106），运行期管精确名**。
            // win./tray. 在这里放行是刻意的——ShadowWindowCommands 用 #pragma 压制 106
            // 保留"内建优先"的行为验证，运行期检查不得把它一并拦掉。
            var router = new SingleNameRouter(commandName);
            OrielCommandRegistry.AddRouter(router);

            // 注册成功：BuildIndex 能查到它（名字唯一，不会与既有注册冲突）。
            var index = OrielCommandRegistry.BuildIndex();
            Assert.True(index.ContainsKey(commandName));
        }
    }

    /// <summary>伪路由：只注册一个给定的命令名（供内建冲突/放行用例参数化使用）。</summary>
    internal sealed class SingleNameRouter(string name) : IOrielCommandRouter
    {
        public IReadOnlyList<string> CommandNames { get; } = [name];
        public Type? TargetType => null;
        public bool RequiresTarget => false;
        public int Route(string commandName) => commandName == name ? 0 : -1;
        public ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args, JsonSerializerContext? jsonContext, OrielEventSink? sink) => ValueTask.FromResult<object?>(null);
    }

    /// <summary>伪路由：注册命令名 "dup.same"（与 DuplicateRouterB 冲突）。</summary>
    internal sealed class DuplicateRouterA : IOrielCommandRouter
    {
        public IReadOnlyList<string> CommandNames { get; } = ["dup.same"];
        public Type? TargetType => null;
        public bool RequiresTarget => false;
        public int Route(string name) => name == "dup.same" ? 0 : -1;
        public ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args, JsonSerializerContext? jsonContext, OrielEventSink? sink) => ValueTask.FromResult<object?>(null);
    }

    /// <summary>伪路由：与 <see cref="DuplicateRouterA"/> 注册同名命令。</summary>
    internal sealed class DuplicateRouterB : IOrielCommandRouter
    {
        public IReadOnlyList<string> CommandNames { get; } = ["dup.same"];
        public Type? TargetType => null;
        public bool RequiresTarget => false;
        public int Route(string name) => name == "dup.same" ? 0 : -1;
        public ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args, JsonSerializerContext? jsonContext, OrielEventSink? sink) => ValueTask.FromResult<object?>(null);
    }
}
