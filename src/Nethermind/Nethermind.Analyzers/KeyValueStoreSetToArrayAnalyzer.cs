// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports calls to <c>IWriteOnlyKeyValueStore.Set(key, value)</c> whose <c>value</c> argument is a
/// freshly materialized <c>byte[]</c> that already exists as a <c>byte</c>-span. Two shapes are flagged:
/// <list type="bullet">
/// <item><c>span.ToArray()</c> where <c>span</c> is <see cref="System.Span{T}"/>/<see cref="System.ReadOnlySpan{T}"/>
/// of <c>byte</c> — pass the span itself.</item>
/// <item><c>x.BytesToArray()</c> where <c>x</c>'s type exposes a <c>Bytes</c> property of
/// <c>Span&lt;byte&gt;</c>/<c>ReadOnlySpan&lt;byte&gt;</c> (e.g. <c>Hash256</c>/<c>ValueHash256</c>) — pass
/// <c>x.Bytes</c>.</item>
/// </list>
/// The copy is often unnecessary — call <c>PutSpan(key, span)</c> instead and let the store decide
/// whether it needs to copy (see <c>IWriteOnlyKeyValueStore.PreferWriteByArray</c>).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KeyValueStoreSetToArrayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH007";

    private static readonly LocalizableString Title =
        "Avoid Set with a byte[] copied from a span when PutSpan is available";

    private static readonly LocalizableString MessageFormat =
        "Value passed to IWriteOnlyKeyValueStore.Set is copied to an array via {0}; call PutSpan with {1} instead";

    private static readonly LocalizableString Description =
        "IWriteOnlyKeyValueStore.Set takes a byte[]. Materializing a span into an array (span.ToArray() or " +
        "x.BytesToArray()) to satisfy it always allocates a heap copy. PutSpan accepts a ReadOnlySpan<byte> " +
        "and lets the store decide whether a copy is needed (governed by PreferWriteByArray). Replace " +
        "Set(key, span.ToArray(), flags) with PutSpan(key, span, flags).";

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
            INamedTypeSymbol? store = startContext.Compilation.GetTypeByMetadataName("Nethermind.Core.IWriteOnlyKeyValueStore");
            INamedTypeSymbol? spanType = startContext.Compilation.GetTypeByMetadataName("System.Span`1");
            INamedTypeSymbol? readOnlySpanType = startContext.Compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
            if (store is null || spanType is null || readOnlySpanType is null)
                return;

            IMethodSymbol? setMethod = FindByteArraySet(store);
            if (setMethod is null)
                return;

            StoreTypes types = new(setMethod, spanType, readOnlySpanType,
                startContext.Compilation.GetSpecialType(SpecialType.System_Byte));

            startContext.RegisterOperationAction(c => Analyze(c, types), OperationKind.Invocation);
        });
    }

    /// <summary>Locates the <c>Set(ReadOnlySpan&lt;byte&gt; key, byte[]? value, WriteFlags flags)</c> member.</summary>
    private static IMethodSymbol? FindByteArraySet(INamedTypeSymbol store)
    {
        foreach (ISymbol member in store.GetMembers("Set"))
        {
            if (member is IMethodSymbol { Parameters.Length: >= 2 } method
                && method.Parameters[1].Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
            {
                return method;
            }
        }
        return null;
    }

    private readonly struct StoreTypes(
        IMethodSymbol setMethod,
        INamedTypeSymbol span,
        INamedTypeSymbol readOnlySpan,
        INamedTypeSymbol byteType)
    {
        public IMethodSymbol SetMethod { get; } = setMethod;
        public INamedTypeSymbol Span { get; } = span;
        public INamedTypeSymbol ReadOnlySpan { get; } = readOnlySpan;
        public INamedTypeSymbol Byte { get; } = byteType;
    }

    private static void Analyze(OperationAnalysisContext context, StoreTypes types)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        if (!IsStoreSet(invocation.TargetMethod, types))
            return;

        // Skip the store's own PutSpan default/override delegating to this.Set — rewriting it to
        // this.PutSpan would self-recurse.
        if (invocation.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
            && context.ContainingSymbol is IMethodSymbol { Name: "PutSpan" })
            return;

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (argument.Parameter?.Ordinal != 1)
                continue;

            IOperation value = argument.Value;
            while (value is IConversionOperation conv)
                value = conv.Operand;

            if (value is IInvocationOperation { TargetMethod: { IsStatic: false, Parameters.Length: 0 } } call
                && TryClassifyCopy(call, types, out string copyDescription, out string replacement))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, value.Syntax.GetLocation(), copyDescription, replacement));
            }
            return;
        }
    }

    /// <summary>
    /// Recognizes the two span-to-array copy shapes and yields how to describe the copy and what to pass
    /// to <c>PutSpan</c> instead.
    /// </summary>
    private static bool TryClassifyCopy(IInvocationOperation call, StoreTypes types, out string copyDescription, out string replacement)
    {
        copyDescription = replacement = string.Empty;
        IMethodSymbol method = call.TargetMethod;

        // Span<byte>.ToArray() / ReadOnlySpan<byte>.ToArray() -> pass the span.
        if (method.Name == "ToArray"
            && method.ContainingType is INamedTypeSymbol { TypeArguments.Length: 1 } span
            && SymbolEqualityComparer.Default.Equals(span.TypeArguments[0], types.Byte))
        {
            INamedTypeSymbol original = span.OriginalDefinition;
            bool isSpan = SymbolEqualityComparer.Default.Equals(original, types.Span);
            if (!isSpan && !SymbolEqualityComparer.Default.Equals(original, types.ReadOnlySpan))
                return false;

            copyDescription = $"{(isSpan ? "Span" : "ReadOnlySpan")}<byte>.ToArray()";
            replacement = "the span";
            return true;
        }

        // x.BytesToArray() where x's type exposes a byte-span `Bytes` property -> pass x.Bytes.
        if (method.Name == "BytesToArray"
            && method.ReturnType is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }
            && call.Instance?.Type is ITypeSymbol receiverType
            && HasByteSpanBytesProperty(receiverType, types))
        {
            copyDescription = "BytesToArray()";
            replacement = "its .Bytes span";
            return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="type"/> (or a base type) exposes an instance <c>Bytes</c> property of type <c>Span&lt;byte&gt;</c> or <c>ReadOnlySpan&lt;byte&gt;</c>.</summary>
    private static bool HasByteSpanBytesProperty(ITypeSymbol type, StoreTypes types)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers("Bytes"))
            {
                if (member is IPropertySymbol { IsStatic: false, Type: INamedTypeSymbol { TypeArguments.Length: 1 } propertyType }
                    && (SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, types.Span)
                        || SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, types.ReadOnlySpan))
                    && SymbolEqualityComparer.Default.Equals(propertyType.TypeArguments[0], types.Byte))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the call is dispatched through <see cref="StoreTypes.SetMethod"/> — i.e. the receiver is
    /// typed as <c>IWriteOnlyKeyValueStore</c> or a derived interface. Calls that bind to a concrete store's
    /// override are ignored: those are low-level plumbing where <c>PreferWriteByArray</c> stores deliberately
    /// take an owned array, and <c>PutSpan</c> is only reachable there via an interface cast.
    /// </summary>
    private static bool IsStoreSet(IMethodSymbol method, StoreTypes types) =>
        method.Name == "Set" && SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, types.SetMethod);
}
