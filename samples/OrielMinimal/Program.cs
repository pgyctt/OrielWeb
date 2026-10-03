using System.Reflection;

// 打印本程序集的内嵌资源名。
//
// 这是对"包内 build logic 是否生效"的直接断言点：资源名就是库在 scheme 处理器里查表用的相对路径的来源
// （OrielMinimal.wwwroot/app.min.js → app.min.js），而"资源名 → 相对路径"的映射
// 由 tests/OrielWeb.Tests/EmbeddedAssetTests.cs 的用例覆盖。
//
// 之所以只打印资源名而不真的加载：建窗要经 OrielApp.Run()——那需要图形会话。
// 这里要验的是**包内 targets 产出的 LogicalName**，无头、确定性、CI 可跑。
//
// tools/verify-pack.ps1 会断言输出里：
//   · 有 OrielMinimal.wwwroot/app.min.js（含点文件名走对了路）
//   · 有 OrielMinimal.wwwroot/assets/img/logo.svg（嵌套目录）
//   · 没有 OrielMinimal.wwwroot.app.min.js（按 '.' 反推路径那条错路；该写法已删除）

foreach (string name in Assembly.GetExecutingAssembly()
             .GetManifestResourceNames()
             .OrderBy(n => n, StringComparer.Ordinal))
{
    Console.WriteLine(name);
}
