// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Globalization;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool;

public class TxPoolInfoProvider(IAccountStateProvider accountStateProvider, ITxPool txPool) : ITxPoolInfoProvider
{
    public TxPoolInfoProvider(IChainHeadInfoProvider chainHeadInfoProvider, ITxPool txPool)
        : this(chainHeadInfoProvider.ReadOnlyStateProvider, txPool) { }

    // Blob txs are intentionally not exposed via txpool_content / txpool_contentFrom; matches
    // Geth's BlobPool.Content() returning empty stubs (see core/txpool/blobpool/blobpool.go).
    // GetCounts() still includes blobs for txpool_status parity.
    public TxPoolInfo GetInfo()
    {
        IDictionary<AddressAsKey, Transaction[]> standardBySender = txPool.GetPendingTransactionsBySender();

        Dictionary<AddressAsKey, IDictionary<string, Transaction>> pendingTransactions = new(standardBySender.Count);
        Dictionary<AddressAsKey, IDictionary<string, Transaction>> queuedTransactions = new(standardBySender.Count);

        foreach (KeyValuePair<AddressAsKey, Transaction[]> group in standardBySender)
        {
            AddSenderToInfo(group.Key, group.Value, blobTransactions: null, pendingTransactions, queuedTransactions);
        }

        return new TxPoolInfo(pendingTransactions, queuedTransactions);
    }

    public TxPoolSenderInfo GetSenderInfo(Address address)
    {
        Transaction[] standard = txPool.GetPendingTransactionsBySender(address);
        if (standard.Length == 0) return TxPoolSenderInfo.Empty;

        (IDictionary<string, Transaction> pending, IDictionary<string, Transaction> queued) =
            SplitByNonce(standard, blobs: null, accountStateProvider.GetNonce(address));
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
        Dictionary<AddressAsKey, IDictionary<string, Transaction>> pendingTransactions,
        Dictionary<AddressAsKey, IDictionary<string, Transaction>> queuedTransactions)
    {
        int total = (standardTransactions?.Length ?? 0) + (blobTransactions?.Length ?? 0);
        if (total == 0) return;

        (IDictionary<string, Transaction> pending, IDictionary<string, Transaction> queued) =
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

    /// <summary>Splits a sender's transactions into the <c>pending</c> and <c>queued</c> maps of <c>txpool_contentFrom</c>.</summary>
    /// <remarks>A transaction stays pending while its nonce is contiguous from the account nonce; an EIP-8250 keyed one is
    /// always pending, its sequence living in the nonce manager rather than the account nonce.</remarks>
    private static (IDictionary<string, Transaction> pending, IDictionary<string, Transaction> queued)
        SplitByNonce(Transaction[]? standard, Transaction[]? blobs, ulong accountNonce)
    {
        Dictionary<string, Transaction> pending = [];
        Dictionary<string, Transaction> queued = [];
        ulong expectedNonce = accountNonce;

        int i = 0;
        int j = 0;
        int n = standard?.Length ?? 0;
        int m = blobs?.Length ?? 0;
        while (i < n || j < m)
        {
            Transaction next = j == m || (i < n && standard![i].Nonce <= blobs![j].Nonce)
                ? standard![i++]
                : blobs![j++];

            if (KeyedNonceManager.UsesKeyedNonce(next))
            {
                pending[KeyOf(next)] = next;
            }
            else if (next.Nonce == expectedNonce)
            {
                pending[KeyOf(next)] = next;
                expectedNonce = next.Nonce + 1;
            }
            else
            {
                // Indexer (not Add) so a duplicate nonce should not crash the RPC handler.
                queued[KeyOf(next)] = next;
            }
        }

        return (pending, queued);
    }

    /// <summary>The key under which <paramref name="tx"/> is listed in <c>txpool_content</c>.</summary>
    /// <remarks>The nonce identifies a sender's transaction, but under EIP-8250 a sender can hold several includable ones
    /// at the same sequence, so keyed transactions are keyed by hash instead.</remarks>
    private static string KeyOf(Transaction tx) =>
        KeyedNonceManager.UsesKeyedNonce(tx)
            ? tx.Hash!.ToString()
            : tx.Nonce.ToString(CultureInfo.InvariantCulture);

    private static int CountPending(Transaction[]? standard, Transaction[]? blobs, ulong accountNonce)
    {
        int pending = 0;
        ulong expectedNonce = accountNonce;

        int i = 0;
        int j = 0;
        int n = standard?.Length ?? 0;
        int m = blobs?.Length ?? 0;
        while (i < n || j < m)
        {
            Transaction next = j == m || (i < n && standard![i].Nonce <= blobs![j].Nonce)
                ? standard![i++]
                : blobs![j++];

            if (KeyedNonceManager.UsesKeyedNonce(next))
            {
                pending++;
            }
            else if (next.Nonce == expectedNonce)
            {
                pending++;
                expectedNonce++;
            }
        }

        return pending;
    }
}
