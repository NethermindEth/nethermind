// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>One L2 block as derivation rebuilds it from a batch's DA: its header inputs and its transactions.</summary>
public sealed record DerivedBlock(Address Beneficiary, byte[] ExtraData, byte[][] Transactions);

/// <summary>
/// Rebuilds the blocks a batch settles from its DA, the inverse of <see cref="DaVerification"/>. Blocks before the
/// settling block carry their published transactions. The settling block, the Sync block, gets a system
/// transaction per applied effect: each outbound load followed by the published user transaction that consumes it,
/// then one delivery per inbound effect, then the published transactions that pair with no effect. User
/// transactions paired with effects L1 did not apply are dropped.
/// </summary>
public static class BatchDerivation
{
    /// <param name="slice">The effects L1 applied, over the outbound effects followed by the inbound ones.</param>
    /// <param name="systemNonce">The system account's nonce at the parent of the batch's first block.</param>
    /// <exception cref="EezSettlementException">The DA cannot rebuild the blocks it claims.</exception>
    public static DerivedBlock[] Derive(DaPayload payload, ProducingSlice slice, ulong systemNonce, ulong chainId, ulong rollupId)
    {
        if (payload.RollupId != rollupId)
        {
            throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, $"The DA payload carries rollup {payload.RollupId}, not rollup {rollupId}.");
        }

        DaSpan span = payload.Span;
        if (span.BlockCount == 0)
        {
            throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, "The DA payload covers no blocks.");
        }

        DerivedBlock[] blocks = new DerivedBlock[span.BlockCount];
        int next = 0;
        for (int i = 0; i < blocks.Length - 1; i++)
        {
            blocks[i] = new DerivedBlock(span.Beneficiaries[i], span.ExtraData[i].ToArray(), Published(span, next, span.TransactionCounts[i]));
            next += span.TransactionCounts[i];
        }

        byte[][] syncUsers = Published(span, next, span.TransactionCounts[^1]);
        blocks[^1] = new DerivedBlock(span.Beneficiaries[^1], span.ExtraData[^1].ToArray(),
            SyncTransactions(payload.Actions, syncUsers, slice, systemNonce, chainId, rollupId));
        return blocks;
    }

    private static byte[][] SyncTransactions(DaAction[] actions, byte[][] users, ProducingSlice slice, ulong systemNonce, ulong chainId, ulong rollupId)
    {
        List<ExecutionEntry> outbound = new(actions.Length);
        List<ExecutionEntry> inbound = new(actions.Length);
        foreach (DaAction action in actions)
        {
            ExecutionEntry entry = action.ToEntry(rollupId);
            if (entry.Calls.Length == 0)
            {
                continue;
            }

            (entry.ProxyEntryHash == default ? outbound : inbound).Add(entry);
        }

        if (users.Length < outbound.Count)
        {
            throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload,
                $"The Sync block publishes {users.Length} user transactions for {outbound.Count} outbound effects.");
        }

        int outboundSkip = Math.Min(slice.Skip, outbound.Count);
        int outboundTake = Math.Min(slice.Take, outbound.Count - outboundSkip);
        int inboundSkip = Math.Min(slice.Skip - outboundSkip, inbound.Count);
        int inboundTake = Math.Min(Math.Max(slice.Take - outboundTake, 0), inbound.Count - inboundSkip);

        // The skipped effects already ran in this block on L1, so their system transactions used the nonces first.
        ulong skipped = (ulong)(outboundSkip + inboundSkip);
        if (systemNonce > ulong.MaxValue - skipped)
        {
            throw new EezSettlementException("The system account nonce overflows.");
        }

        ulong nonce = systemNonce + skipped;
        (ExecutionEntry, byte[])[] paired = new (ExecutionEntry, byte[])[outboundTake];
        for (int k = 0; k < outboundTake; k++)
        {
            paired[k] = (outbound[outboundSkip + k], users[outboundSkip + k]);
        }

        byte[][] effects = SyncBlock.BuildTransactions(paired, inbound.GetRange(inboundSkip, inboundTake), chainId, rollupId, nonce);
        int trailing = users.Length - outbound.Count;
        byte[][] transactions = new byte[effects.Length + trailing][];
        effects.CopyTo(transactions, 0);
        Array.Copy(users, outbound.Count, transactions, effects.Length, trailing);
        return transactions;
    }

    private static byte[][] Published(DaSpan span, int first, int count)
    {
        byte[][] transactions = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            transactions[i] = span.Transactions[first + i].ToArray();
        }

        return transactions;
    }
}
