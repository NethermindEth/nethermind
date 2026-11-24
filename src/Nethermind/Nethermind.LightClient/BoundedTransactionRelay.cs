// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.LightClient.Rpc;
using Nethermind.Network.Contract.Messages;
using Nethermind.TxPool;

namespace Nethermind.LightClient;

/// <summary>Retains locally validated submissions so peers can fetch announced blob sidecars.</summary>
internal sealed class BoundedTransactionRelay : ITxPool, IDisposable
{
    private readonly MemoryCache _transactions = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private readonly Lock _admission = new();

    internal bool AddVerified(MempoolTransaction transaction)
    {
        Hash256 hash = transaction.Hash!;
        lock (_admission)
        {
            long size = transaction.GetLength() * 2L + 1024;
            bool added = !_transactions.TryGetValue(hash, out Entry? entry);
            if (entry is null || entry.Transaction.PayerAddress != transaction.PayerAddress)
            {
                Transaction retainedTransaction = new();
                transaction.Transaction.CopyTo(retainedTransaction, copyHash: true);
                MempoolTransaction retained = transaction with { Transaction = retainedTransaction };
                if (entry is null) entry = new(retained);
                else entry.UpdateTransaction(retained);
            }
            _transactions.Set(hash, entry, new MemoryCacheEntryOptions { Size = size, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
            if (!_transactions.TryGetValue(hash, out _))
                throw new RpcException(-32005, "The bounded transaction relay is full.");
            return added;
        }
    }

    internal bool TryMarkAnnounced(Hash256 hash) =>
        _transactions.TryGetValue(hash, out Entry? entry) && entry!.TryMarkAnnounced();

    /// <summary>The sender's account after the announced local transactions that continue its nonce and fit its balance.</summary>
    /// <param name="nonceLimit">The first nonce not applied, so a submission can replace or repeat a pending transaction.</param>
    internal Account GetPendingAccount(Address address, Account account, ulong nonceLimit = ulong.MaxValue)
    {
        ulong nonce = account.Nonce;
        UInt256 balance = account.Balance;
        Dictionary<ulong, UInt256>? local = null;
        foreach (object key in _transactions.Keys)
        {
            if (_transactions.TryGetValue(key, out Entry? entry) && entry is { IsAnnounced: true, Transaction: { } tx } && tx.SenderAddress == address
                && tx.NonceKeys is null && tx.Nonce >= nonce && ExecutionData.TryGetSenderCost(tx, out UInt256 cost))
            {
                local ??= [];
                if (!local.TryGetValue(tx.Nonce, out UInt256 existing) || cost < existing) local[tx.Nonce] = cost;
            }
        }
        if (local is null) return account;
        while (nonce < nonceLimit && local.TryGetValue(nonce, out UInt256 cost) && cost <= balance)
        {
            balance -= cost;
            nonce++;
        }
        return account.WithChangedNonce(nonce).WithChangedBalance(balance);
    }

    public bool SupportsBlobs => true;
    public AcceptTxResult SubmitTx(MempoolTransaction tx, TxHandlingOptions txHandlingOptions) => AcceptTxResult.Invalid;
    public bool IsKnown(Hash256 hash) => _transactions.TryGetValue(hash, out _);
    public bool IsKnown(in ValueHash256 hash) => IsKnown(hash.ToCommitment());
    public bool ContainsTx(Hash256 hash, TxType txType) => TryGetPendingTransaction(hash, out MempoolTransaction? transaction) && transaction.Type == txType;
    public bool TryGetPendingTransaction(in ValueHash256 hash, [NotNullWhen(true)] out MempoolTransaction? transaction) =>
        TryGetTransaction(hash.ToCommitment(), out transaction);

    private bool TryGetTransaction(object hash, [NotNullWhen(true)] out MempoolTransaction? transaction)
    {
        transaction = _transactions.TryGetValue(hash, out Entry? entry) ? entry!.Transaction : null;
        return transaction is not null;
    }
    public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out MempoolTransaction? blobTransaction)
    {
        if (TryGetPendingTransaction(hash, out blobTransaction) && blobTransaction.CarriesBlobs) return true;
        blobTransaction = null;
        return false;
    }

    public MempoolTransaction[] GetPendingTransactions()
    {
        List<MempoolTransaction> result = [];
        foreach (object key in _transactions.Keys)
            if (TryGetTransaction(key, out MempoolTransaction? tx) && !tx.CarriesBlobs) result.Add(tx);
        return result.ToArray();
    }

    public int GetPendingTransactionsCount() => GetPendingTransactions().Length;
    public int GetPendingBlobTransactionsCount() => Math.Max(0, _transactions.Count - GetPendingTransactionsCount());
    public MempoolTransaction[] GetPendingTransactionsBySender(Address address)
    {
        List<MempoolTransaction> result = [];
        foreach (MempoolTransaction tx in GetPendingTransactions())
            if (tx.SenderAddress == address) result.Add(tx);
        result.Sort(static (left, right) => left.Nonce.CompareTo(right.Nonce));
        return result.ToArray();
    }

    public IDictionary<AddressAsKey, MempoolTransaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default) => Group(false);
    public IDictionary<AddressAsKey, MempoolTransaction[]> GetPendingLightBlobTransactionsBySender() => Group(true);
    public IDictionary<AddressAsKey, MempoolTransaction[]> GetPendingLightBlobTransactionsBySender(bool filterToReadyTx, UInt256 baseFee = default) => Group(true);

    private IDictionary<AddressAsKey, MempoolTransaction[]> Group(bool blobs)
    {
        Dictionary<AddressAsKey, List<MempoolTransaction>> groups = [];
        foreach (object key in _transactions.Keys)
        {
            if (!TryGetTransaction(key, out MempoolTransaction? tx) || tx.SenderAddress is null || tx.CarriesBlobs != blobs) continue;
            AddressAsKey sender = tx.SenderAddress;
            if (!groups.TryGetValue(sender, out List<MempoolTransaction>? bucket)) groups[sender] = bucket = [];
            bucket.Add(blobs ? WithoutBlobs(tx) : tx);
        }
        Dictionary<AddressAsKey, MempoolTransaction[]> result = [];
        foreach (KeyValuePair<AddressAsKey, List<MempoolTransaction>> group in groups)
        {
            group.Value.Sort(static (left, right) => left.Nonce.CompareTo(right.Nonce));
            result[group.Key] = group.Value.ToArray();
        }
        return result;
    }
    public bool RemoveTransaction(Hash256? hash)
    {
        if (hash is null || !IsKnown(hash)) return false;
        _transactions.Remove(hash);
        return true;
    }
    public bool EvictTransaction(MempoolTransaction tx) => RemoveTransaction(tx.Hash);
    public void ResetTxPoolState() => _transactions.Clear();

    public bool TryGetPendingBlobCellMask(in ValueHash256 hash, out BlobCellMask availableMask)
    {
        bool found = TryGetPendingBlobTransaction(hash.ToCommitment(), out MempoolTransaction? tx) && tx.NetworkWrapper is not null;
        availableMask = found ? tx!.NetworkWrapper!.GetAvailableCellMask() : BlobCellMask.Empty;
        return found;
    }

    public bool TryGetPendingBlobCellMetadata(Hash256 hash, out BlobCellMask availableMask, out int blobCount, out int materializationWork)
    {
        bool found = TryGetPendingBlobTransaction(hash, out MempoolTransaction? tx) && tx.NetworkWrapper is not null;
        blobCount = found ? tx!.Transaction.GetBlobCount() : 0;
        availableMask = found ? tx!.NetworkWrapper!.GetAvailableCellMask() : BlobCellMask.Empty;
        materializationWork = found ? checked(blobCount * BlobCellMask.CellCount) : 0;
        return found;
    }

    public bool TryGetBlobCells(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells)
    {
        cells = null;
        availableMask = BlobCellMask.Empty;
        if (!TryGetPendingBlobTransaction(hash, out MempoolTransaction? tx) || tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper
            || !BlobCellsHelper.TryGetFlattenedCells(wrapper, requestedMask, out byte[][] selected)) return false;
        availableMask = wrapper.GetAvailableCellMask() & requestedMask;
        cells = selected;
        return true;
    }

    public bool TryGetPendingTransactionWithoutBlobs(in ValueHash256 hash, [NotNullWhen(true)] out MempoolTransaction? transaction)
    {
        if (!TryGetPendingTransaction(hash, out transaction)) return false;
        transaction = WithoutBlobs(transaction);
        return true;
    }

    private static MempoolTransaction WithoutBlobs(MempoolTransaction transaction)
    {
        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper) return transaction;
        return transaction with { NetworkWrapper = wrapper with { Blobs = [], Cells = null, CellMask = BlobCellMask.Empty } };
    }

    public bool TryGetBlobAndProofV0(byte[] blobVersionedHash, [NotNullWhen(true)] out byte[]? blob, [NotNullWhen(true)] out byte[]? proof)
    {
        blob = proof = null;
        if (!FindBlob(blobVersionedHash, ProofVersion.V0, out ShardBlobNetworkWrapper? wrapper, out int index)) return false;
        blob = wrapper.Blobs[index];
        proof = wrapper.GetProofs(index).ToArray();
        return true;
    }

    public bool TryGetBlobAndProofV1(byte[] blobVersionedHash, [NotNullWhen(true)] out byte[]? blob, out ReadOnlyMemory<byte> cellProofs)
    {
        blob = null;
        cellProofs = default;
        if (!FindBlob(blobVersionedHash, ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper, out int index)) return false;
        blob = wrapper.Blobs[index];
        cellProofs = wrapper.GetProofs(index * BlobCellMask.CellCount, BlobCellMask.CellCount);
        return true;
    }

    public int TryGetBlobsAndProofsV1(byte[][] hashes, Span<byte[]?> blobs, Span<ReadOnlyMemory<byte>> proofs)
    {
        int found = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            if (!TryGetBlobAndProofV1(hashes[i], out byte[]? blob, out ReadOnlyMemory<byte> cellProofs)) continue;
            blobs[i] = blob;
            proofs[i] = cellProofs;
            found++;
        }
        return found;
    }

    public bool TryGetBlobCellsAndProofsV1(byte[] hash, BlobCellMask requestedMask, out BlobCellMask availableMask,
        [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
    {
        cells = proofs = null;
        availableMask = BlobCellMask.Empty;
        if (!FindBlob(hash, ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper, out int blobIndex)) return false;
        availableMask = requestedMask;
        if (requestedMask.IsEmpty) { cells = proofs = []; return true; }
        ShardBlobNetworkWrapper selected = new([wrapper.Blobs[blobIndex]], [wrapper.Commitments[blobIndex]], [], ProofVersion.V1);
        if (!BlobCellsHelper.TryGetFlattenedCells(selected, requestedMask, out byte[][] selectedCells)) return false;
        cells = selectedCells;
        proofs = new byte[requestedMask.Count][];
        int output = 0;
        foreach (int cell in requestedMask.EnumerateSetBits()) proofs[output++] = wrapper.GetProofs(blobIndex * BlobCellMask.CellCount + cell).ToArray();
        return true;
    }

    private bool FindBlob(byte[] hash, ProofVersion version, [NotNullWhen(true)] out ShardBlobNetworkWrapper? wrapper, out int index)
    {
        foreach (object key in _transactions.Keys)
        {
            if (!TryGetTransaction(key, out MempoolTransaction? tx) || tx.NetworkWrapper is not ShardBlobNetworkWrapper candidate
                || candidate.Version != version || tx.BlobVersionedHashes is not { } hashes) continue;
            for (int i = 0; i < hashes.Length; i++)
                if (hash.AsSpan().SequenceEqual(hashes[i])) { wrapper = candidate; index = i; return true; }
        }
        wrapper = null;
        index = -1;
        return false;
    }

    // Local RPC submits verified transactions directly; this relay does not maintain a full pending pool.
    void ITxPool.AddPeer(ITxPoolPeer peer) { }
    void ITxPool.RemovePeer(ITxPoolPeer peer) { }
    void ITxPool.RemovePeer(PublicKey nodeId) { }
    AnnounceResult ITxPool.NotifyAboutTx(in ValueHash256 txhash, IMessageHandler<PooledTransactionRequestMessage> retryHandler) =>
        AnnounceResult.RequestRequired;
    AcceptTxResult ITxPool.ValidateTxForBlobSampling(MempoolTransaction tx) => AcceptTxResult.Invalid;
    void ITxPool.ForgetRejectedBlobTransaction(Hash256 hash) { }
    bool ITxPool.ForgetInvalidBlobProofs(Hash256 hash) => false;
    bool ITxPool.TryMergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells) => false;
    BlobCellMergeResult ITxPool.MergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells) =>
        BlobCellMergeResult.TransactionUnavailable;
    MempoolTransaction? ITxPool.GetBestTx() => null;
    IEnumerable<MempoolTransaction> ITxPool.GetBestTxOfEachSender() => [];
    ulong ITxPool.GetLatestPendingNonce(Address address) => 0;
    bool ITxPool.AcceptTxWhenNotSynced { get; set; }
    long ITxPool.PendingTransactionsAdded => 0;
    MempoolTransaction[] IPendingTxsBySender.GetPendingLightBlobTransactionsBySender(Address address) => [];
    long IPendingTxsBySender.GetRemovalGeneration(Address sender) => 0;

    event EventHandler<Block>? ITxPool.TxPoolHeadChanged { add { } remove { } }
    event EventHandler<TxEventArgs> ITxPool.NewDiscovered { add { } remove { } }
    event EventHandler<TxEventArgs> ITxPool.NewPending { add { } remove { } }
    event EventHandler<TxEventArgs> ITxPool.RemovedPending { add { } remove { } }
    event EventHandler<TxEventArgs> ITxPool.EvictedPending { add { } remove { } }

    public void Dispose() => _transactions.Dispose();

    private sealed class Entry(MempoolTransaction transaction)
    {
        private int _announced;
        private MempoolTransaction _transaction = transaction;
        internal MempoolTransaction Transaction => Volatile.Read(ref _transaction);
        internal void UpdateTransaction(MempoolTransaction updated) => Volatile.Write(ref _transaction, updated);
        internal bool IsAnnounced => Volatile.Read(ref _announced) != 0;
        internal bool TryMarkAnnounced() => Interlocked.Exchange(ref _announced, 1) == 0;
    }
}
