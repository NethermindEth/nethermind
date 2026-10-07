// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Binds a batch's DA payload to the window it settles. Blocks before the settling block are published whole. The
/// settling block is published without its system transactions, which derivation rebuilds from one action per
/// effect entry; the rebuilt Sync block must be byte for byte the block that was executed.
/// </summary>
public static class DaVerification
{
    /// <param name="window">The window's blocks, oldest first; the last is the settling block.</param>
    /// <exception cref="EezSettlementException">The payload does not publish exactly the window and its effects.</exception>
    public static void Verify(ReadOnlyMemory<byte> payload, IReadOnlyList<Block> window, AuthorizedOutbound[] outbound, AuthorizedInbound[] inbound,
        ulong chainId, ulong rollupId)
    {
        DaPayload decoded = DaPayloadCodec.Decode(payload);
        if (decoded.RollupId != rollupId)
        {
            throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, $"The DA payload carries rollup {decoded.RollupId}, not rollup {rollupId}.");
        }

        DaSpan span = decoded.Span;
        if (span.BlockCount != window.Count)
        {
            throw new EezSettlementException($"The DA payload covers {span.BlockCount} blocks, but the window has {window.Count}.");
        }

        int omitted = outbound.Length + inbound.Length;
        int[] omittedPositions = OmittedPositions(outbound, inbound);
        int published = 0;
        byte[][]? settlingTransactions = null;
        for (int blockIndex = 0; blockIndex < window.Count; blockIndex++)
        {
            Block block = window[blockIndex];
            bool settling = blockIndex == window.Count - 1;
            EnsureHeaderInputs(span, blockIndex, block);
            int expected = block.Transactions.Length - (settling ? omitted : 0);
            if (expected < 0)
            {
                throw new EezSettlementException(EezSettlementFailure.InternalInvariant, "The settling block has fewer transactions than effects.");
            }

            if (span.TransactionCounts[blockIndex] != expected)
            {
                throw new EezSettlementException($"The DA payload publishes {span.TransactionCounts[blockIndex]} transactions of block {block.Number}, not {expected}.");
            }

            byte[][] encoded = EncodeTransactions(block);
            int nextOmitted = 0;
            for (int i = 0; i < encoded.Length; i++)
            {
                if (settling && nextOmitted < omittedPositions.Length && omittedPositions[nextOmitted] == i)
                {
                    nextOmitted++;
                    continue;
                }

                if (published == span.Transactions.Length || !span.Transactions[published++].Span.SequenceEqual(encoded[i]))
                {
                    throw new EezSettlementException($"The DA payload does not publish transaction {i} of block {block.Number}.");
                }
            }

            if (settling)
            {
                if (nextOmitted != omittedPositions.Length)
                {
                    throw new EezSettlementException(EezSettlementFailure.InternalInvariant, "The effect transactions lie outside the settling block.");
                }

                settlingTransactions = encoded;
            }
        }

        if (published != span.Transactions.Length)
        {
            throw new EezSettlementException($"The DA payload publishes {span.Transactions.Length - published} transactions beyond the window.");
        }

        ExecutionEntry[] outboundEntries = VerifySidecars(decoded.Actions, outbound, inbound, rollupId);
        if (omitted != 0)
        {
            VerifySyncBlock(window[^1], settlingTransactions!, outbound, outboundEntries, inbound, chainId, rollupId);
        }
    }

    private static int[] OmittedPositions(AuthorizedOutbound[] outbound, AuthorizedInbound[] inbound)
    {
        int[] positions = new int[outbound.Length + inbound.Length];
        for (int i = 0; i < outbound.Length; i++)
        {
            positions[i] = outbound[i].LoadTransactionIndex;
        }

        for (int i = 0; i < inbound.Length; i++)
        {
            positions[outbound.Length + i] = inbound[i].TransactionIndex;
        }

        return positions;
    }

    private static void EnsureHeaderInputs(DaSpan span, int blockIndex, Block block)
    {
        if (span.Beneficiaries[blockIndex] != block.Header.Beneficiary)
        {
            throw new EezSettlementException($"The DA payload claims another beneficiary for block {block.Number}.");
        }

        if (!span.ExtraData[blockIndex].Span.SequenceEqual(block.Header.ExtraData))
        {
            throw new EezSettlementException($"The DA payload claims other extra data for block {block.Number}.");
        }
    }

    /// <returns>Each outbound call's DA entry, with the result DA publishes for it.</returns>
    private static ExecutionEntry[] VerifySidecars(DaAction[] actions, AuthorizedOutbound[] outbound, AuthorizedInbound[] inbound, ulong rollupId)
    {
        int expected = outbound.Length + inbound.Length;
        if (actions.Length != expected)
        {
            throw new EezSettlementException($"The DA payload carries {actions.Length} actions, but the batch has {expected} effects.");
        }

        ExecutionEntry[] outboundEntries = new ExecutionEntry[outbound.Length];
        for (int i = 0; i < actions.Length; i++)
        {
            ExecutionEntry published = actions[i].ToEntry(rollupId);
            ExecutionEntry derived;
            if (i < outbound.Length)
            {
                AuthorizedOutbound call = outbound[i];
                derived = call.BaseDaEntry with { ReturnData = published.ReturnData };
                if (RollingHash.CallEnd(call.PendingRollingHash, call.BaseDaEntry.Success, published.ReturnData) != call.ClaimedRollingHash)
                {
                    throw new EezSettlementException($"DA action {i} publishes a result the entry's rolling hash does not record.");
                }

                outboundEntries[i] = derived;
            }
            else
            {
                derived = inbound[i - outbound.Length].Observation.DerivedDaEntry;
            }

            if (!EezCalldata.EncodeEntry(published).AsSpan().SequenceEqual(EezCalldata.EncodeEntry(derived)))
            {
                throw new EezSettlementException($"DA action {i} does not rebuild the entry of its effect.");
            }
        }

        return outboundEntries;
    }

    private static void VerifySyncBlock(Block block, byte[][] encoded, AuthorizedOutbound[] outbound, ExecutionEntry[] outboundEntries, AuthorizedInbound[] inbound,
        ulong chainId, ulong rollupId)
    {
        int firstSystem = outbound.Length > 0 ? outbound[0].LoadTransactionIndex : inbound[0].TransactionIndex;
        (ExecutionEntry, byte[])[] outboundInputs = new (ExecutionEntry, byte[])[outbound.Length];
        for (int i = 0; i < outbound.Length; i++)
        {
            outboundInputs[i] = (outboundEntries[i], encoded[outbound[i].TransactionIndex]);
        }

        ExecutionEntry[] inboundEntries = new ExecutionEntry[inbound.Length];
        for (int i = 0; i < inbound.Length; i++)
        {
            inboundEntries[i] = inbound[i].Observation.DerivedDaEntry;
        }

        byte[][] rebuilt;
        try
        {
            rebuilt = SyncBlock.BuildTransactions(outboundInputs, inboundEntries, chainId, rollupId, block.Transactions[firstSystem].Nonce);
        }
        catch (EezSettlementException e)
        {
            throw new EezSettlementException(EezSettlementFailure.InternalInvariant, $"Authorized effects cannot be rebuilt into a Sync block: {e.Message}");
        }
        if (rebuilt.Length != encoded.Length)
        {
            throw new EezSettlementException($"The Sync block has {encoded.Length} transactions, but its effects rebuild {rebuilt.Length}.");
        }

        for (int i = 0; i < rebuilt.Length; i++)
        {
            if (!rebuilt[i].AsSpan().SequenceEqual(encoded[i]))
            {
                throw new EezSettlementException($"Sync block transaction {i} is not the one its effects rebuild.");
            }
        }
    }

    private static byte[][] EncodeTransactions(Block block)
    {
        byte[][] encoded = new byte[block.Transactions.Length][];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = TxDecoder.Instance.Encode(block.Transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        return encoded;
    }
}
