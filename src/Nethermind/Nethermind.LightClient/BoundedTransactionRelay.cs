// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.LightClient;

/// <summary>Retains locally validated submissions so peers can fetch announced blob sidecars.</summary>
internal sealed class BoundedTransactionRelay : NullTxPool, ITxPool, IDisposable
{
    private readonly MemoryCache _transactions = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });

    internal void AddVerified(Transaction transaction)
    {
        Hash256 hash = transaction.Hash!;
        long size = transaction.GetLength() * 2L + 1024;
        _transactions.Set(hash, transaction, new MemoryCacheEntryOptions { Size = size, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        if (!_transactions.TryGetValue(hash, out _))
            throw new RpcException(-32005, "The bounded transaction relay is full.");
    }

    public new bool SupportsBlobs => true;
    public new AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions) => AcceptTxResult.Invalid;
    public new bool IsKnown(Hash256 hash) => _transactions.TryGetValue(hash, out _);
    public new bool IsKnown(in ValueHash256 hash) => IsKnown(hash.ToCommitment());
    public new bool ContainsTx(Hash256 hash, TxType txType) => TryGetPendingTransaction(hash, out Transaction? transaction) && transaction.Type == txType;
    public new bool TryGetPendingTransaction(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? transaction) =>
        _transactions.TryGetValue(hash.ToCommitment(), out transaction);
    public new bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction)
    {
        if (TryGetPendingTransaction(hash, out blobTransaction) && blobTransaction.CarriesBlobs) return true;
        blobTransaction = null;
        return false;
    }

    public new Transaction[] GetPendingTransactions()
    {
        List<Transaction> result = [];
        foreach (object key in _transactions.Keys)
            if (_transactions.TryGetValue(key, out Transaction? tx) && tx is not null && !tx.CarriesBlobs) result.Add(tx);
        return result.ToArray();
    }

    public new int GetPendingTransactionsCount() => GetPendingTransactions().Length;
    public new int GetPendingBlobTransactionsCount() => Math.Max(0, _transactions.Count - GetPendingTransactionsCount());
    public new Transaction[] GetPendingTransactionsBySender(Address address)
    {
        List<Transaction> result = [];
        foreach (Transaction tx in GetPendingTransactions())
            if (tx.SenderAddress == address) result.Add(tx);
        result.Sort(static (left, right) => left.Nonce.CompareTo(right.Nonce));
        return result.ToArray();
    }

    public new IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default) => Group(false);
    public new IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender() => Group(true);
    public new IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender(bool filterToReadyTx, UInt256 baseFee = default) => Group(true);

    private IDictionary<AddressAsKey, Transaction[]> Group(bool blobs)
    {
        Dictionary<AddressAsKey, List<Transaction>> groups = [];
        foreach (object key in _transactions.Keys)
        {
            if (!_transactions.TryGetValue(key, out Transaction? tx) || tx is null || tx.SenderAddress is null || tx.CarriesBlobs != blobs) continue;
            AddressAsKey sender = tx.SenderAddress;
            if (!groups.TryGetValue(sender, out List<Transaction>? bucket)) groups[sender] = bucket = [];
            bucket.Add(blobs ? WithoutBlobs(tx) : tx);
        }
        Dictionary<AddressAsKey, Transaction[]> result = [];
        foreach (KeyValuePair<AddressAsKey, List<Transaction>> group in groups)
        {
            group.Value.Sort(static (left, right) => left.Nonce.CompareTo(right.Nonce));
            result[group.Key] = group.Value.ToArray();
        }
        return result;
    }
    public new bool RemoveTransaction(Hash256? hash)
    {
        if (hash is null || !IsKnown(hash)) return false;
        _transactions.Remove(hash);
        return true;
    }
    public new bool EvictTransaction(Transaction tx) => RemoveTransaction(tx.Hash);
    public new void ResetTxPoolState() => _transactions.Clear();

    public new bool TryGetPendingBlobCellMask(in ValueHash256 hash, out BlobCellMask availableMask)
    {
        bool found = TryGetPendingBlobTransaction(hash.ToCommitment(), out Transaction? tx) && tx.NetworkWrapper is ShardBlobNetworkWrapper;
        availableMask = found ? ((ShardBlobNetworkWrapper)tx!.NetworkWrapper!).GetAvailableCellMask() : BlobCellMask.Empty;
        return found;
    }

    public new bool TryGetPendingBlobCellMetadata(Hash256 hash, out BlobCellMask availableMask, out int blobCount, out int materializationWork)
    {
        bool found = TryGetPendingBlobTransaction(hash, out Transaction? tx) && tx.NetworkWrapper is ShardBlobNetworkWrapper;
        blobCount = found ? tx!.GetBlobCount() : 0;
        availableMask = found ? ((ShardBlobNetworkWrapper)tx!.NetworkWrapper!).GetAvailableCellMask() : BlobCellMask.Empty;
        materializationWork = found ? checked(blobCount * BlobCellMask.CellCount) : 0;
        return found;
    }

    public new bool TryGetBlobCells(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells)
    {
        cells = null;
        availableMask = BlobCellMask.Empty;
        if (!TryGetPendingBlobTransaction(hash, out Transaction? tx) || tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper
            || !BlobCellsHelper.TryGetFlattenedCells(wrapper, requestedMask, out byte[][] selected)) return false;
        availableMask = wrapper.GetAvailableCellMask() & requestedMask;
        cells = selected;
        return true;
    }

    public bool TryGetPendingTransactionWithoutBlobs(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? transaction)
    {
        if (!TryGetPendingTransaction(hash, out transaction)) return false;
        transaction = WithoutBlobs(transaction);
        return true;
    }

    private static Transaction WithoutBlobs(Transaction transaction)
    {
        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper) return transaction;
        Transaction result = new();
        transaction.CopyTo(result, copyHash: true);
        result.NetworkWrapper = wrapper with { Blobs = [], Cells = null, CellMask = BlobCellMask.Empty };
        return result;
    }

    public new bool TryGetBlobAndProofV0(byte[] blobVersionedHash, [NotNullWhen(true)] out byte[]? blob, [NotNullWhen(true)] out byte[]? proof)
    {
        blob = proof = null;
        if (!FindBlob(blobVersionedHash, ProofVersion.V0, out ShardBlobNetworkWrapper? wrapper, out int index)) return false;
        blob = wrapper.Blobs[index];
        proof = wrapper.Proofs[index];
        return true;
    }

    public new bool TryGetBlobAndProofV1(byte[] blobVersionedHash, [NotNullWhen(true)] out byte[]? blob, [NotNullWhen(true)] out byte[][]? cellProofs)
    {
        blob = null;
        cellProofs = null;
        if (!FindBlob(blobVersionedHash, ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper, out int index)) return false;
        blob = wrapper.Blobs[index];
        cellProofs = wrapper.Proofs.AsSpan(index * BlobCellMask.CellCount, BlobCellMask.CellCount).ToArray();
        return true;
    }

    public new int TryGetBlobsAndProofsV1(byte[][] hashes, Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs)
    {
        int found = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            if (!TryGetBlobAndProofV1(hashes[i], out byte[]? blob, out byte[][]? cellProofs)) continue;
            blobs[i] = blob;
            proofs[i] = cellProofs;
            found++;
        }
        return found;
    }

    public new bool TryGetBlobCellsAndProofsV1(byte[] hash, BlobCellMask requestedMask, out BlobCellMask availableMask,
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
        foreach (int cell in requestedMask.EnumerateSetBits()) proofs[output++] = wrapper.Proofs[blobIndex * BlobCellMask.CellCount + cell];
        return true;
    }

    private bool FindBlob(byte[] hash, ProofVersion version, [NotNullWhen(true)] out ShardBlobNetworkWrapper? wrapper, out int index)
    {
        foreach (object key in _transactions.Keys)
        {
            if (!_transactions.TryGetValue(key, out Transaction? tx) || tx?.NetworkWrapper is not ShardBlobNetworkWrapper candidate
                || candidate.Version != version || tx.BlobVersionedHashes is not { } hashes) continue;
            for (int i = 0; i < hashes.Length; i++)
                if (hash.AsSpan().SequenceEqual(hashes[i])) { wrapper = candidate; index = i; return true; }
        }
        wrapper = null;
        index = -1;
        return false;
    }

    public void Dispose() => _transactions.Dispose();
}
