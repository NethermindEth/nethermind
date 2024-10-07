// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
    : TxDistinctSortedPool(capacity, comparer, logManager)
{
    protected override string ShortPoolName => "BlobPool";

    internal readonly ConcurrentDictionary<byte[], ConcurrentHashSet<Hash256>> BlobIndex = new(Bytes.EqualityComparer);

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public bool TryGetBlobAndProof(byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[]? proof)
    {
        if (BlobIndex.TryGetValue(requestedBlobVersionedHash, out ConcurrentHashSet<Hash256>? txHashes))
        {
            foreach (Hash256 hash in txHashes)
            {
                if (TryGetValue(hash, out Transaction? blobTx) && blobTx.BlobVersionedHashes?.Length > 0)
                {
                    for (int indexOfBlob = 0; indexOfBlob < blobTx.BlobVersionedHashes.Length; indexOfBlob++)
                    {
                        if (Bytes.AreEqual(blobTx.BlobVersionedHashes[indexOfBlob], requestedBlobVersionedHash)
                            && blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
                        {
                            blob = wrapper.Blobs[indexOfBlob];
                            proof = wrapper.Proofs[indexOfBlob];
                            return true;
                        }
                    }
                }
            }
        }

        blob = default;
        proof = default;
        return false;
    }

    protected override void InsertCore(ValueHash256 key, Transaction value, AddressAsKey groupKey)
    {
        base.InsertCore(key, value, groupKey);
        AddToBlobIndex(value);
    }

    protected void AddToBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.Length == KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    ConcurrentHashSet<Hash256> set = BlobIndex.GetOrAdd(blobVersionedHash, static _ => new ConcurrentHashSet<Hash256>());
                    set.Add(blobTx.Hash!);
                }
            }
        }
    }

    protected override bool Remove(ValueHash256 hash, out Transaction? tx)
    {
        if (base.Remove(hash, out tx))
        {
            if (tx is not null)
            {
                RemoveFromBlobIndex(tx);
            }
            return true;
        }

        return false;
    }

    private void RemoveFromBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash is not null && BlobIndex.TryGetValue(blobVersionedHash, out ConcurrentHashSet<Hash256>? txHashes))
                {
                    txHashes.TryRemove(blobTx.Hash!);
                    if (txHashes.Count == 0)
                    {
                        BlobIndex.Remove(blobVersionedHash, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);
}
