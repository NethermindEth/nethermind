// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

internal static partial class Extractor
{
    private const string SignalTopologyDiagnostic =
        "fold.signal.topology: TransactionsExecuted must test the captured event, skip Invoke only for null, and rejoin normally.";

    private static ConditionalSignalIdentity BindTransactionsExecuted(SourceFile source,
        MethodDeclarationSyntax method, SemanticModel model, Compilation compilation)
    {
        InvocationExpressionSyntax invocation = SingleInvocation(method,
            "TransactionsExecuted?.Invoke()", "block.transactions-executed-signal");
        EnsureDeclaredMethodOwner(invocation, method, "block.transactions-executed-signal");
        if (invocation.Parent is not ConditionalAccessExpressionSyntax conditional ||
            !ReferenceEquals(conditional.WhenNotNull, invocation) ||
            conditional.Parent is not ExpressionStatementSyntax statement ||
            !ReferenceEquals(statement.Expression, conditional) ||
            model.GetOperation(conditional) is not IConditionalAccessOperation
            {
                Operation: IEventReferenceOperation eventReference,
                WhenNotNull: IInvocationOperation call,
            } ||
            CanonicalSymbol(eventReference.Event) != "E:" + ProcessorType + ".TransactionsExecuted" ||
            eventReference.Instance is not IInstanceReferenceOperation ||
            TypeId(eventReference.Instance.Type) != ProcessorType)
        {
            throw new ExtractionException(SignalTopologyDiagnostic);
        }

        ControlFlowGraph graph = RequireGraph(model, method, "BlockProcessor.ProcessBlock");
        TypedBinding invocationBinding = BindNode("block.transactions-executed-signal", source, method,
            invocation, model, compilation, graph, call);
        ValidateInvocation("block.transactions-executed-signal", invocationBinding.SymbolId,
            invocationBinding.ReceiverType, invocationBinding.ReceiverSymbol, invocationBinding.ArgumentBindings);
        TypedBinding evaluation = BindNode("block.transactions-executed-evaluation", source, method,
            conditional.Expression, model, compilation, graph, eventReference);
        if (evaluation.ControlFlowBlock < 0 || invocationBinding.ControlFlowBlock < 0)
            throw new ExtractionException(SignalTopologyDiagnostic);

        // The event capture/null test executes even without subscribers; Invoke does not.
        BasicBlock evaluationBlock = graph.Blocks[evaluation.ControlFlowBlock];
        BasicBlock invocationBlock = graph.Blocks[invocationBinding.ControlFlowBlock];
        IFlowCaptureOperation[] captures = evaluationBlock.Operations.OfType<IFlowCaptureOperation>()
            .Where(capture => capture.Value is IEventReferenceOperation captured &&
                captured.Syntax.Span == conditional.Expression.Span &&
                SymbolEqualityComparer.Default.Equals(captured.Event, eventReference.Event))
            .ToArray();
        if (captures.Length != 1 || !evaluationBlock.IsReachable || !invocationBlock.IsReachable ||
            evaluationBlock.BranchValue is not IIsNullOperation
            { Operand: IFlowCaptureReferenceOperation nullOperand } ||
            !nullOperand.Id.Equals(captures[0].Id) ||
            evaluationBlock.ConditionKind is not (ControlFlowConditionKind.WhenTrue or ControlFlowConditionKind.WhenFalse))
        {
            throw new ExtractionException(SignalTopologyDiagnostic);
        }

        BasicBlock? whenNull = (evaluationBlock.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? evaluationBlock.ConditionalSuccessor : evaluationBlock.FallThroughSuccessor)?.Destination;
        BasicBlock? whenNotNull = (evaluationBlock.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? evaluationBlock.FallThroughSuccessor : evaluationBlock.ConditionalSuccessor)?.Destination;
        if (whenNull is null || !whenNull.IsReachable || whenNotNull != invocationBlock ||
            whenNull == invocationBlock || evaluationBlock == invocationBlock ||
            invocationBlock.Operations.Length != 1 ||
            invocationBlock.Operations[0] is not IExpressionStatementOperation
            {
                Operation: IInvocationOperation
                {
                    Instance: IFlowCaptureReferenceOperation invokedReceiver,
                } loweredCall,
            } ||
            !invokedReceiver.Id.Equals(captures[0].Id) ||
            loweredCall.Syntax.Span != invocation.Span ||
            !SymbolEqualityComparer.Default.Equals(loweredCall.TargetMethod, call.TargetMethod) ||
            loweredCall.Arguments.Length != 0 ||
            invocationBlock.ConditionKind != ControlFlowConditionKind.None ||
            invocationBlock.FallThroughSuccessor?.Destination != whenNull ||
            invocationBlock.FallThroughSuccessor?.Semantics != ControlFlowBranchSemantics.Regular ||
            invocationBlock.ConditionalSuccessor is not null ||
            Branches(evaluationBlock).Any(static branch => branch.Semantics != ControlFlowBranchSemantics.Regular))
        {
            throw new ExtractionException(SignalTopologyDiagnostic);
        }

        return new(evaluation, whenNull.Ordinal, invocationBlock.Ordinal);
    }

    private static void ValidateTransactionsExecuted(ConditionalSignalIdentity signal,
        AnchorIdentity invocation, ControlFlowIdentity flow)
    {
        TypedBinding evaluation = signal.Evaluation;
        if (evaluation is null || evaluation.Path != BlockProcessorPath ||
            evaluation.Owner != "BlockProcessor" || evaluation.Member != "ProcessBlock" ||
            evaluation.ContainingMember != "ProcessBlock" || evaluation.NodeKind != "IdentifierName" ||
            evaluation.CanonicalSyntax != "TransactionsExecuted" ||
            evaluation.SyntaxSha256 != Sha256(System.Text.Encoding.UTF8.GetBytes(evaluation.CanonicalSyntax)) ||
            evaluation.SymbolId != "E:" + ProcessorType + ".TransactionsExecuted" ||
            evaluation.SymbolKind != "Event" || evaluation.OperationKind != "EventReference" ||
            evaluation.OperationType != "System.Action" || evaluation.ReceiverType != string.Empty ||
            evaluation.ReceiverSymbol != string.Empty || evaluation.ArgumentBindings is not { Length: 0 } ||
            !evaluation.IsReachable || !flow.ReachableBlocks.Contains(evaluation.ControlFlowBlock) ||
            !flow.ReachableBlocks.Contains(signal.WhenNullBlock) ||
            signal.WhenNotNullBlock != invocation.Binding.ControlFlowBlock ||
            signal.WhenNullBlock == signal.WhenNotNullBlock ||
            evaluation.ControlFlowBlock == signal.WhenNullBlock ||
            evaluation.ControlFlowBlock == signal.WhenNotNullBlock ||
            evaluation.Position >= invocation.Binding.Position ||
            !flow.Edges.Where(edge => edge.StartsWith(evaluation.ControlFlowBlock + "->", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal).SetEquals([
                    $"{evaluation.ControlFlowBlock}->{signal.WhenNullBlock}",
                    $"{evaluation.ControlFlowBlock}->{signal.WhenNotNullBlock}",
                ]) ||
            !flow.Edges.Where(edge => edge.StartsWith(signal.WhenNotNullBlock + "->", StringComparison.Ordinal))
                .SequenceEqual([$"{signal.WhenNotNullBlock}->{signal.WhenNullBlock}"], StringComparer.Ordinal))
        {
            throw new ExtractionException(SignalTopologyDiagnostic);
        }

        ValidateBinding(evaluation);
    }
}
