// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports <c>new TaskCompletionSource(...)</c> / <c>new TaskCompletionSource&lt;T&gt;(...)</c>
/// constructions that do not pass <c>TaskCreationOptions.RunContinuationsAsynchronously</c>.
///
/// Without that flag, <c>SetResult</c>/<c>SetException</c>/<c>TrySetResult</c>/<c>TrySetException</c>
/// invoke continuations *synchronously* on the completing thread. In Nethermind this has caused
/// hard-to-diagnose deadlocks (continuation re-enters a lock the completer's caller holds) and
/// latency spikes on hot processing threads. The flag must be passed as a constant expression
/// whose value has the <c>RunContinuationsAsynchronously</c> bit set (alone or OR-combined with
/// other flags). Non-constant options expressions are flagged because the analyzer cannot prove
/// the bit is set. An explicit constant <c>TaskCreationOptions.None</c> is accepted as a
/// deliberate opt-in to synchronous continuations; the rule targets constructions where the
/// flag was never considered, not ones where the trade-off was made consciously.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH006";

    // TaskCreationOptions.RunContinuationsAsynchronously == 64.
    private const int RunContinuationsAsynchronouslyFlag = 64;

    private static readonly LocalizableString Title =
        "TaskCompletionSource must use RunContinuationsAsynchronously";

    private static readonly LocalizableString MessageFormat =
        "TaskCompletionSource is constructed without TaskCreationOptions.RunContinuationsAsynchronously; " +
        "SetResult/SetException will invoke continuations synchronously and may deadlock or block hot threads.";

    private static readonly LocalizableString Description =
        "Continuations attached to a TaskCompletionSource run synchronously on the completing thread " +
        "unless TaskCreationOptions.RunContinuationsAsynchronously is passed to the constructor. " +
        "This has caused hard-to-trace deadlocks in Nethermind. Always construct TaskCompletionSource " +
        "with a constant TaskCreationOptions value that includes RunContinuationsAsynchronously, " +
        "or pass an explicit TaskCreationOptions.None to opt in to synchronous continuations deliberately.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        category: "Reliability",
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
            INamedTypeSymbol? tcs = startContext.Compilation.GetTypeByMetadataName(
                "System.Threading.Tasks.TaskCompletionSource");
            INamedTypeSymbol? tcsGeneric = startContext.Compilation.GetTypeByMetadataName(
                "System.Threading.Tasks.TaskCompletionSource`1");
            INamedTypeSymbol? options = startContext.Compilation.GetTypeByMetadataName(
                "System.Threading.Tasks.TaskCreationOptions");

            if (tcs is null && tcsGeneric is null) return;

            startContext.RegisterOperationAction(
                ctx => Analyze(ctx, tcs, tcsGeneric, options),
                OperationKind.ObjectCreation);
        });
    }

    private static void Analyze(
        OperationAnalysisContext context,
        INamedTypeSymbol? tcs,
        INamedTypeSymbol? tcsGeneric,
        INamedTypeSymbol? options)
    {
        IObjectCreationOperation creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol type) return;

        INamedTypeSymbol comparisonTarget = type.IsGenericType ? type.OriginalDefinition : type;
        if (!SymbolEqualityComparer.Default.Equals(comparisonTarget, tcs)
            && !SymbolEqualityComparer.Default.Equals(comparisonTarget, tcsGeneric))
        {
            return;
        }

        bool hasFlag = false;
        foreach (IArgumentOperation argument in creation.Arguments)
        {
            if (argument.Parameter is null) continue;
            if (!SymbolEqualityComparer.Default.Equals(argument.Parameter.Type, options)) continue;

            // Found the TaskCreationOptions argument. Require a constant value with the bit set,
            // or an explicit TaskCreationOptions.None (a deliberate opt-in to synchronous continuations).
            Optional<object?> constant = argument.Value.ConstantValue;
            if (constant.HasValue && constant.Value is int value
                && ((value & RunContinuationsAsynchronouslyFlag) != 0 || value == 0))
            {
                hasFlag = true;
            }
            break;
        }

        if (!hasFlag)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Syntax.GetLocation()));
        }
    }
}
