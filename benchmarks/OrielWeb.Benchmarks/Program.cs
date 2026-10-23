using BenchmarkDotNet.Running;

namespace OrielWeb.Benchmarks;

/// <summary>
/// 基准入口。
/// </summary>
/// <remarks>
/// 用法：
/// <code>
/// dotnet run -c Release --project benchmarks/OrielWeb.Benchmarks -- --filter '*'
/// dotnet run -c Release --project benchmarks/OrielWeb.Benchmarks -- --filter '*Invoke*'
/// </code>
/// 这个项目**不在** <c>OrielWeb.slnx</c> 里：基准跑一次要几分钟，不该拖慢每次
/// <c>dotnet build</c> 与 <c>dotnet format</c>。CI 单独一个 job 跑它（见 .github/workflows/ci.yml）。
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
