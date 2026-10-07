// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Follower;

/// <summary>A <c>postAndVerifyBatch</c> mined on L1, with what it claims for our rollup.</summary>
/// <param name="VerifiesOurRollup">The batch lists our rollup, so it clears our queue on L1 and ends the previous batch's window.</param>
/// <param name="ClaimedCurrentState">The first state update's current state for our rollup, if the batch updates it.</param>
/// <param name="ClaimedChain">Every new state the batch claims for our rollup, in entry order.</param>
public sealed record L1Batch(ulong BlockNumber, Hash256 BlockHash, Hash256 TransactionHash, ulong TransactionIndex, bool VerifiesOurRollup,
    ValueHash256? ClaimedCurrentState, ValueHash256[] ClaimedChain, ReadOnlyMemory<byte> CallData)
{
    public static L1Batch Of(PostBatch batch, ulong rollupId, ulong blockNumber, Hash256 blockHash, Hash256 transactionHash, ulong transactionIndex)
    {
        ValueHash256? current = null;
        List<ValueHash256> chain = [];
        foreach (ExecutionEntry entry in batch.Entries)
        {
            foreach (StateUpdate update in entry.StateUpdates)
            {
                if (update.RollupId == rollupId)
                {
                    current ??= update.CurrentState;
                    chain.Add(update.NewState);
                }
            }
        }

        return new L1Batch(blockNumber, blockHash, transactionHash, transactionIndex, Lists(batch, rollupId), current, [.. chain], batch.CallData);
    }

    private static bool Lists(PostBatch batch, ulong rollupId)
    {
        foreach (RollupProofSystems rollup in batch.RollupIdsWithProofSystems)
        {
            if (rollup.RollupId == rollupId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>An <c>L2ExecutionPerformed</c> L1 emitted for our rollup: one settled step.</summary>
public readonly record struct SettledRoot(ulong BlockNumber, Hash256 BlockHash, ulong TransactionIndex, ulong LogIndex, ValueHash256 Root);
