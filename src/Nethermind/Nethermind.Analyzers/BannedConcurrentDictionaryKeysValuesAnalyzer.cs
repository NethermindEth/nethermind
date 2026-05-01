// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Bans access to <c>System.Collections.Concurrent.ConcurrentDictionary&lt;TKey,TValue&gt;.Keys</c>
/// and <c>.Values</c>. Both properties take a snapshot — they walk every bucket under all locks
/// and allocate a fresh <c>List&lt;T&gt;</c>-backed <c>ReadOnlyCollection&lt;T&gt;</c>. On hot paths
/// this is a hidden allocation cliff. Enumerate the dictionary directly with <c>foreach</c>, or
/// take a deliberate snapshot via <c>ConcurrentDictionaryExtensions.AcquireLock</c> when stability
/// is genuinely required.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BannedConcurrentDictionaryKeysValuesAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH004";

    private const string ConcurrentDictionaryMetadataName = "System.Collections.Concurrent.ConcurrentDictionary`2";

    private static readonly LocalizableString Title =
        "Avoid ConcurrentDictionary.Keys / .Values — they allocate a snapshot";

    private static readonly LocalizableString MessageFormat =
        "ConcurrentDictionary<TKey,TValue>.{0} allocates a full snapshot list. Enumerate the dictionary directly with foreach, or use AcquireLock for a deliberate snapshot.";

    private static readonly LocalizableString Description =
        "Accessing ConcurrentDictionary<TKey,TValue>.Keys or .Values walks every bucket under all " +
        "internal locks and returns a freshly allocated ReadOnlyCollection<T> backed by a List<T>. " +
        "On hot paths this is a hidden, repeating allocation. Prefer foreach over the dictionary " +
        "itself, or use Nethermind.Core.Collections.ConcurrentDictionaryExtensions.AcquireLock when " +
        "a stable snapshot is genuinely required.";

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
            INamedTypeSymbol? concurrentDictionary = startContext.Compilation
                .GetTypeByMetadataName(ConcurrentDictionaryMetadataName);

            // If the BCL type isn't referenced (e.g. minimal compilation in tests),
            // there is nothing this analyzer can flag.
            if (concurrentDictionary is null)
                return;

            startContext.RegisterOperationAction(
                operationContext => Analyze(operationContext, concurrentDictionary),
                OperationKind.PropertyReference);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol concurrentDictionary)
    {
        IPropertyReferenceOperation operation = (IPropertyReferenceOperation)context.Operation;
        IPropertySymbol property = operation.Property;

        if (property.Name is not ("Keys" or "Values"))
            return;

        // Compare against the open generic definition so any constructed
        // ConcurrentDictionary<TKey,TValue> matches, while user-defined or
        // third-party types of the same simple name do not.
        INamedTypeSymbol containing = property.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, concurrentDictionary))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Rule, operation.Syntax.GetLocation(), property.Name));
    }
}
