// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The check an attester runs before it signs a batch: re-execute the window statelessly, each block over its own
/// witness, then verify every settlement claim of the batch against it. A composer runs the same check before it asks
/// the attesters, so a batch it built wrong is caught locally instead of costing an attempt with each of them.
/// </summary>
public sealed class SettlementCheck(ISpecProvider specProvider, EezSettlementContext context, ILogManager logManager)
{
    private readonly EezStatelessExecutor _executor = new(specProvider, logManager);

    public EezSettlementContext Context => context;

    /// <summary>Re-executes the window, checkpointing the settling block at <see cref="SettlingBlock.CheckpointsOf"/>.</summary>
    /// <param name="claims">The hash and parent hash the composer sealed for each block, from <paramref name="fromBlock"/> up.</param>
    /// <exception cref="EezStatelessException">The window does not execute, or is not the window the composer claims.</exception>
    /// <exception cref="EezSettlementException">The batch claims more or fewer effects than the settling block holds.</exception>
    public EezStatelessBlockResult[] Execute(byte[] postBatchCalldata, IReadOnlyList<EezStatelessBlock> blocks, IReadOnlyList<(Hash256 Hash, Hash256 ParentHash)> claims,
        ulong fromBlock)
    {
        if (blocks.Count == 0 || claims.Count != blocks.Count)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, $"The window has {blocks.Count} blocks for {claims.Count} claims.");
        }

        Block settling = DecodeSettling(blocks[^1]);
        EnsureEffectCountCanMatch(postBatchCalldata, SettlingBlock.EffectTransactionsOf(settling).Length);
        int[] checkpoints = SettlingBlock.CheckpointsOf(settling);
        EezStatelessBlockResult[] executed = _executor.Execute(blocks, checkpoints);
        for (int i = 0; i < executed.Length; i++)
        {
            BlockHeader header = executed[i].Block.Header;
            if (header.Number != fromBlock + (ulong)i || header.Hash != claims[i].Hash || header.ParentHash != claims[i].ParentHash)
            {
                throw new EezStatelessException(EezStatelessFailure.Rejected, $"Block {header.Number} is not the block claimed at height {fromBlock + (ulong)i}.");
            }
        }

        return executed;
    }

    /// <returns>The public inputs hash the attesters sign.</returns>
    /// <exception cref="EezSettlementException">The batch claims something the executed window does not show.</exception>
    public ValueHash256 Verify(byte[] postBatchCalldata, IReadOnlyList<EezStatelessBlockResult> executed) =>
        EezSettlementVerifier.Verify(postBatchCalldata, executed, context, specProvider);

    private static Block DecodeSettling(EezStatelessBlock settling)
    {
        try
        {
            return Rlp.Decode<Block>(settling.Rlp) ?? throw new EezStatelessException(EezStatelessFailure.Rejected, "The settling block does not decode.");
        }
        catch (RlpException e)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, $"The settling block does not decode: {e.Message}");
        }
    }

    /// <summary>
    /// Refuses a batch that claims fewer or more effects than the settling block can hold before re-executing it,
    /// because every checkpoint of a padded settling block re-hashes the whole transaction prefix.
    /// </summary>
    private static void EnsureEffectCountCanMatch(byte[] calldata, int effectTransactions)
    {
        PostBatch batch;
        try
        {
            batch = EezCalldata.DecodePostAndVerifyBatch(calldata);
        }
        catch (EezAbiException)
        {
            return;
        }

        if (batch.Entries.Length - 1 != effectTransactions)
        {
            throw new EezSettlementException($"The batch claims {batch.Entries.Length - 1} effects for a settling block with {effectTransactions}.");
        }
    }
}
