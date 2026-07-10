// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports call sites that pass <c>span.ToArray()</c> to a method (or constructor) when an
/// overload of that same method accepts <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c>
/// at the same position. The array allocation is wasted — pass the span directly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SpanToArrayAtCallSiteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH005";

    private static readonly LocalizableString Title =
        "Avoid Span<T>.ToArray() when a span overload exists";

    private static readonly LocalizableString MessageFormat =
        "'{0}' has an overload accepting {1}<{2}>; pass the span directly instead of allocating an array via ToArray";

    private static readonly LocalizableString Description =
        "Calling Span<T>.ToArray() or ReadOnlySpan<T>.ToArray() to fit a T[]-typed parameter " +
        "allocates a copy on the heap. When the same method has an overload taking " +
        "Span<T> or ReadOnlySpan<T> at that position, drop the .ToArray() and pass the span directly.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? spanType = startContext.Compilation.GetTypeByMetadataName("System.Span`1");
            INamedTypeSymbol? readOnlySpanType = startContext.Compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
            if (spanType is null || readOnlySpanType is null)
                return;

            SpanTypes spanTypes = new(spanType, readOnlySpanType);

            startContext.RegisterOperationAction(c => AnalyzeInvocation(c, spanTypes), OperationKind.Invocation);
            startContext.RegisterOperationAction(c => AnalyzeObjectCreation(c, spanTypes), OperationKind.ObjectCreation);
        });
    }

    private readonly struct SpanTypes(INamedTypeSymbol span, INamedTypeSymbol readOnlySpan)
    {
        public INamedTypeSymbol Span { get; } = span;
        public INamedTypeSymbol ReadOnlySpan { get; } = readOnlySpan;
    }

    private enum ParamShape { Array, Span, ReadOnlySpan }

    private static void AnalyzeInvocation(OperationAnalysisContext context, SpanTypes spanTypes)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.ContainingType is not INamedTypeSymbol containingType)
            return;

        AnalyzeArguments(context, spanTypes, invocation.Arguments, method, containingType, isConstructor: false);
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context, SpanTypes spanTypes)
    {
        IObjectCreationOperation creation = (IObjectCreationOperation)context.Operation;
        if (creation.Constructor is not IMethodSymbol ctor)
            return;
        if (ctor.ContainingType is not INamedTypeSymbol containingType)
            return;

        AnalyzeArguments(context, spanTypes, creation.Arguments, ctor, containingType, isConstructor: true);
    }

    private static ImmutableArray<IMethodSymbol> CollectMethodOverloads(INamedTypeSymbol containingType, string name)
    {
        ImmutableArray<ISymbol> members = containingType.GetMembers(name);
        ImmutableArray<IMethodSymbol>.Builder builder = ImmutableArray.CreateBuilder<IMethodSymbol>(members.Length);
        foreach (ISymbol member in members)
        {
            if (member is IMethodSymbol method)
                builder.Add(method);
        }
        return builder.Count == builder.Capacity ? builder.MoveToImmutable() : builder.ToImmutable();
    }

    private static void AnalyzeArguments(
        OperationAnalysisContext context,
        SpanTypes spanTypes,
        ImmutableArray<IArgumentOperation> arguments,
        IMethodSymbol currentMethod,
        INamedTypeSymbol containingType,
        bool isConstructor)
    {
        ImmutableArray<IMethodSymbol> overloads = default;

        foreach (IArgumentOperation argument in arguments)
        {
            if (argument.Parameter is not IParameterSymbol parameter)
                continue;
            if (parameter.IsParams)
                continue;

            ITypeSymbol? expectedElementType = null;
            ParamShape paramShape;
            if (parameter.Type is IArrayTypeSymbol arrayType)
            {
                paramShape = ParamShape.Array;
                expectedElementType = arrayType.ElementType;
            }
            else if (parameter.Type is INamedTypeSymbol namedParam
                && namedParam.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(namedParam.OriginalDefinition, spanTypes.Span))
            {
                paramShape = ParamShape.Span;
                expectedElementType = namedParam.TypeArguments[0];
            }
            else if (parameter.Type is INamedTypeSymbol namedRosParam
                && namedRosParam.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(namedRosParam.OriginalDefinition, spanTypes.ReadOnlySpan))
            {
                paramShape = ParamShape.ReadOnlySpan;
                expectedElementType = namedRosParam.TypeArguments[0];
            }
            else
            {
                continue;
            }

            IOperation value = argument.Value;
            while (value is IConversionOperation conv)
                value = conv.Operand;

            if (value is not IInvocationOperation toArrayCall)
                continue;
            IMethodSymbol toArray = toArrayCall.TargetMethod;
            if (toArray.Name != "ToArray" || toArray.Parameters.Length != 0 || toArray.IsStatic)
                continue;
            if (toArray.ContainingType is not INamedTypeSymbol receiverType)
                continue;

            INamedTypeSymbol original = receiverType.OriginalDefinition;
            bool isSpan = SymbolEqualityComparer.Default.Equals(original, spanTypes.Span);
            bool isReadOnlySpan = SymbolEqualityComparer.Default.Equals(original, spanTypes.ReadOnlySpan);
            if (!isSpan && !isReadOnlySpan)
                continue;
            if (receiverType.TypeArguments.Length != 1)
                continue;

            ITypeSymbol elementType = receiverType.TypeArguments[0];
            if (!SymbolEqualityComparer.Default.Equals(elementType, expectedElementType))
                continue;

            string? overloadKind;
            if (paramShape == ParamShape.Array)
            {
                if (overloads.IsDefault)
                    overloads = isConstructor
                        ? containingType.InstanceConstructors
                        : CollectMethodOverloads(containingType, currentMethod.Name);

                overloadKind = FindSpanOverloadKind(
                    overloads,
                    currentMethod,
                    parameter.Ordinal,
                    elementType,
                    isSpan,
                    spanTypes,
                    context.ContainingSymbol,
                    context.Compilation);
            }
            else
            {
                // Param is already (ReadOnly)Span<T>; ToArray is just heap allocation.
                // ReadOnlySpan<T> caller cannot fit a Span<T> param.
                if (paramShape == ParamShape.Span && !isSpan)
                    continue;
                overloadKind = paramShape == ParamShape.Span ? "Span" : "ReadOnlySpan";
            }

            if (overloadKind is null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                toArrayCall.Syntax.GetLocation(),
                currentMethod.Name,
                overloadKind,
                elementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static string? FindSpanOverloadKind(
        ImmutableArray<IMethodSymbol> overloads,
        IMethodSymbol currentMethod,
        int paramIndex,
        ITypeSymbol elementType,
        bool callerIsSpan,
        SpanTypes spanTypes,
        ISymbol? containingSymbol,
        Compilation compilation)
    {
        IMethodSymbol currentOriginal = currentMethod.OriginalDefinition;
        ImmutableArray<IParameterSymbol> currentParams = currentOriginal.Parameters;
        ISymbol? containingOriginal = containingSymbol?.OriginalDefinition;
        ISymbol withinAccessibility = containingSymbol?.ContainingType ?? containingSymbol ?? currentMethod.ContainingType;
        string? bestMatch = null;

        foreach (IMethodSymbol candidate in overloads)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, currentOriginal))
                continue;
            // Skip when the matching overload IS the method making this call —
            // delegating from the span overload to the array overload (e.g.
            // `Foo(ReadOnlySpan<byte> s) : this(s.ToArray())`) is the canonical
            // way to share storage and would self-recurse if "fixed".
            if (containingOriginal is not null
                && SymbolEqualityComparer.Default.Equals(candidate, containingOriginal))
                continue;
            if (candidate.Parameters.Length != currentParams.Length)
                continue;
            if (candidate.IsStatic != currentMethod.IsStatic)
                continue;
            if (candidate.TypeParameters.Length != currentOriginal.TypeParameters.Length)
                continue;
            // Suggesting an inaccessible overload would produce uncompilable code.
            if (!compilation.IsSymbolAccessibleWithin(candidate, withinAccessibility))
                continue;

            string? matchKind = MatchingSpanParamKind(
                candidate.Parameters[paramIndex].Type,
                currentParams[paramIndex].Type,
                elementType,
                callerIsSpan,
                spanTypes);
            if (matchKind is null)
                continue;

            bool otherParamsMatch = true;
            for (int i = 0; i < currentParams.Length; i++)
            {
                if (i == paramIndex) continue;
                if (!SymbolEqualityComparer.Default.Equals(currentParams[i].Type, candidate.Parameters[i].Type))
                {
                    otherParamsMatch = false;
                    break;
                }
            }
            if (!otherParamsMatch)
                continue;

            // Prefer Span<T> overload when caller already has Span<T> (matches without conversion).
            if (callerIsSpan && matchKind == "Span")
                return "Span";
            bestMatch ??= matchKind;
        }

        return bestMatch;
    }

    private static string? MatchingSpanParamKind(
        ITypeSymbol candidateParamType,
        ITypeSymbol currentParamType,
        ITypeSymbol elementType,
        bool callerIsSpan,
        SpanTypes spanTypes)
    {
        if (candidateParamType is not INamedTypeSymbol named || named.TypeArguments.Length != 1)
            return null;

        INamedTypeSymbol original = named.OriginalDefinition;
        bool isReadOnlySpan = SymbolEqualityComparer.Default.Equals(original, spanTypes.ReadOnlySpan);
        bool isSpan = SymbolEqualityComparer.Default.Equals(original, spanTypes.Span);
        if (!isSpan && !isReadOnlySpan)
            return null;

        // ReadOnlySpan<T> caller cannot pass to a Span<T> overload.
        if (!callerIsSpan && isSpan)
            return null;

        ITypeSymbol candidateElement = named.TypeArguments[0];

        bool elementMatches = SymbolEqualityComparer.Default.Equals(candidateElement, elementType);
        if (!elementMatches)
        {
            // Generic shape: current's T[] and candidate's Span<T> must reference the same method
            // type parameter ordinal, so a single substitution unifies them with the call-site type.
            elementMatches = currentParamType is IArrayTypeSymbol currentArr
                && currentArr.ElementType is ITypeParameterSymbol curTp
                && candidateElement is ITypeParameterSymbol candTp
                && curTp.Ordinal == candTp.Ordinal
                && curTp.TypeParameterKind == candTp.TypeParameterKind;
        }

        if (!elementMatches)
            return null;

        return isSpan ? "Span" : "ReadOnlySpan";
    }
}
