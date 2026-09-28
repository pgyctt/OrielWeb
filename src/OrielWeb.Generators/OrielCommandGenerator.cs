using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#nullable enable

namespace OrielWeb.Generators;

/// <summary>
/// 扫描 [OrielCommand("name")] 方法，为每个包含命令的类型生成 ICommandRouter
/// （switch O(1) 分发 + 参数强类型提取），并在程序集内生成 [ModuleInitializer]
/// 自动注册——运行期零反射，Native AOT 安全。
/// </summary>
[Generator]
public sealed class OrielCommandGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var commands = context.SyntaxProvider.ForAttributeWithMetadataName(
            "OrielWeb.Ipc.OrielCommandAttribute",
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (context, token) => Transform(context, token));

        context.RegisterSourceOutput(commands.Collect(), static (context, models) => Emit(context, models));
    }

    // ------------------------------------------------------------------
    // 模型
    // ------------------------------------------------------------------

    private sealed record CommandParameter(string Name, string TypeDisplay, bool IsOptional, bool IsJsonElementArgs);

    private sealed record CommandModel(
        string CommandName,
        string TypeDisplay,
        string TypeNameSafe,
        string MethodName,
        bool IsStatic,
        bool NeedsAwait,
        bool HasResult,
        ImmutableArray<CommandParameter> Parameters);

    private static CommandModel? Transform(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        var method = (IMethodSymbol)context.TargetSymbol;
        var attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not string commandName
            || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var containingType = method.ContainingType;
        string typeDisplay = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string typeSafe = MakeSafeIdentifier(containingType);

        var parameters = method.Parameters
            .Select(p =>
            {
                var display = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                bool isJsonElementArgs = display == "global::System.Text.Json.JsonElement";
                // 可空性必须读符号注解：FullyQualifiedFormat 对引用类型的 '?' 不显示
                bool isOptional = p.Type.NullableAnnotation == NullableAnnotation.Annotated;
                return new CommandParameter(p.Name, display, isOptional, isJsonElementArgs);
            })
            .ToImmutableArray();

        // 返回类型解包：Task<T> / ValueTask<T> → await + 有结果；Task / ValueTask → await 无结果；void → 无结果
        string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        bool needsAwait;
        bool hasResult;
        if (returnType == "global::System.Threading.Tasks.Task"
            || returnType == "global::System.Threading.Tasks.ValueTask")
        {
            needsAwait = true;
            hasResult = false;
        }
        else if (returnType.StartsWith("global::System.Threading.Tasks.Task<", StringComparison.Ordinal)
            || returnType.StartsWith("global::System.Threading.Tasks.ValueTask<", StringComparison.Ordinal))
        {
            needsAwait = true;
            hasResult = true;
        }
        else
        {
            needsAwait = false;
            hasResult = method.ReturnsVoid ? false : true;
        }

        return new CommandModel(
            commandName,
            typeDisplay,
            typeSafe,
            method.Name,
            method.IsStatic,
            needsAwait,
            hasResult,
            parameters);
    }

    /// <summary>
    /// 把类型名转成可用作 C# 标识符的稳定字符串。
    /// </summary>
    private static string MakeSafeIdentifier(INamedTypeSymbol type)
    {
        // 用完全限定名而非 MinimallyQualifiedFormat：后者会因命名空间不同而碰撞，
        // 且泛型/嵌套类型名会带 '<' '>' '.' '+' 等非法标识符字符。
        var fullyQualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var builder = new StringBuilder(fullyQualified.Length + 9);
        foreach (var c in fullyQualified)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        // 追加确定性短哈希：'Ns.A.B' 与 'Ns.A_B' 净化后同名，若不区分会被下面的
        // GroupBy 并入同一路由，而 TypeDisplay 取自 commands.First() → 命令指向错误类型。
        builder.Append('_');
        builder.Append(StableHash(fullyQualified));
        return builder.ToString();
    }

    /// <summary>FNV-1a 32 位：确定性且跨进程稳定（string.GetHashCode() 不满足后者）。</summary>
    private static string StableHash(string text)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // 产出
    // ------------------------------------------------------------------

    private static void Emit(SourceProductionContext context, ImmutableArray<CommandModel?> models)
    {
        var valid = models
            .Where(m => m is not null)
            .Select(m => m!)
            .GroupBy(m => m.TypeNameSafe)
            .ToImmutableList();

        if (valid.IsEmpty)
        {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/> 由 OrielWeb.Generators 生成：零反射 IPC 路由（勿手改） </auto-generated>");
        source.AppendLine("#nullable enable");
        source.AppendLine("namespace OrielWeb.Generated");
        source.AppendLine("{");

        // 模块初始化器：程序集加载即注册全部路由
        source.AppendLine("    internal static class OrielModuleInit");
        source.AppendLine("    {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Register()");
        source.AppendLine("        {");
        foreach (var group in valid)
        {
            source.AppendLine($"            global::OrielWeb.Ipc.OrielCommandRegistry.AddRouter(new {group.Key}_Router());");
        }
        source.AppendLine("        }");
        source.AppendLine("    }");

        int globalIndex = 0;
        foreach (var group in valid)
        {
            EmitRouter(source, group.Key, group, ref globalIndex);
        }

        source.AppendLine("}");

        context.AddSource("OrielWeb.Generated.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void EmitRouter(StringBuilder source, string typeSafeName, IGrouping<string, CommandModel> commands, ref int globalIndex)
    {
        var model = commands.First();
        bool requiresTarget = commands.Any(c => !c.IsStatic);

        source.AppendLine();
        source.AppendLine($"    internal sealed class {typeSafeName}_Router : global::OrielWeb.Ipc.IOrielCommandRouter");
        source.AppendLine("    {");
        source.AppendLine($"        public global::System.Type? TargetType => {(requiresTarget ? $"typeof({model.TypeDisplay})" : "null")};");
        source.AppendLine($"        public bool RequiresTarget => {requiresTarget.ToString().ToLowerInvariant()};");
        source.AppendLine();
        // 静态命令名表：供 OrielCommandRegistry 建立全局索引与同名冲突检测
        source.AppendLine("        private static readonly global::System.String[] s_names =");
        source.AppendLine("        [");
        foreach (var command in commands)
        {
            source.AppendLine($"            \"{Escape(command.CommandName)}\",");
        }
        source.AppendLine("        ];");
        source.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::System.String> CommandNames => s_names;");
        source.AppendLine();

        source.AppendLine("        public int Route(global::System.String name)");
        source.AppendLine("        {");
        source.AppendLine("            switch (name)");
        source.AppendLine("            {");
        foreach (var command in commands)
        {
            source.AppendLine($"                case \"{Escape(command.CommandName)}\": return {globalIndex};");
            globalIndex++;
        }
        source.AppendLine("                default: return -1;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();

        source.AppendLine("        public async global::System.Threading.Tasks.ValueTask<object?> InvokeAsync(int index, object? target, global::System.Text.Json.JsonElement args)");
        source.AppendLine("        {");
        source.AppendLine("            switch (index)");
        source.AppendLine("            {");

        int localIndex = globalIndex - commands.Count();
        foreach (var command in commands)
        {
            source.AppendLine($"                case {localIndex}: // {command.CommandName}");
            localIndex++;

            // 单一作用域块：参数提取与调用同域
            source.AppendLine("                {");

            foreach (var parameter in command.Parameters)
            {
                if (parameter.IsJsonElementArgs)
                {
                    source.AppendLine($"                    var __arg_{parameter.Name} = args;");
                    continue;
                }
                if (parameter.IsOptional)
                {
                    source.AppendLine($"                    var __arg_{parameter.Name} = global::OrielWeb.Ipc.OrielJson.GetOptionalArg<{parameter.TypeDisplay}>(args, \"{Escape(parameter.Name)}\");");
                }
                else
                {
                    source.AppendLine($"                    var __arg_{parameter.Name} = global::OrielWeb.Ipc.OrielJson.GetRequiredArg<{parameter.TypeDisplay}>(args, \"{Escape(parameter.Name)}\");");
                }
            }

            string arguments = string.Join(", ", command.Parameters.Select(p => $"__arg_{p.Name}"));
            string receiver = command.IsStatic
                ? $"{command.TypeDisplay}.{command.MethodName}({arguments})"
                : $"(({command.TypeDisplay})target!).{command.MethodName}({arguments})";

            if (command.HasResult && command.NeedsAwait)
            {
                source.AppendLine($"                    var __result = await {receiver}.ConfigureAwait(false);");
                source.AppendLine("                    return __result;");
            }
            else if (command.HasResult)
            {
                source.AppendLine($"                    var __result = {receiver};");
                source.AppendLine("                    return __result;");
            }
            else if (command.NeedsAwait)
            {
                source.AppendLine($"                    await {receiver}.ConfigureAwait(false);");
                source.AppendLine("                    return null;");
            }
            else
            {
                source.AppendLine($"                    {receiver};");
                source.AppendLine("                    return null;");
            }

            source.AppendLine("                }");
        }

        source.AppendLine("                default:");
        source.AppendLine("                    throw new global::OrielWeb.Ipc.OrielIpcException($\"未知命令索引 {index}。\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("    }");
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
