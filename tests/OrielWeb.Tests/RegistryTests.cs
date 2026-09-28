using System.Text.Json;
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
    /// <summary>只有命令名冲突检测不共享 OrielJson 全局上下文，但注册表是全局静态，故同集合串行。</summary>
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
            var dispatcher = new OrielCommandDispatcher(factories);

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
    }

    /// <summary>伪路由：注册命令名 "dup.same"（与 DuplicateRouterB 冲突）。</summary>
    internal sealed class DuplicateRouterA : IOrielCommandRouter
    {
        public IReadOnlyList<string> CommandNames { get; } = ["dup.same"];
        public Type? TargetType => null;
        public bool RequiresTarget => false;
        public int Route(string name) => name == "dup.same" ? 0 : -1;
        public ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args) => ValueTask.FromResult<object?>(null);
    }

    /// <summary>伪路由：与 <see cref="DuplicateRouterA"/> 注册同名命令。</summary>
    internal sealed class DuplicateRouterB : IOrielCommandRouter
    {
        public IReadOnlyList<string> CommandNames { get; } = ["dup.same"];
        public Type? TargetType => null;
        public bool RequiresTarget => false;
        public int Route(string name) => name == "dup.same" ? 0 : -1;
        public ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args) => ValueTask.FromResult<object?>(null);
    }
}
