// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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

    internal readonly Dictionary<byte[], List<Hash256>> BlobIndex = new(Bytes.EqualityComparer);

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public bool TryGetBlobAndProof(byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[]? proof)
    {
        using var lockRelease = Lock.Acquire();

        if (BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
        {
            foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
            {
                if (TryGetValueNonLocked(hash, out Transaction? blobTx) && blobTx.BlobVersionedHashes?.Length > 0)
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

    protected override bool InsertCore(ValueHash256 key, Transaction value, AddressAsKey groupKey)
    {
        if (base.InsertCore(key, value, groupKey))
        {
            AddToBlobIndex(value);
            return true;
        }

        return false;
    }

    private void AddToBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.Length == KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    ref List<Hash256>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(BlobIndex, blobVersionedHash, out _);
                    list ??= new List<Hash256>();
                    list.Add(blobTx.Hash!);
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
                if (blobVersionedHash is not null && BlobIndex.TryGetValue(blobVersionedHash, out List<Hash256>? txHashes))
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
        => base.TryGetValueNonLocked(hash, out lightBlobTx);
}
