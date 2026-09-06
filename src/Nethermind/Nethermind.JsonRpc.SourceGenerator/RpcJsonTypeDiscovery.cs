// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.JsonRpc.SourceGenerator;

internal static class RpcJsonTypeDiscovery
{
    private const string JsonRpcNamespace = "Nethermind.JsonRpc";
    private const string JsonRpcModulesNamespace = "Nethermind.JsonRpc.Modules";
    private const string JsonRpcSubscribeNamespace = "Nethermind.JsonRpc.Modules.Subscribe";
    private const string SystemThreadingTasksNamespace = "System.Threading.Tasks";

    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static ImmutableArray<string> GetJsonTypes(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
    {
        ImmutableArray<ITypeSymbol> typeSymbols = GetJsonTypeSymbols(semanticModel, node, cancellationToken);
        if (typeSymbols.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        ImmutableArray<string>.Builder types = ImmutableArray.CreateBuilder<string>(typeSymbols.Length);
        for (int i = 0; i < typeSymbols.Length; i++)
        {
            types.Add(GetTypeDisplayString(typeSymbols[i]));
        }

        return types.ToImmutable();
    }

    public static ImmutableArray<ITypeSymbol> GetJsonTypeSymbols(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken) =>
        node switch
        {
            TypeDeclarationSyntax typeDeclaration => GetRpcModuleJsonTypeSymbols(semanticModel, typeDeclaration, cancellationToken),
            InvocationExpressionSyntax invocation => GetSubscriptionJsonTypeSymbols(semanticModel, invocation, cancellationToken),
            _ => ImmutableArray<ITypeSymbol>.Empty
        };

    public static ImmutableArray<string> GetRpcMethodNames(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
    {
        if (node is not TypeDeclarationSyntax typeDeclaration ||
            semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol type ||
            !IsRpcModule(type))
        {
            return ImmutableArray<string>.Empty;
        }

        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
        if (type.TypeKind == TypeKind.Interface)
        {
            AddMethodNames(type, names);
        }

        for (int i = 0; i < type.AllInterfaces.Length; i++)
        {
            INamedTypeSymbol interfaceType = type.AllInterfaces[i];
            if (IsRpcModule(interfaceType))
            {
                AddMethodNames(interfaceType, names);
            }
        }

        return names.ToImmutable();
    }

    public static string GetTypeDisplayString(ITypeSymbol type) => type.ToDisplayString(TypeDisplayFormat);

    public static string[] GetSortedUniqueTypes(ImmutableArray<ImmutableArray<string>> typeGroups)
    {
        HashSet<string> uniqueTypes = new(StringComparer.Ordinal);
        for (int i = 0; i < typeGroups.Length; i++)
        {
            ImmutableArray<string> group = typeGroups[i];
            for (int j = 0; j < group.Length; j++)
            {
                uniqueTypes.Add(group[j]);
            }
        }

        string[] sortedTypes = [.. uniqueTypes];
        Array.Sort(sortedTypes, StringComparer.Ordinal);
        return sortedTypes;
    }

    private static ImmutableArray<ITypeSymbol> GetRpcModuleJsonTypeSymbols(
        SemanticModel semanticModel,
        TypeDeclarationSyntax typeDeclaration,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol type ||
            !IsRpcModule(type))
        {
            return ImmutableArray<ITypeSymbol>.Empty;
        }

        ImmutableArray<ITypeSymbol>.Builder types = ImmutableArray.CreateBuilder<ITypeSymbol>();
        AddMethodTypes(type, types);
        for (int i = 0; i < type.AllInterfaces.Length; i++)
        {
            INamedTypeSymbol interfaceType = type.AllInterfaces[i];
            if (IsRpcModule(interfaceType))
            {
                AddMethodTypes(interfaceType, types);
            }
        }

        return types.ToImmutable();
    }

    private static ImmutableArray<ITypeSymbol> GetSubscriptionJsonTypeSymbols(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            method.Name != "CreateSubscriptionMessage" ||
            !method.IsGenericMethod ||
            method.TypeArguments.Length != 1 ||
            !IsNamedType(method.ContainingType, JsonRpcSubscribeNamespace, "Subscription"))
        {
            return ImmutableArray<ITypeSymbol>.Empty;
        }

        ImmutableArray<ITypeSymbol>.Builder types = ImmutableArray.CreateBuilder<ITypeSymbol>(1);
        AddJsonType(method.TypeArguments[0], types);
        return types.ToImmutable();
    }

    private static bool IsRpcModule(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        for (int i = 0; i < type.AllInterfaces.Length; i++)
        {
            if (IsNamedType(type.AllInterfaces[i], JsonRpcModulesNamespace, "IRpcModule"))
            {
                return true;
            }
        }

        return IsNamedType(type, JsonRpcModulesNamespace, "IRpcModule");
    }

    private static void AddMethodTypes(INamedTypeSymbol type, ImmutableArray<ITypeSymbol>.Builder types)
    {
        ImmutableArray<ISymbol> members = type.GetMembers();
        for (int i = 0; i < members.Length; i++)
        {
            if (GetPublicRpcMethod(members[i]) is not { } method)
            {
                continue;
            }

            AddReturnPayloadTypes(method.ReturnType, types);
            for (int j = 0; j < method.Parameters.Length; j++)
            {
                AddJsonType(method.Parameters[j].Type, types);
            }
        }
    }

    private static void AddMethodNames(INamedTypeSymbol type, ImmutableArray<string>.Builder names)
    {
        ImmutableArray<ISymbol> members = type.GetMembers();
        for (int i = 0; i < members.Length; i++)
        {
            if (GetPublicRpcMethod(members[i]) is not { } method ||
                !HasJsonRpcMethodAttribute(method))
            {
                continue;
            }

            names.Add(method.Name);
        }
    }

    private static IMethodSymbol? GetPublicRpcMethod(ISymbol member) =>
        member is IMethodSymbol
        {
            IsStatic: false,
            IsGenericMethod: false,
            IsImplicitlyDeclared: false,
            MethodKind: MethodKind.Ordinary,
            DeclaredAccessibility: Accessibility.Public
        } method ? method : null;

    private static bool HasJsonRpcMethodAttribute(IMethodSymbol method)
    {
        ImmutableArray<AttributeData> attributes = method.GetAttributes();
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].AttributeClass is INamedTypeSymbol attributeType &&
                IsNamedType(attributeType, JsonRpcModulesNamespace, "JsonRpcMethodAttribute"))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddReturnPayloadTypes(ITypeSymbol returnType, ImmutableArray<ITypeSymbol>.Builder types)
    {
        ITypeSymbol resultType = UnwrapTaskLike(returnType);
        if (resultType is not INamedTypeSymbol namedResult || !namedResult.IsGenericType)
        {
            return;
        }

        INamedTypeSymbol resultDefinition = namedResult.ConstructedFrom;
        if (IsNamedType(resultDefinition, JsonRpcNamespace, "ResultWrapper`1"))
        {
            AddJsonType(namedResult.TypeArguments[0], types);
            return;
        }

        if (IsNamedType(resultDefinition, JsonRpcNamespace, "ResultWrapper`2"))
        {
            AddJsonType(namedResult.TypeArguments[0], types);
            AddJsonType(namedResult.TypeArguments[1], types);
        }
    }

    private static ITypeSymbol UnwrapTaskLike(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } namedType &&
        (IsNamedType(namedType.ConstructedFrom, SystemThreadingTasksNamespace, "Task`1") ||
         IsNamedType(namedType.ConstructedFrom, SystemThreadingTasksNamespace, "ValueTask`1"))
            ? namedType.TypeArguments[0]
            : type;

    private static void AddJsonType(ITypeSymbol type, ImmutableArray<ITypeSymbol>.Builder types)
    {
        if (type.TypeKind is TypeKind.TypeParameter or TypeKind.Error ||
            type.SpecialType == SpecialType.System_Void)
        {
            return;
        }

        types.Add(type);
    }

    private static bool IsNamedType(INamedTypeSymbol type, string namespaceName, string metadataName) =>
        type.ContainingNamespace.ToDisplayString() == namespaceName && type.MetadataName == metadataName;
}
