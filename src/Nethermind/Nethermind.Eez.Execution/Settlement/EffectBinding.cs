// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Binds a batch's entries to the settling block: one leading anchor, then one inbound or outbound entry per effect
/// transaction, each claiming the block that ends with that transaction. With effects the anchor claims the settling
/// block's empty prefix, so every claim names a block at the settling height and a short settlement replaces a sibling
/// rather than retreating. Without effects the anchor is the batch's only entry and the settling block holds no
/// transactions.
/// </summary>
public static class EffectBinding
{
    /// <param name="updates">The state updates <see cref="RollupUpdateChain.Verify"/> returned for the batch.</param>
    /// <param name="checkpoints">
    /// The settling block's checkpoints: its empty prefix when it has transactions, then one per effect transaction.
    /// </param>
    /// <param name="effectTransactions">The settling block's effect transactions, ascending.</param>
    /// <param name="systemTransactions">Whether each transaction of the settling block is a system transaction.</param>
    /// <exception cref="EezSettlementException">The entries do not describe the settling block's effects.</exception>
    public static BoundEffect[] Bind(PostBatch batch, RollupUpdate[] updates, ulong rollupId,
        ReadOnlySpan<EezTransactionCheckpoint> checkpoints, ReadOnlySpan<int> effectTransactions, ReadOnlySpan<bool> systemTransactions)
    {
        ExecutionEntry[] entries = batch.Entries;
        EntryShape leading = EntryShapes.Classify(entries[0], updates[0], rollupId);
        if (leading != EntryShape.Anchor)
        {
            throw new EezSettlementException($"The leading entry is {leading}, not an anchor.");
        }

        EntryShape[] shapes = new EntryShape[entries.Length];
        for (int i = 1; i < entries.Length; i++)
        {
            shapes[i] = EntryShapes.Classify(entries[i], updates[i], rollupId);
            if (shapes[i] == EntryShape.Anchor)
            {
                throw new EezSettlementException($"Entry {i} is a second anchor.");
            }

            if (shapes[i] == EntryShape.Invalid)
            {
                throw new EezSettlementException($"Entry {i} is not a valid effect.");
            }
        }

        int claimed = entries.Length - 1;
        if (claimed != effectTransactions.Length)
        {
            throw new EezSettlementException($"The batch claims {claimed} effects, but the settling block has {effectTransactions.Length}.");
        }

        if (!updates[0].EtherDelta.IsZero)
        {
            throw new EezSettlementException($"The anchor moves {updates[0].EtherDelta} ether.");
        }

        if (claimed == 0)
        {
            return checkpoints.Length == 0 ? [] : throw new EezSettlementException("A batch without effects must settle a block without transactions.");
        }

        if (checkpoints.Length != claimed + 1)
        {
            throw CheckpointCount(claimed + 1, checkpoints.Length);
        }

        if (checkpoints[0].TransactionIndex != EezTransactionCheckpoint.PreExecution)
        {
            throw new EezSettlementException(EezSettlementFailure.InternalInvariant,
                $"The anchor's checkpoint is at transaction {checkpoints[0].TransactionIndex}, not at the empty prefix.");
        }

        if (updates[0].NewRoot != checkpoints[0].BlockHash.ValueHash256)
        {
            throw new EezSettlementException($"The anchor ends at {updates[0].NewRoot}, not at the settling block's empty prefix {checkpoints[0].BlockHash}.");
        }

        ReadOnlySpan<EezTransactionCheckpoint> effectCheckpoints = checkpoints[1..];
        BoundEffect[] effects = new BoundEffect[claimed];
        for (int i = 0; i < claimed; i++)
        {
            int entryIndex = i + 1;
            int transactionIndex = effectTransactions[i];
            EntryShape claimedShape = shapes[entryIndex];
            EntryShape observedShape = systemTransactions[transactionIndex] ? EntryShape.Inbound : EntryShape.Outbound;
            if (claimedShape != observedShape)
            {
                throw new EezSettlementException($"Entry {entryIndex} is {claimedShape}, but transaction {transactionIndex} is {observedShape}.");
            }

            if (effectCheckpoints[i].TransactionIndex != transactionIndex)
            {
                throw new EezSettlementException(EezSettlementFailure.InternalInvariant,
                    $"Checkpoint {i} is at transaction {effectCheckpoints[i].TransactionIndex}, not at effect transaction {transactionIndex}.");
            }

            if (updates[entryIndex].NewRoot != effectCheckpoints[i].BlockHash.ValueHash256)
            {
                throw new EezSettlementException(
                    $"Entry {entryIndex} claims block {updates[entryIndex].NewRoot}, but transaction {transactionIndex} ends block {effectCheckpoints[i].BlockHash}.");
            }

            effects[i] = new BoundEffect(entryIndex, transactionIndex, claimedShape, entries[entryIndex], updates[entryIndex]);
        }

        return effects;
    }

    private static EezSettlementException CheckpointCount(int expected, int actual) =>
        new(EezSettlementFailure.InternalInvariant, $"The settlement needs {expected} transaction checkpoints, but {actual} were computed.");
}
