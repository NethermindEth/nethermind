// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

/// <summary>
/// Accounts for every call, write, local initializer and allocation in an admitted method.
/// </summary>
internal static class ReceiptTerminalFoldEffects
{
    internal static void Validate(string member, SemanticStep[] steps, SemanticOperation body)
    {
        List<SemanticExpression> calls = [];
        List<(string Form, SemanticExpression Target, SemanticExpression Value)> writes = [];
        List<string> allocations = [];
        List<SemanticExpression?> returns = [];
        foreach (SemanticStep step in steps) VisitStep(step);

        string[] expectedCalls = member switch
        {
            "MarkAsSuccess" => ["_txReceipts.Add", "BuildReceipt", "otherTxTracer.MarkAsSuccess", "_currentTxTracer.MarkAsSuccess"],
            "MarkAsFailed" => ["_txReceipts.Add", "BuildFailedReceipt", "otherTxTracer.MarkAsFailed", "_currentTxTracer.MarkAsFailed"],
            "BuildFailedReceipt" => ["BuildReceipt"],
            "BuildReceipt" => ["UpdateCumulativeGasTracking", "transaction.CalculateEffectiveGasPrice"],
            "UpdateCumulativeGasTracking" => ["BlockReceiptGasAccountingKernel.Accumulate", "_cumulativeBlockGasPerTx.Add", "Debug.Assert"],
            "TakeSnapshot" => [],
            "Restore" => ["_txReceipts.RemoveRange", "_cumulativeBlockGasPerTx.RemoveRange", "Debug.Assert", "BlockReceiptGasAccountingKernel.FromTotals"],
            "FinalizeTransaction" =>
            [
                "opts.HasFlag", "opts.HasFlag", "WorldState.Reset", "WorldState.DeleteAccount", "WorldState.AddToBalance",
                "DecrementNonce", "WorldState.Commit", "WorldState.Commit", "WorldState.ResetTransient", "WorldState.ReapEmptyAccounts",
                "WorldState.RecalculateStateRoot", "substate.Output.AsReadOnlyArray", "substate.EvmExceptionType.FastToString",
                "tracer.MarkAsFailed", "substate.LogsToArray", "tracer.MarkAsSuccess", "substate.Output.AsReadOnlyArray", "TransactionResult.EvmException",
            ],
            "CombineBlockGas" => ["BlockGasAccountingKernel.Combine"],
            "Combine" => ["Math.Max"],
            "CalculateEffectiveGasPrice" => ["UInt256.AddOverflow", "UInt256.Min"],
            "Accumulate" => ["FromTotals"],
            "FromTotals" => ["EthereumGasPolicy.CombineBlockGas"],
            _ => throw Rejected(member, "no admitted effect ledger"),
        };
        string[] expectedWrites = member switch
        {
            "MarkAsSuccess" or "MarkAsFailed" or "TakeSnapshot" or "CombineBlockGas" or "Combine" or
                "CalculateEffectiveGasPrice" or "FromTotals" => [],
            "BuildFailedReceipt" => ["initializer:receipt", "assignment:receipt.Error"],
            "BuildReceipt" =>
            [
                "initializer:cumulativeReceiptGas", "initializer:transaction", "initializer:baseFee", "initializer:effectiveGasPrice", "initializer:txReceipt",
                "assignment:Logs", "assignment:TxType", "assignment:GasUsedTotal", "assignment:StatusCode", "assignment:Recipient",
                "assignment:BlockHash", "assignment:BlockNumber", "assignment:Index", "assignment:GasUsed", "assignment:EffectiveGasPrice",
                "assignment:Sender", "assignment:ContractAddress", "assignment:TxHash", "assignment:PostTransactionState",
                "assignment:txReceipt.BlockGasUsed", "assignment:txReceipt.ExecutionGasUsed", "assignment:txReceipt.StorageGasUsed",
            ],
            "UpdateCumulativeGasTracking" =>
            [
                "assignment:(ulong prevExecution, ulong prevState)", "initializer:accounting",
                "assignment:Block.Header.GasUsed", "assignment:_cumulativeReceiptGas",
            ],
            "Restore" =>
            [
                "initializer:numToRemove", "assignment:(ulong cumulativeExecution, ulong cumulativeState)",
                "initializer:cumulativeReceipt", "initializer:accounting", "assignment:Block.Header.GasUsed", "assignment:_cumulativeReceiptGas",
            ],
            "FinalizeTransaction" =>
            [
                "assignment:tx.BlockGasUsed", "assignment:tx.SpentGas", "initializer:stateRoot", "assignment:stateRoot",
                "initializer:output", "initializer:error", "assignment:error", "initializer:logs",
            ],
            "Accumulate" => ["initializer:cumulativeExecutionGas", "initializer:cumulativeStateGas", "initializer:cumulativeReceiptGas"],
            _ => throw Rejected(member, "no admitted write ledger"),
        };
        string[] expectedAllocations = member switch
        {
            "BuildReceipt" => ["global::Nethermind.Core.TxReceipt"],
            "FromTotals" => ["global::Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingResult"],
            _ => [],
        };

        if (!calls.Select(static call => call.Children[0].Text).SequenceEqual(expectedCalls, StringComparer.Ordinal))
            throw Rejected(member, "unconsumed, missing or reordered invocation");
        if (!writes.Select(static write => write.Form + ":" + write.Target.Text).SequenceEqual(expectedWrites, StringComparer.Ordinal))
            throw Rejected(member, "unconsumed, missing or reordered write");
        if (!allocations.SequenceEqual(expectedAllocations, StringComparer.Ordinal))
            throw Rejected(member, "unconsumed, missing or reordered allocation");

        if (calls.Count != body.Invocations.Length || !calls.Zip(body.Invocations).All(pair =>
                pair.First.Start == pair.Second.Position && SameExpression(pair.First, pair.Second.Expression) &&
                pair.First.Children[1].Children.Length == pair.Second.Arguments.Length &&
                pair.First.Children[1].Children.Zip(pair.Second.Arguments).All(argument =>
                    SameExpression(argument.First.Children.LastOrDefault(), argument.Second))))
            throw Rejected(member, "invocation facts do not exhaust the statement expressions");
        if (writes.Count != body.Assignments.Length || !writes.Zip(body.Assignments).All(pair =>
                pair.First.Form == pair.Second.Form && pair.First.Target.Text == pair.Second.Target &&
                SameExpression(pair.First.Target, pair.Second.TargetExpression) && SameExpression(pair.First.Value, pair.Second.Value)))
            throw Rejected(member, "write facts do not exhaust the statement expressions");
        if (returns.Count > 1 || !SameExpression(returns.SingleOrDefault(), body.ReturnExpression))
            throw Rejected(member, "return fact differs from the complete statement tree");

        if (member is "BuildReceipt" or "BuildFailedReceipt")
        {
            string local = member == "BuildReceipt" ? "txReceipt" : "receipt";
            SemanticAssignment construction = body.Assignments.Single(assignment => assignment.Form == "initializer" && assignment.Target == local);
            SemanticExpression? constructed = construction.TargetExpression;
            SemanticExpression? returned = body.ReturnExpression;
            if (constructed is null || returned is null || returned.Kind != "IdentifierName" || returned.Text != local ||
                returned.SymbolId is null || returned.SymbolId != constructed.SymbolId || returned.TypeName != constructed.TypeName ||
                member == "BuildReceipt" && (construction.Value.Kind != "ImplicitObjectCreationExpression" ||
                    construction.Value.Children.Length != 2 || construction.Value.Children[0].Kind != "ArgumentList" ||
                    construction.Value.Children[0].Children.Length != 0 || construction.Value.Children[1].Kind != "ObjectInitializerExpression"))
                throw new ExtractionException($"RECEIPT_RETURN_BINDING: {member}: return does not name the constructed receipt local.");
        }

        void VisitStep(SemanticStep step)
        {
            if (step.Kind == "LocalDeclarationStatement")
            {
                if (step.DeclaredTarget is null || step.Expression is null)
                    throw Rejected(member, "local declaration has no unique initializer binding");
                writes.Add(("initializer", step.DeclaredTarget, step.Expression));
            }
            if (step.Kind is "ReturnStatement" or "ArrowExpressionClause") returns.Add(step.Expression);
            if (step.Expression is not null) VisitExpression(step.Expression);
            foreach (SemanticStep child in step.Children) VisitStep(child);
        }

        void VisitExpression(SemanticExpression expression)
        {
            switch (expression.Kind)
            {
                case "InvocationExpression":
                    if (expression.Children.Length != 2)
                        throw Rejected(member, "malformed invocation expression");
                    calls.Add(expression);
                    break;
                case "SimpleAssignmentExpression":
                    if (expression.Children.Length != 2)
                        throw Rejected(member, "malformed assignment expression");
                    writes.Add(("assignment", expression.Children[0], expression.Children[1]));
                    break;
                case "ImplicitObjectCreationExpression":
                    allocations.Add(expression.TypeName ?? "<untyped>");
                    break;
                case "Argument":
                    if ((expression.Text.StartsWith("out ", StringComparison.Ordinal) || expression.Text.StartsWith("ref ", StringComparison.Ordinal)) &&
                        (member != "CalculateEffectiveGasPrice" || expression.Text != "out UInt256 effectiveFee"))
                        throw Rejected(member, "unconsumed writable argument");
                    break;
                case "AddExpression": case "ArgumentList": case "BracketedArgumentList":
                case "CollectionExpression": case "ConditionalExpression": case "ConstantPattern":
                case "DeclarationExpression": case "DeclarationPattern": case "ElementAccessExpression":
                case "EqualsExpression": case "FalseLiteralExpression": case "TrueLiteralExpression": case "GreaterThanExpression":
                case "IdentifierName": case "IndexExpression": case "IsPatternExpression":
                case "LogicalAndExpression": case "LogicalNotExpression": case "LogicalOrExpression":
                case "NameColon": case "NotEqualsExpression": case "NotPattern": case "NullLiteralExpression":
                case "NumericLiteralExpression": case "ObjectInitializerExpression": case "ParenthesizedExpression":
                case "PredefinedType": case "SimpleMemberAccessExpression": case "SingleVariableDesignation":
                case "StringLiteralExpression": case "SubtractExpression": case "SuppressNullableWarningExpression":
                case "TupleExpression": case "UncheckedExpression":
                    break;
                default:
                    throw Rejected(member, $"unconsumed expression kind {expression.Kind}");
            }
            foreach (SemanticExpression child in expression.Children) VisitExpression(child);
        }
    }

    private static bool SameExpression(SemanticExpression? left, SemanticExpression? right) =>
        left is null ? right is null : right is not null && left.Kind == right.Kind && left.Text == right.Text &&
            left.SymbolId == right.SymbolId && left.TypeName == right.TypeName && left.Start == right.Start &&
            left.Children.Length == right.Children.Length && left.Children.Zip(right.Children).All(pair => SameExpression(pair.First, pair.Second));

    private static ExtractionException Rejected(string member, string reason) => new($"RECEIPT_EFFECT_LEDGER: {member}: {reason}.");
}
