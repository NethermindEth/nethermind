// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
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

        (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued) =
            SplitByNonce(standard, blobs, accountStateProvider.GetNonce(address));
        return new TxPoolSenderInfo(pending, queued);
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
        int total = (standardTransactions?.Length ?? 0) + (blobTransactions?.Length ?? 0);
        if (total == 0) return;

        (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued) =
            SplitByNonce(standardTransactions, blobTransactions, accountStateProvider.GetNonce(sender));
        if (pending.Count != 0) pendingTransactions[sender] = pending;
        if (queued.Count != 0) queuedTransactions[sender] = queued;
    }

    private void AddSenderToCounts(
        Address sender,
        Transaction[]? standardTransactions,
        Transaction[]? blobTransactions,
        ref int pendingTotal,
        ref int queuedTotal)
    {
        int total = (standardTransactions?.Length ?? 0) + (blobTransactions?.Length ?? 0);
        if (total == 0) return;

        int senderPending = CountPending(standardTransactions, blobTransactions, accountStateProvider.GetNonce(sender));
        pendingTotal += senderPending;
        queuedTotal += total - senderPending;
    }

    // Streams a two-pointer merge of two nonce-sorted bucket arrays from the standard and
    // blob pools (TxDistinctSortedPool sorts each bucket by nonce). Pending = txs whose
    // nonce continues from accountNonce; gap → queued. Mirrors Geth's split.
    // Note: TxTypeTxFilter prevents a sender from holding both types simultaneously, so
    // the merge case is rare in practice but the API handles it correctly anyway.
    private static (IDictionary<ulong, Transaction> pending, IDictionary<ulong, Transaction> queued)
        SplitByNonce(Transaction[]? standard, Transaction[]? blobs, UInt256 accountNonce)
    {
        Dictionary<ulong, Transaction> pending = new();
        Dictionary<ulong, Transaction> queued = new();
        UInt256 expectedNonce = accountNonce;

        int i = 0;
        int j = 0;
        int n = standard?.Length ?? 0;
        int m = blobs?.Length ?? 0;
        while (i < n || j < m)
        {
            Transaction next = j == m || (i < n && standard![i].Nonce <= blobs![j].Nonce)
                ? standard![i++]
                : blobs![j++];

            ulong nonce = (ulong)next.Nonce;
            if (next.Nonce == expectedNonce)
            {
                pending[nonce] = next;
                expectedNonce = next.Nonce + 1;
            }
            else
            {
                // Indexer (not Add) so a duplicate nonce — should be impossible given
                // TxTypeTxFilter, but defensive — does not crash the RPC handler.
                queued[nonce] = next;
            }
        }

        return (pending, queued);
    }

    private static int CountPending(Transaction[]? standard, Transaction[]? blobs, UInt256 accountNonce)
    {
        int pending = 0;
        UInt256 expectedNonce = accountNonce;

        int i = 0;
        int j = 0;
        int n = standard?.Length ?? 0;
        int m = blobs?.Length ?? 0;
        while (i < n || j < m)
        {
            Transaction next = j == m || (i < n && standard![i].Nonce <= blobs![j].Nonce)
                ? standard![i++]
                : blobs![j++];

            if (next.Nonce == expectedNonce)
            {
                pending++;
                expectedNonce += UInt256.One;
            }
        }

        return pending;
    }
}
