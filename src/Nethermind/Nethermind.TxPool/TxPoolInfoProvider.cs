// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public class TxPoolInfoProvider(IAccountStateProvider accountStateProvider, ITxPool txPool) : ITxPoolInfoProvider
{
    public TxPoolInfoProvider(IChainHeadInfoProvider chainHeadInfoProvider, ITxPool txPool)
        : this(chainHeadInfoProvider.ReadOnlyStateProvider, txPool) { }

    public TxPoolInfo GetInfo()
    {
        IDictionary<AddressAsKey, Transaction[]> standardBySender = txPool.GetPendingTransactionsBySender();
        IDictionary<AddressAsKey, Transaction[]> blobBySender = txPool.GetPendingLightBlobTransactionsBySender();

        int senderEstimate = standardBySender.Count + blobBySender.Count;
        Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> pendingTransactions = new(senderEstimate);
        Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> queuedTransactions = new(senderEstimate);

        foreach (KeyValuePair<AddressAsKey, Transaction[]> group in standardBySender)
        {
            blobBySender.TryGetValue(group.Key, out Transaction[]? blobTransactions);
            AddSenderToInfo(group.Key, group.Value, blobTransactions, pendingTransactions, queuedTransactions);
        }

        foreach (KeyValuePair<AddressAsKey, Transaction[]> group in blobBySender)
        {
            if (standardBySender.ContainsKey(group.Key)) continue;
            AddSenderToInfo(group.Key, standardTransactions: null, group.Value, pendingTransactions, queuedTransactions);
        }

        return new TxPoolInfo(pendingTransactions, queuedTransactions);
    }

    public TxPoolSenderInfo GetSenderInfo(Address address)
    {
        Transaction[] standard = txPool.GetPendingTransactionsBySender(address);
        Transaction[] blobs = txPool.GetPendingLightBlobTransactionsBySender(address);
        if (standard.Length == 0 && blobs.Length == 0) return TxPoolSenderInfo.Empty;

        Transaction[] merged = MergeOwned(standard, blobs, out int mergedLength);
        try
        {
            (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued) =
                SplitByNonce(merged, mergedLength, accountStateProvider.GetNonce(address));
            return new TxPoolSenderInfo(pending, queued);
        }
        finally
        {
            ReturnIfPooled(merged, standard, blobs);
        }
    }

    public TxPoolCounts GetCounts()
    {
        IDictionary<AddressAsKey, Transaction[]> standardBySender = txPool.GetPendingTransactionsBySender();
        IDictionary<AddressAsKey, Transaction[]> blobBySender = txPool.GetPendingLightBlobTransactionsBySender();

        int pendingTotal = 0;
        int queuedTotal = 0;

        foreach (KeyValuePair<AddressAsKey, Transaction[]> group in standardBySender)
        {
            blobBySender.TryGetValue(group.Key, out Transaction[]? blobTransactions);
            AddSenderToCounts(group.Key, group.Value, blobTransactions, ref pendingTotal, ref queuedTotal);
        }

        foreach (KeyValuePair<AddressAsKey, Transaction[]> group in blobBySender)
        {
            if (standardBySender.ContainsKey(group.Key)) continue;
            AddSenderToCounts(group.Key, standardTransactions: null, group.Value, ref pendingTotal, ref queuedTotal);
        }

        return new TxPoolCounts(pendingTotal, queuedTotal);
    }

    private void AddSenderToInfo(
        Address sender,
        Transaction[]? standardTransactions,
        Transaction[]? blobTransactions,
        Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> pendingTransactions,
        Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> queuedTransactions)
    {
        Transaction[] merged = MergeOwned(standardTransactions, blobTransactions, out int mergedLength);
        if (mergedLength == 0) return;

        try
        {
            (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued) =
                SplitByNonce(merged, mergedLength, accountStateProvider.GetNonce(sender));
            if (pending.Count != 0) pendingTransactions[sender] = pending;
            if (queued.Count != 0) queuedTransactions[sender] = queued;
        }
        finally
        {
            ReturnIfPooled(merged, standardTransactions, blobTransactions);
        }
    }

    private void AddSenderToCounts(
        Address sender,
        Transaction[]? standardTransactions,
        Transaction[]? blobTransactions,
        ref int pendingTotal,
        ref int queuedTotal)
    {
        Transaction[] merged = MergeOwned(standardTransactions, blobTransactions, out int mergedLength);
        if (mergedLength == 0) return;

        try
        {
            int senderPending = CountPending(merged, mergedLength, accountStateProvider.GetNonce(sender));
            pendingTotal += senderPending;
            queuedTotal += mergedLength - senderPending;
        }
        finally
        {
            ReturnIfPooled(merged, standardTransactions, blobTransactions);
        }
    }

    // Returns either the existing input array (no allocation) or a pooled buffer that the caller
    // MUST return. `length` is the logical count regardless — pooled buffers may be oversized.
    private static Transaction[] MergeOwned(Transaction[]? standardTransactions, Transaction[]? blobTransactions, out int length)
    {
        int standardCount = standardTransactions?.Length ?? 0;
        int blobCount = blobTransactions?.Length ?? 0;
        length = standardCount + blobCount;

        if (blobCount == 0) return standardTransactions ?? [];
        if (standardCount == 0) return blobTransactions!;

        Transaction[] merged = SafeArrayPool<Transaction>.Shared.Rent(length);
        standardTransactions!.AsSpan().CopyTo(merged);
        blobTransactions!.AsSpan().CopyTo(merged.AsSpan(standardCount));
        return merged;
    }

    private static void ReturnIfPooled(Transaction[] merged, Transaction[]? standard, Transaction[]? blobs)
    {
        if (merged != standard && merged != blobs)
            SafeArrayPool<Transaction>.Shared.Return(merged, clearArray: true);
    }

    // Walks transactions in nonce order: txs whose nonce continues from accountNonce go to
    // pending, anything beyond a gap goes to queued. Mirrors Geth's pending/queued split.
    private static (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued)
        SplitByNonce(Transaction[] transactions, int length, UInt256 accountNonce)
    {
        Dictionary<ulong, Transaction> pending = new();
        Dictionary<ulong, Transaction> queued = new();
        UInt256 expectedNonce = accountNonce;

        using ArrayPoolListRef<Transaction> sorted = new(length);
        sorted.AddRange(transactions.AsSpan(0, length));
        sorted.Sort(NonceComparer.Instance);
        for (int i = 0; i < length; i++)
        {
            Transaction transaction = sorted[i];
            ulong transactionNonce = (ulong)transaction.Nonce;
            if (transaction.Nonce == expectedNonce)
            {
                pending[transactionNonce] = transaction;
                expectedNonce = transaction.Nonce + 1;
            }
            else
            {
                // Indexer (not Add) so a duplicate nonce — should be impossible given
                // TxTypeTxFilter, but defensive — does not crash the RPC handler.
                queued[transactionNonce] = transaction;
            }
        }

        return (pending, queued);
    }

    private static int CountPending(Transaction[] transactions, int length, UInt256 accountNonce)
    {
        int pending = 0;
        UInt256 expectedNonce = accountNonce;

        using ArrayPoolListRef<Transaction> sorted = new(length);
        sorted.AddRange(transactions.AsSpan(0, length));
        sorted.Sort(NonceComparer.Instance);
        for (int i = 0; i < length; i++)
        {
            if (sorted[i].Nonce == expectedNonce)
            {
                pending++;
                expectedNonce += UInt256.One;
            }
        }

        return pending;
    }

    private sealed class NonceComparer : IComparer<Transaction>
    {
        public static readonly NonceComparer Instance = new();
        public int Compare(Transaction? x, Transaction? y) => x!.Nonce.CompareTo(y!.Nonce);
    }
}
