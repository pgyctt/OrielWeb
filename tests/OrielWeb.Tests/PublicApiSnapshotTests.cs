using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 公开 API 快照：任何新增、删除或改签名的公开成员都会让这条用例红。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接用 <c>PublicApiAnalyzers</c></b>：试过。它把"未登记的公开成员"报成
/// <c>RS0016</c>，而本仓库 <c>src/</c> 开了 <c>TreatWarningsAsErrors</c>，于是新加一个公开方法
/// 就直接构建失败；要恢复得先跑 <c>dotnet format</c> 把 1600+ 条一次性灌进
/// <c>PublicAPI.Unshipped.txt</c>。0.x 阶段 API 还在长，这个摩擦力大于收益。
/// </para>
/// <para>
/// <b>这个测试关心什么</b>：不是"API 长什么样"，而是**它有没有被悄悄改掉**。
/// 0.x 允许破坏性变更，但必须是有意的——删掉一个公开方法时，改动者应当看见这行失败，
/// 而不是让某个下游在升级后才发现 <c>MissingMethodException</c>。
/// </para>
/// <para>
/// <b>怎么更新</b>：失败时会把当前 API 写到输出目录的 <c>PublicApi.actual.txt</c>。
/// 确认差异是有意的，把它复制成 <c>PublicApi.snapshot.txt</c> 并提交。
/// 快照用 LF 与 Ordinal 排序，跨平台差异不会造成假红。
/// </para>
/// </remarks>
public sealed class PublicApiSnapshotTests
{
    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "PublicApi.snapshot.txt");

    private static readonly string ActualPath =
        Path.Combine(AppContext.BaseDirectory, "PublicApi.actual.txt");

    [Fact]
    public void PublicApi_MatchesSnapshot()
    {
        string actual = string.Join("\n", DescribeApi().Order(StringComparer.Ordinal));

        if (!File.Exists(SnapshotPath))
        {
            File.WriteAllText(ActualPath, actual);
            Assert.Fail(
                $"快照不存在。当前 API 已写到 {ActualPath}——" +
                "确认无误后复制为 PublicApi.snapshot.txt 并提交。");
        }

        string expected = File.ReadAllText(SnapshotPath).ReplaceLineEndings("\n").TrimEnd('\n');
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(ActualPath, actual);

        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        string[] removed = [.. expectedLines.Except(actualLines, StringComparer.Ordinal).Take(20)];
        string[] added = [.. actualLines.Except(expectedLines, StringComparer.Ordinal).Take(20)];

        Assert.Fail(
            "公开 API 与快照不一致。若变更是有意的，把输出目录的 PublicApi.actual.txt " +
            "复制为 PublicApi.snapshot.txt 并提交。\n" +
            $"删除/改动（{removed.Length} 条，最多显示 20）：\n  " + string.Join("\n  ", removed) + "\n" +
            $"新增（{added.Length} 条，最多显示 20）：\n  " + string.Join("\n  ", added));
    }

    /// <summary>列出声明的公开 API：类型一行，每个公开成员一行。</summary>
    // 反射是这些断言的**手段**本身：测试程序集不参与 AOT/裁剪发布，
    // 而 GetExportedTypes() / GetMembers() 带 RequiresUnreferencedCode 与 trim 注解，
    // 逐层传播要求会污染整条调用链。
    [UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "测试专用反射，测试程序集不参与 AOT/裁剪发布。")]
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "测试专用反射，测试程序集不参与 AOT/裁剪发布。")]
    private static IEnumerable<string> DescribeApi()
    {
        Assembly assembly = typeof(OrielApp).Assembly;

        const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in assembly.GetExportedTypes())
        {
            if (type.FullName is null || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            {
                continue;
            }

            yield return $"type {type.FullName}";

            foreach (MemberInfo member in type.GetMembers(MemberFlags))
            {
                // 属性/事件的访问器（get_ / set_ / add_ / remove_）不单独记：
                // PropertyInfo / EventInfo 本身已经在列表里，重复记只会让快照噪音翻倍。
                if (member is MethodInfo { IsSpecialName: true } or ConstructorInfo { IsSpecialName: true })
                {
                    continue;
                }

                yield return $"  {type.FullName} :: {DescribeMember(member)}";
            }
        }
    }

    private static string DescribeMember(MemberInfo member) => member switch
    {
        ConstructorInfo ctor => $"ctor({DescribeParameters(ctor.GetParameters())})",
        MethodInfo method =>
            $"{method.Name}({DescribeParameters(method.GetParameters())}) -> {method.ReturnType}",
        PropertyInfo property =>
            $"{property.Name} : {property.PropertyType} (get={property.GetMethod is not null}, set={property.SetMethod is not null})",
        FieldInfo field => $"{field.Name} : {field.FieldType}",
        EventInfo @event => $"event {@event.Name} : {@event.EventHandlerType}",
        _ => member.ToString() ?? member.Name,
    };

    private static string DescribeParameters(ParameterInfo[] parameters)
        => string.Join(
            ", ",
            parameters.Select(p => $"{p.ParameterType}{(p.IsOptional ? " = optional" : string.Empty)}"));
}
