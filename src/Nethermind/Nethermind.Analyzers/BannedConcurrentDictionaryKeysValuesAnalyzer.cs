// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Bans access to <c>.Keys</c> and <c>.Values</c> on <c>System.Collections.Concurrent.ConcurrentDictionary</c>
/// and <c>NonBlocking.ConcurrentDictionary</c>. Both implementations walk every bucket and allocate
/// a fresh snapshot collection on each access. On hot paths this is a hidden allocation cliff.
/// Enumerate the dictionary directly with <c>foreach</c>, or take a deliberate snapshot via
/// <c>ConcurrentDictionaryExtensions.AcquireLock</c> when stability is genuinely required.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BannedConcurrentDictionaryKeysValuesAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH004";

    private static readonly string[] BannedDictionaryMetadataNames =
    [
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "NonBlocking.ConcurrentDictionary`2",
    ];

    private static readonly LocalizableString Title =
        "Avoid ConcurrentDictionary.Keys / .Values — they allocate a snapshot";

    private static readonly LocalizableString MessageFormat =
        "ConcurrentDictionary<TKey,TValue>.{0} allocates a full snapshot list. Enumerate the dictionary directly with foreach, or use AcquireLock for a deliberate snapshot.";

    private static readonly LocalizableString Description =
        "Accessing ConcurrentDictionary<TKey,TValue>.Keys or .Values (System.Collections.Concurrent " +
        "or NonBlocking) walks every bucket and returns a freshly allocated snapshot collection. " +
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
            ImmutableHashSet<INamedTypeSymbol>.Builder builder =
                ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (string metadataName in BannedDictionaryMetadataNames)
            {
                INamedTypeSymbol? type = startContext.Compilation.GetTypeByMetadataName(metadataName);
                if (type is not null) builder.Add(type);
            }

            // If none of the banned types are referenced (e.g. minimal compilation in tests),
            // there is nothing this analyzer can flag.
            if (builder.Count == 0) return;

            ImmutableHashSet<INamedTypeSymbol> bannedTypes = builder.ToImmutable();
            startContext.RegisterOperationAction(
                operationContext => Analyze(operationContext, bannedTypes),
                OperationKind.PropertyReference);
        });
    }

    private static void Analyze(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> bannedTypes)
    {
        IPropertyReferenceOperation operation = (IPropertyReferenceOperation)context.Operation;
        IPropertySymbol property = operation.Property;

        if (property.Name is not ("Keys" or "Values"))
            return;

        // Compare against the open generic definition so any constructed
        // ConcurrentDictionary<TKey,TValue> matches, while user-defined or
        // third-party types of the same simple name do not.
        if (!bannedTypes.Contains(property.ContainingType.OriginalDefinition))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Rule, operation.Syntax.GetLocation(), property.Name));
    }
}
