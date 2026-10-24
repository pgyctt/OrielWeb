using OrielWeb;
using OrielWeb.Ipc;

namespace OrielConformance;

/// <summary>
/// 自检自己需要的两条命令：一条用来验"往返真的通"，一条给 fs 自检提供可写根目录。
/// </summary>
/// <remarks>
/// <para>
/// 它们原先借的是 demo 的 <c>todo.list</c> 与 <c>demo.fsRoot</c>。借用的代价是方向反了：
/// 自检（契约测试）依赖示例应用的业务命令，于是"改了 Todo 示例"会让自检失败，
/// 而失败信息指的是 Todo，不是契约。
/// </para>
/// <para>
/// 现在自检只依赖自己声明的命令。演示应用长什么样、有没有 Todo，与契约无关。
/// </para>
/// </remarks>
internal sealed class ConformanceCommands
{
    /// <summary>本族的前缀。能力名单按它整族放行（<c>conf.*</c>）。</summary>
    internal const string Prefix = "conf.*";

    internal const string ListCommand = "conf.list";
    internal const string FsRootCommand = "conf.fsRoot";

    /// <summary>
    /// fs 自检的可写根目录。
    /// </summary>
    /// <remarks>
    /// 用临时目录而不是应用数据目录：自检不该在用户的数据目录里留下垃圾，
    /// 而临时目录在三个平台上都有，不需要为它再写一份平台分支。
    /// 它同时**必须与能力声明里的 fs scope 一致**——不一致的话自检会以
    /// <c>path_not_allowed</c> 失败，而那个失败看起来像"scope 没生效"。
    /// </remarks>
    internal static string FsRootPath => Path.Combine(Path.GetTempPath(), "oriel-conformance-selftest");

    /// <summary>
    /// 返回一个数组，用来验 <c>core.invoke</c> 的 JSON 往返。
    /// </summary>
    /// <remarks>
    /// 刻意返回数组而不是字符串：数组往返会同时经过"序列化集合"与"页面侧 JSON.parse"
    /// 两段，而字符串只覆盖后者。返回空列表是因为这条断言只关心形状与可达性。
    /// </remarks>
    [OrielCommand(ListCommand)]
    public static string[] List() => [];

    [OrielCommand(FsRootCommand)]
    public static string FsRoot() => FsRootPath;
}
