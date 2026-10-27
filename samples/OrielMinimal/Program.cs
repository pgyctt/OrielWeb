using System.Reflection;
using OrielWeb;

// OrielMinimal —— 从 NuGet 包消费的最小可运行样例。
//
// 它同时承担两件事，缺一不可：
//
//   1. **图形界面**：一个窗口、一个内嵌页面，显示 "Hello OrielWeb!"。
//      这是"从包消费"链路的端到端证明——页面资源不是拷到输出目录的散文件，
//      而是由包内 buildTransitive/OrielWeb.targets 嵌进程序集，运行期经 oriel:// 提供。
//
//   2. **无头自检**：带 --list-resources 时只打印内嵌资源名、不建窗。
//      tools/verify-package.ps1 靠它断言 LogicalName 的形状：
//        含点文件名（app.min.js）必须落在 wwwroot/app.min.js，不能被反推成 wwwroot.app.min.js；
//        嵌套目录（assets/img/logo.svg）必须保留 '/'。
//      建窗要图形会话，CI 上没有，所以自检通道必须能无头跑。
//
// 这个工程刻意**不进 OrielWeb.slnx**：它引用的是 NuGet 包而不是项目，
// 需要先 dotnet pack 出本地包才能还原。驱动它的是 tools/verify-package.ps1。

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 自检通道：不初始化 UI，也不碰 WebView2。
        if (args.Contains("--list-resources"))
        {
            ListEmbeddedResources();
            return 0;
        }

        Oriel.CreateBuilder(args)
            // 未设置 WithUrl 时加载内嵌首页：oriel://app.oriel/index.html
            .UseEmbeddedAssets()
            // 标识符会成为用户数据目录的一部分；各示例各用一个，避免共用 WebView2 profile。
            .WithAppInfo(name: "Oriel Minimal", identifier: "com.orielweb.samples.minimal")
            .AddWindow(w => w
                .WithTitle("OrielMinimal")
                .WithSize(720, 480)
                .WithMinSize(420, 300)
                .Centered())
            .Run();

        return 0;
    }

    /// <summary>
    /// 打印本程序集的内嵌资源名。
    /// </summary>
    /// <remarks>
    /// 资源名就是库在 scheme 处理器里查表用的相对路径的来源
    /// （<c>OrielMinimal.wwwroot/app.min.js</c> → <c>app.min.js</c>）。
    /// 只打印、不真的加载：这里要验的是**包内 targets 产出的 LogicalName**，
    /// 无头、确定性、CI 可跑。
    /// </remarks>
    private static void ListEmbeddedResources()
    {
        foreach (string name in Assembly.GetExecutingAssembly()
                     .GetManifestResourceNames()
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            Console.WriteLine(name);
        }
    }
}
