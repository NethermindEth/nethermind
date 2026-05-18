// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Analyzers;

[Generator(LanguageNames.CSharp)]
public sealed class JsonRpcDispatcherGenerator : IIncrementalGenerator
{
    private const string JsonRpcAssemblyName = "Nethermind.JsonRpc";
    private const string RpcModuleAttributeMetadataName = "Nethermind.JsonRpc.Modules.RpcModuleAttribute";
    private const string IResultWrapperMetadataName = "Nethermind.JsonRpc.IResultWrapper";

    private static readonly SymbolDisplayFormat FullyQualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly string[] KnownExternalMethodNames =
    [
        "engine_exchangeCapabilities",
        "engine_forkchoiceUpdatedV2",
        "engine_forkchoiceUpdatedV3",
        "engine_getPayloadV3",
        "engine_getPayloadV4",
        "engine_newPayloadV1",
        "engine_newPayloadV2",
        "engine_newPayloadV3",
        "engine_newPayloadV4",
    ];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<RpcModuleModel?> rpcModules = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (context, _) => GetRpcModule(context))
            .Where(static module => module is not null);

        IncrementalValueProvider<(Compilation Compilation, ImmutableArray<RpcModuleModel?> Modules)> source =
            context.CompilationProvider.Combine(rpcModules.Collect());

        context.RegisterSourceOutput(source, static (context, source) =>
        {
            if (source.Compilation.AssemblyName != JsonRpcAssemblyName)
            {
                return;
            }

            ImmutableArray<RpcModuleModel> modules = source.Modules
                .Where(static module => module is not null)
                .Select(static module => module!)
                .ToImmutableArray();

            context.AddSource(
                "JsonRpc.GeneratedRpcMethodNames.g.cs",
                SourceText.From(GenerateMethodNames(modules), Encoding.UTF8));

            context.AddSource(
                "JsonRpc.GeneratedRpcMethodDispatcher.g.cs",
                SourceText.From(GenerateDispatcher(modules), Encoding.UTF8));
        });
    }

    private static RpcModuleModel? GetRpcModule(GeneratorSyntaxContext context)
    {
        InterfaceDeclarationSyntax declaration = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (!HasAttribute(symbol, RpcModuleAttributeMetadataName))
        {
            return null;
        }

        INamedTypeSymbol? resultWrapper = context.SemanticModel.Compilation.GetTypeByMetadataName(IResultWrapperMetadataName);
        if (resultWrapper is null)
        {
            return null;
        }

        Dictionary<string, RpcMethodModel> methods = new(StringComparer.Ordinal);
        AddMethods(symbol, resultWrapper, methods);
        foreach (INamedTypeSymbol baseInterface in symbol.AllInterfaces)
        {
            AddMethods(baseInterface, resultWrapper, methods);
        }

        if (methods.Count == 0)
        {
            return null;
        }

        return new RpcModuleModel(
            symbol.ToDisplayString(FullyQualifiedTypeFormat),
            methods.Values.OrderBy(static method => method.Name, StringComparer.Ordinal).ToImmutableArray());
    }

    private static void AddMethods(
        INamedTypeSymbol module,
        INamedTypeSymbol resultWrapper,
        Dictionary<string, RpcMethodModel> methods)
    {
        foreach (IMethodSymbol method in module.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsGenericMethod)
            {
                continue;
            }

            if (method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
            {
                continue;
            }

            InvocationKind invocationKind = GetInvocationKind(method.ReturnType, resultWrapper);
            if (invocationKind == InvocationKind.Unsupported)
            {
                continue;
            }

            string methodName = method.Name.Trim();
            if (!methods.ContainsKey(methodName))
            {
                methods.Add(
                    methodName,
                    new RpcMethodModel(
                        methodName,
                        invocationKind,
                        method.Parameters
                            .Select(static parameter => parameter.Type.ToDisplayString(FullyQualifiedTypeFormat))
                            .ToImmutableArray()));
            }
        }
    }

    private static InvocationKind GetInvocationKind(ITypeSymbol returnType, INamedTypeSymbol resultWrapper)
    {
        if (IsAssignableTo(returnType, resultWrapper))
        {
            return InvocationKind.Direct;
        }

        if (returnType is not INamedTypeSymbol namedType || namedType.TypeArguments.Length != 1)
        {
            return InvocationKind.Unsupported;
        }

        if (!IsAssignableTo(namedType.TypeArguments[0], resultWrapper))
        {
            return InvocationKind.Unsupported;
        }

        return namedType.OriginalDefinition.ToDisplayString() switch
        {
            "System.Threading.Tasks.Task<TResult>" => InvocationKind.Task,
            "System.Threading.Tasks.ValueTask<TResult>" => InvocationKind.ValueTask,
            _ => InvocationKind.Unsupported
        };
    }

    private static bool IsAssignableTo(ITypeSymbol type, INamedTypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(type, target))
        {
            return true;
        }

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
    }

    private static bool HasAttribute(INamedTypeSymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string GenerateMethodNames(ImmutableArray<RpcModuleModel> modules)
    {
        string[] methodNames = modules
            .SelectMany(static module => module.Methods.Select(static method => method.Name))
            .Concat(KnownExternalMethodNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable disable");
        builder.AppendLine("using System.Text.Json;");
        builder.AppendLine();
        builder.AppendLine("namespace Nethermind.JsonRpc;");
        builder.AppendLine();
        builder.AppendLine("internal static class GeneratedRpcMethodNames");
        builder.AppendLine("{");
        builder.AppendLine("    public static string Intern(JsonElement methodElement)");
        builder.AppendLine("    {");
        foreach (string methodName in methodNames)
        {
            builder
                .Append("        if (methodElement.ValueEquals(\"")
                .Append(Escape(methodName))
                .Append("\"u8)) return \"")
                .Append(Escape(methodName))
                .AppendLine("\";");
        }
        builder.AppendLine();
        builder.AppendLine("        return methodElement.GetString();");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string GenerateDispatcher(ImmutableArray<RpcModuleModel> modules)
    {
        RpcMethodEntry[] methods = modules
            .SelectMany(static module => module.Methods.Select(method => new RpcMethodEntry(module.InterfaceType, method)))
            .OrderBy(static method => method.Method.Name, StringComparer.Ordinal)
            .ThenBy(static method => method.InterfaceType, StringComparer.Ordinal)
            .ToArray();

        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable disable");
        builder.AppendLine("using System.Threading.Tasks;");
        builder.AppendLine("using Nethermind.JsonRpc.Modules;");
        builder.AppendLine();
        builder.AppendLine("namespace Nethermind.JsonRpc;");
        builder.AppendLine();
        builder.AppendLine("internal static class GeneratedRpcMethodDispatcher");
        builder.AppendLine("{");
        builder.AppendLine("    public static bool TryInvoke(string methodName, IRpcModule rpcModule, object[] parameters, bool hasMissing, out ValueTask<IResultWrapper> invocation)");
        builder.AppendLine("    {");
        builder.AppendLine("        invocation = default;");
        builder.AppendLine("        if (hasMissing)");
        builder.AppendLine("        {");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        switch (methodName)");
        builder.AppendLine("        {");

        foreach (IGrouping<string, RpcMethodEntry> group in methods.GroupBy(static method => method.Method.Name, StringComparer.Ordinal))
        {
            builder
                .Append("            case \"")
                .Append(Escape(group.Key))
                .AppendLine("\":");
            builder.AppendLine("            {");

            int entryIndex = 0;
            foreach (RpcMethodEntry entry in group)
            {
                AppendMethodCase(builder, entry, $"module{entryIndex++}");
            }

            builder.AppendLine("                break;");
            builder.AppendLine("            }");
        }

        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return false;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static bool HasParameterCount(object[] parameters, int count) =>");
        builder.AppendLine("        count == 0 ? parameters is null || parameters.Length == 0 : parameters is not null && parameters.Length == count;");
        builder.AppendLine();
        builder.AppendLine("    private static ValueTask<IResultWrapper> Wrap(IResultWrapper result) => new(result);");
        builder.AppendLine();
        builder.AppendLine("    private static async ValueTask<IResultWrapper> Wrap<T>(Task<T> task) where T : IResultWrapper =>");
        builder.AppendLine("        await task.ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine("    private static async ValueTask<IResultWrapper> Wrap<T>(ValueTask<T> task) where T : IResultWrapper =>");
        builder.AppendLine("        await task.ConfigureAwait(false);");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendMethodCase(StringBuilder builder, RpcMethodEntry entry, string moduleVariableName)
    {
        ImmutableArray<string> parameterTypes = entry.Method.ParameterTypes;
        builder
            .Append("                if (rpcModule is ")
            .Append(entry.InterfaceType)
            .Append(' ')
            .Append(moduleVariableName)
            .AppendLine(")");
        builder.AppendLine("                {");
        builder
            .Append("                    if (!HasParameterCount(parameters, ")
            .Append(parameterTypes.Length)
            .AppendLine("))");
        builder.AppendLine("                    {");
        builder.AppendLine("                        return false;");
        builder.AppendLine("                    }");
        builder.AppendLine();
        builder.Append("                    invocation = Wrap(");
        builder.Append(moduleVariableName);
        builder.Append('.');
        builder.Append(entry.Method.Name);
        builder.Append('(');
        for (int i = 0; i < parameterTypes.Length; i++)
        {
            if (i != 0)
            {
                builder.Append(", ");
            }

            builder
                .Append('(')
                .Append(parameterTypes[i])
                .Append(")parameters[")
                .Append(i)
                .Append(']');
        }
        builder.AppendLine("));");
        builder.AppendLine("                    return true;");
        builder.AppendLine("                }");
    }

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

    private enum InvocationKind
    {
        Unsupported,
        Direct,
        Task,
        ValueTask
    }

    private sealed class RpcModuleModel(string interfaceType, ImmutableArray<RpcMethodModel> methods)
    {
        public string InterfaceType { get; } = interfaceType;
        public ImmutableArray<RpcMethodModel> Methods { get; } = methods;
    }

    private readonly struct RpcMethodModel(string name, InvocationKind invocationKind, ImmutableArray<string> parameterTypes)
    {
        public string Name { get; } = name;
        public InvocationKind InvocationKind { get; } = invocationKind;
        public ImmutableArray<string> ParameterTypes { get; } = parameterTypes;
    }

    private readonly struct RpcMethodEntry(string interfaceType, RpcMethodModel method)
    {
        public string InterfaceType { get; } = interfaceType;
        public RpcMethodModel Method { get; } = method;
    }
}
