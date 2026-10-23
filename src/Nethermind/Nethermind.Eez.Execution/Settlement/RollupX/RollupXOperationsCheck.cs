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
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (window.Count == 0 || operations.BlockCount != window.Count)
            throw new EezSettlementException("The operations do not cover the selected execution window.");

        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(window[^1].Block);
        plan.Validate(window[^1].Checkpoints);
        int index = 0;
        foreach (OperationsRun run in operations.Runs())
        {
            for (uint offset = 0; offset < run.Blocks; offset++)
            {
                token.ThrowIfCancellationRequested();
                EezStatelessBlockResult result = window[index];
                bool terminal = index == window.Count - 1;
                if (!terminal) VerifyIntermediate(result);
                VerifyBlock(run, result.Block, terminal ? plan.OrdinaryTransactions : result.Block.Transactions.Length, token);
                index++;
            }
        }
    }

    private static void VerifyBlock(OperationsRun run, Block block, int count, CancellationToken token)
    {
        if (run.Beneficiary != block.Header.Beneficiary || !run.ExtraData.Span.SequenceEqual(block.Header.ExtraData))
            throw new EezSettlementException($"The operations publish different header inputs for block {block.Number}.");
        if (run.Transactions.Length != count)
            throw new EezSettlementException($"The operations publish {run.Transactions.Length} ordinary transactions for block {block.Number}, expected {count}.");
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            Transaction transaction = block.Transactions[i];
            if (transaction.IsEezSystemTransaction() || transaction.SenderAddress == EezConstants.SystemAddress)
                throw new EezSettlementException($"Ordinary transaction {i} of block {block.Number} uses the reserved system identity.");
            if (!TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.AsSpan().SequenceEqual(run.Transactions[i].Span))
                throw new EezSettlementException($"The operations do not publish transaction {i} of block {block.Number}.");
        }
    }

    internal static void VerifyIntermediate(EezStatelessBlockResult result)
    {
        foreach (Transaction transaction in result.Block.Transactions)
            if (transaction.IsEezSystemTransaction() || transaction.SenderAddress == EezConstants.SystemAddress)
                throw new EezSettlementException($"Intermediate block {result.Block.Number} contains a system transaction.");
        if (OutboundEvent.Observe(result.Receipts, EezConstants.RollupXEezl2Address).Length != 0)
            throw new EezSettlementException($"Intermediate block {result.Block.Number} contains an outbound event.");
    }
}
