// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    internal readonly ConcurrentDictionary<byte[], List<Hash256>> BlobIndex = new(Bytes.EqualityComparer);

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public bool TryGetBlobAndProof(byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[]? proof)
    {
        if (BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes)
            && txHashes[0] is not null
            && TryGetValue(txHashes[0], out Transaction? blobTx)
            && blobTx.BlobVersionedHashes?.Length > 0)
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

        blob = default;
        proof = default;
        return false;
    }

    public override bool TryInsert(ValueHash256 hash, Transaction blobTx, out Transaction? removed)
    {
        if (base.TryInsert(blobTx.Hash, blobTx, out removed))
        {
            AddToBlobIndex(blobTx);
            return true;
        }

        return false;
    }

    protected void AddToBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.Length == KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    BlobIndex.AddOrUpdate(blobVersionedHash,
                        k => [blobTx.Hash!],
                        (k, b) =>
                        {
                            b.Add(blobTx.Hash!);
                            return b;
                        });
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
                if (blobVersionedHash is not null
                    && BlobIndex.TryGetValue(blobVersionedHash, out List<Hash256>? txHashes))
                {
                    if (txHashes.Count < 2)
                    {
                        BlobIndex.Remove(blobVersionedHash, out _);
                    }
                    else
                    {
                        txHashes.Remove(blobTx.Hash!);
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
