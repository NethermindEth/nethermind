// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class RollupXOperationsCheck
{
    public static void Verify(OperationsPayload operations, IReadOnlyList<EezStatelessBlockResult> window,
        CancellationToken token = default, EezChainRules? nativeRules = null)
    {
        token.ThrowIfCancellationRequested();
        if (window.Count == 0)
            throw new EezSettlementException("The operations do not cover the selected execution window.");
        Verify(operations, window[^1], window, default, token, nativeRules);
    }

    internal static void Verify(OperationsPayload operations, ReadOnlySpan<ValidatedRollupXBlock> preceding,
        EezStatelessBlockResult terminal, CancellationToken token, EezChainRules rules) =>
        Verify(operations, terminal, null, preceding, token, rules);

    private static void Verify(OperationsPayload operations, EezStatelessBlockResult terminal,
        IReadOnlyList<EezStatelessBlockResult>? window, ReadOnlySpan<ValidatedRollupXBlock> preceding,
        CancellationToken token, EezChainRules? nativeRules)
    {
        token.ThrowIfCancellationRequested();
        int count = window?.Count ?? preceding.Length + 1;
        if (operations.BlockCount != count)
            throw new EezSettlementException("The operations do not cover the selected execution window.");

        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal.Block);
        plan.Validate(terminal.Checkpoints);
        int index = 0;
        foreach (OperationsRun run in operations.Runs())
        {
            for (uint offset = 0; offset < run.Blocks; offset++)
            {
                token.ThrowIfCancellationRequested();
                if (index == count - 1)
                    VerifyBlock(run, terminal.Block, plan.OrdinaryTransactions, token, nativeRules);
                else if (window is not null)
                {
                    EezStatelessBlockResult result = window[index];
                    VerifyIntermediate(result, nativeRules);
                    VerifyBlock(run, result.Block, result.Block.Transactions.Length, token, nativeRules);
                }
                else VerifyBlock(run, in preceding[index], token);
                index++;
            }
        }
    }

    private static void VerifyBlock(OperationsRun run, Block block, int count, CancellationToken token, EezChainRules? nativeRules)
    {
        if (run.Beneficiary != block.Header.Beneficiary || !run.ExtraData.Span.SequenceEqual(block.Header.ExtraData))
            throw new EezSettlementException($"The operations publish different header inputs for block {block.Number}.");
        if (run.Transactions.Length != count)
            throw new EezSettlementException($"The operations publish {run.Transactions.Length} ordinary transactions for block {block.Number}, expected {count}.");
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            Transaction transaction = block.Transactions[i];
            if (!IsHistory(transaction, i, nativeRules) && (transaction.IsEezSystemTransaction() || transaction.SenderAddress ==
                (nativeRules?.SystemAddress ?? EezConstants.SystemAddress)))
                throw new EezSettlementException($"Ordinary transaction {i} of block {block.Number} uses the reserved system identity.");
            if (!TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).AsSpan().SequenceEqual(run.Transactions[i].Span))
                throw new EezSettlementException($"The operations do not publish transaction {i} of block {block.Number}.");
        }
    }

    private static void VerifyBlock(OperationsRun run, in ValidatedRollupXBlock block, CancellationToken token)
    {
        VerifyIntermediate(block.Failure, block.Number);
        if (run.Beneficiary != block.Beneficiary || !run.ExtraData.Span.SequenceEqual(block.ExtraData.Span))
            throw new EezSettlementException($"The operations publish different header inputs for block {block.Number}.");
        if (!block.MatchesTransactions(run.Transactions, token))
            throw new EezSettlementException($"The operations publish different transactions for block {block.Number}.");
    }

    internal static void VerifyIntermediate(EezStatelessBlockResult result, EezChainRules? nativeRules = null) =>
        VerifyIntermediate(CheckIntermediate(result, nativeRules), result.Block.Number);

    internal static IntermediateFailure CheckIntermediate(EezStatelessBlockResult result, EezChainRules? nativeRules)
    {
        for (int index = 0; index < result.Block.Transactions.Length; index++)
        {
            Transaction transaction = result.Block.Transactions[index];
            if (!IsHistory(transaction, index, nativeRules) && (transaction.IsEezSystemTransaction() ||
                transaction.SenderAddress == (nativeRules?.SystemAddress ?? EezConstants.SystemAddress)))
                return IntermediateFailure.SystemTransaction;
        }
        return OutboundEvent.Contains(result.Receipts, EezConstants.RollupXEezl2Address)
            ? IntermediateFailure.OutboundEvent
            : IntermediateFailure.None;
    }

    private static void VerifyIntermediate(IntermediateFailure failure, ulong number)
    {
        if (failure == IntermediateFailure.SystemTransaction)
            throw new EezSettlementException($"Intermediate block {number} contains a system transaction.");
        if (failure == IntermediateFailure.OutboundEvent)
            throw new EezSettlementException($"Intermediate block {number} contains an outbound event.");
    }

    internal enum IntermediateFailure : byte { None, SystemTransaction, OutboundEvent }
    private static bool IsHistory(Transaction transaction, int position, EezChainRules? rules) =>
        position == 0 && rules?.UsesNativeContract == true && transaction is NativeHistoryTransaction && transaction.HasSystemTransactionFields(rules);
}
