// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
    : TxDistinctSortedPool(capacity, comparer, logManager)
{
    protected override string ShortPoolName => "BlobPool";

    internal readonly Dictionary<byte[], List<Hash256>> BlobIndex = new(Bytes.EqualityComparer);

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public bool TryGetBlobAndProofV0(
       byte[] requestedBlobVersionedHash,
       [NotNullWhen(true)] out byte[]? blob,
       [NotNullWhen(true)] out byte[]? proof) => TryGetBlobAndProof(requestedBlobVersionedHash, out blob, out proof, ProofVersion.V0,
           static (proofs, index) => proofs[index]);

    public bool TryGetBlobAndProofV1(
       byte[] requestedBlobVersionedHash,
       [NotNullWhen(true)] out byte[]? blob,
       [NotNullWhen(true)] out byte[][]? proof) => TryGetBlobAndProof(requestedBlobVersionedHash, out blob, out proof, ProofVersion.V1,
           static (proofs, index) => [.. proofs.Slice(Ckzg.CellsPerExtBlob * index, Ckzg.CellsPerExtBlob)]);

    private bool TryGetBlobAndProof<TProof>(
        byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out TProof? proof,
        ProofVersion requiredVersion,
        Func<byte[][], int, TProof> proofSelector)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();

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
                            if (wrapper is null || wrapper.Version != requiredVersion)
                            {
                                break;
                            }
                            blob = wrapper.Blobs[indexOfBlob];
                            proof = proofSelector(wrapper.Proofs, indexOfBlob)!;
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

    public int GetBlobCounts(byte[][] requestedBlobVersionedHashes)
    {
        using var lockRelease = Lock.Acquire();
        int count = 0;

        foreach (byte[] requestedBlobVersionedHash in requestedBlobVersionedHashes)
        {
            if (BlobIndex.ContainsKey(requestedBlobVersionedHash))
            {
                count += 1;
            }
        }

        return count;
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
