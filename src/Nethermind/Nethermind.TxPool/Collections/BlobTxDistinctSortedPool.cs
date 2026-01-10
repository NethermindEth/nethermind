// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
    : TxDistinctSortedPool(capacity, comparer, logManager)
{
    protected override string ShortPoolName => "BlobPool";

    internal readonly Dictionary<byte[], List<Hash256>> BlobIndex = new(Bytes.EqualityComparer);

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public void TryGetBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes, ArrayPoolList<BlobAndProofV1?> results) =>
        TryGetBlobsAndProofsCore(
            requestedBlobVersionedHashes,
            results,
            ProofVersion.V0,
            static (wrapper, blobIndex) => new BlobAndProofV1(wrapper.Blobs[blobIndex], wrapper.Proofs[blobIndex]));

    public void TryGetBlobsAndProofsV2(byte[][] requestedBlobVersionedHashes, ArrayPoolList<BlobAndProofV2?> results) =>
        TryGetBlobsAndProofsCore(
            requestedBlobVersionedHashes,
            results,
            ProofVersion.V1,
            static (wrapper, blobIndex) => new BlobAndProofV2(
                wrapper.Blobs[blobIndex],
                [.. wrapper.Proofs.Slice(Ckzg.CellsPerExtBlob * blobIndex, Ckzg.CellsPerExtBlob)]));

    protected virtual void TryGetBlobsAndProofsCore<TResult>(
        byte[][] requestedBlobVersionedHashes,
        ArrayPoolList<TResult?> results,
        ProofVersion requiredVersion,
        Func<ShardBlobNetworkWrapper, int, TResult> createResult)
        where TResult : struct
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();

        for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
        {
            byte[] blobHash = requestedBlobVersionedHashes[i];

            if (!BlobIndex.TryGetValue(blobHash, out List<Hash256>? txHashes))
            {
                results.Add(null);
                continue;
            }

            bool found = false;
            foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
            {
                if (!TryGetValueNonLocked(hash, out Transaction? blobTx) || blobTx.BlobVersionedHashes is null)
                    continue;

                int blobIndex = FindBlobIndex(blobTx.BlobVersionedHashes, blobHash);
                if (blobIndex < 0)
                    continue;

                if (blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper && wrapper.Version == requiredVersion)
                {
                    results.Add(createResult(wrapper, blobIndex));
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                results.Add(null);
            }
        }
    }

    protected static int FindBlobIndex(byte[]?[] blobVersionedHashes, byte[] targetHash)
    {
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            if (blobVersionedHashes[i] is not null && Bytes.AreEqual(blobVersionedHashes[i], targetHash))
                return i;
        }
        return -1;
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
                if (blobVersionedHash?.Length == Eip4844Constants.BytesPerBlobVersionedHash)
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
