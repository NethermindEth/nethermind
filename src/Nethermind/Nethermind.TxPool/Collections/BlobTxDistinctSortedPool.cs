// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
                            if (wrapper.Version != requiredVersion || !wrapper.HasFullBlobs())
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

    public virtual int TryGetBlobsAndProofsV1(
        byte[][] requestedBlobVersionedHashes,
        Span<byte[]?> blobs,
        Span<ReadOnlyMemory<byte[]>> proofs)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();
        int found = 0;

        for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
        {
            byte[] requestedBlobVersionedHash = requestedBlobVersionedHashes[i];
            if (!BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
                continue;

            foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
            {
                if (!TryGetValueNonLocked(hash, out Transaction? blobTx)
                    || blobTx.BlobVersionedHashes is not { Length: > 0 })
                    continue;

                bool matched = false;
                for (int indexOfBlob = 0; indexOfBlob < blobTx.BlobVersionedHashes.Length; indexOfBlob++)
                {
                    if (Bytes.AreEqual(blobTx.BlobVersionedHashes[indexOfBlob], requestedBlobVersionedHash)
                        && blobTx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper)
                    {
                        if (!wrapper.HasFullBlobs())
                        {
                            break;
                        }

                        blobs[i] = wrapper.Blobs[indexOfBlob];
                        proofs[i] = new ReadOnlyMemory<byte[]>(
                            wrapper.Proofs,
                            Ckzg.CellsPerExtBlob * indexOfBlob,
                            Ckzg.CellsPerExtBlob);
                        found++;
                        matched = true;
                        break;
                    }
                }
                if (matched) break;
            }
        }
        return found;
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
            foreach (byte[]? blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.Length == Eip4844Constants.BytesPerBlobVersionedHash)
                {
                    ref List<Hash256>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(BlobIndex, blobVersionedHash, out _);
                    list ??= [];
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

    /// <summary>
    /// Gets the cell availability mask of a pooled blob transaction without touching blob payloads,
    /// the blob cache, or persistent storage.
    /// </summary>
    /// <returns><c>true</c> when the transaction is present in the blob pool.</returns>
    public bool TryGetAvailableCellMask(ValueHash256 hash, out BlobCellMask availableMask)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();
        if (!base.TryGetValueNonLocked(hash, out Transaction? blobTx))
        {
            availableMask = default;
            return false;
        }

        availableMask = blobTx switch
        {
            LightTransaction lightTx => lightTx.BlobCellMask,
            { NetworkWrapper: ShardBlobNetworkWrapper wrapper } => wrapper.GetAvailableCellMask(),
            _ => default
        };
        return true;
    }

    public virtual bool TryGetCells(ValueHash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, out byte[][]? cells)
    {
        ShardBlobNetworkWrapper? wrapper = null;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (TryGetValueNonLocked(hash, out Transaction? blobTx)
                && blobTx.NetworkWrapper is ShardBlobNetworkWrapper currentWrapper)
            {
                wrapper = currentWrapper;
            }
        }

        if (wrapper is not null
            && BlobCellsHelper.TryGetFlattenedCells(wrapper, requestedMask, out byte[][] flattenedCells))
        {
            availableMask = wrapper.GetAvailableCellMask() & requestedMask;
            cells = flattenedCells;
            return true;
        }

        availableMask = default;
        cells = default;
        return false;
    }

    public virtual bool TryGetBlobCellsAndProofsV1(
        byte[] requestedBlobVersionedHash,
        BlobCellMask requestedMask,
        out BlobCellMask availableMask,
        out byte[][]? cells,
        out byte[][]? proofs)
    {
        List<BlobCellsCandidate>? candidates = null;

        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
            {
                candidates = new(txHashes.Count);
                foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
                {
                    if (!TryGetValueNonLocked(hash, out Transaction? blobTx)
                        || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } currentWrapper
                        || !TryFindBlobIndex(blobTx, requestedBlobVersionedHash, out int blobIndex))
                    {
                        continue;
                    }

                    candidates.Add(new BlobCellsCandidate(currentWrapper, blobIndex));
                }
            }
        }

        return TryBuildBlobCellsAndProofsResponse(candidates, requestedMask, out availableMask, out cells, out proofs);
    }

    public bool TryMergeCells(ValueHash256 hash, BlobCellMask cellMask, byte[][] cells)
    {
        ShardBlobNetworkWrapper wrapper;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (!TryGetValueNonLocked(hash, out Transaction? blobTx)
                || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper currentWrapper
                || currentWrapper.Version is not ProofVersion.V1
                || cellMask.IsEmpty)
            {
                return false;
            }

            if (currentWrapper.HasFullBlobs())
            {
                return true;
            }

            if (cells.Length != currentWrapper.Commitments.Length * cellMask.Count)
            {
                return false;
            }

            wrapper = currentWrapper;
        }

        BlobCellMask mergedMask = wrapper.CellMask | cellMask;
        byte[][] mergedCells = BlobCellsHelper.MergeFlattenedCells(wrapper.Cells, wrapper.CellMask, cells, cellMask, wrapper.Commitments.Length);
        ShardBlobNetworkWrapper mergedWrapper = wrapper with { CellMask = mergedMask, Cells = mergedCells };
        if (!BlobCellsHelper.ValidateCells(mergedWrapper))
        {
            return false;
        }

        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (!TryGetValueNonLocked(hash, out Transaction? blobTx)
                || !ReferenceEquals(blobTx.NetworkWrapper, wrapper))
            {
                return false;
            }

            blobTx.NetworkWrapper = mergedWrapper;
            blobTx.ClearLengthCache();
            OnBlobTransactionUpdatedNonLocked(blobTx);
            return true;
        }
    }

    protected virtual void OnBlobTransactionUpdatedNonLocked(Transaction blobTx)
    {
    }

    private void RemoveFromBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (byte[]? blobVersionedHash in blobTx.BlobVersionedHashes)
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
    /// Gets the in-memory (light) pool entry without touching the blob cache or persistent storage.
    /// </summary>
    /// <remarks>Must be called under the pool lock.</remarks>
    internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? lightBlobTx)
        => base.TryGetValueNonLocked(hash, out lightBlobTx);

    protected static bool TryFindBlobIndex(Transaction blobTx, byte[] requestedBlobVersionedHash, out int blobIndex)
    {
        if (blobTx.BlobVersionedHashes is { Length: > 0 } blobVersionedHashes)
        {
            for (int i = 0; i < blobVersionedHashes.Length; i++)
            {
                if (Bytes.AreEqual(blobVersionedHashes[i], requestedBlobVersionedHash))
                {
                    blobIndex = i;
                    return true;
                }
            }
        }

        blobIndex = -1;
        return false;
    }

    protected static bool TryBuildBlobCellsAndProofsResponse(
        List<BlobCellsCandidate>? candidates,
        BlobCellMask requestedMask,
        out BlobCellMask availableMask,
        out byte[][]? cells,
        out byte[][]? proofs)
    {
        if (candidates is null)
        {
            availableMask = default;
            cells = default;
            proofs = default;
            return false;
        }

        bool hasFallback = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            BlobCellsCandidate candidate = candidates[i];
            BlobCellMask candidateMask = candidate.Wrapper.GetAvailableCellMask() & requestedMask;
            if (candidateMask.IsEmpty)
            {
                hasFallback = true;
                continue;
            }

            if (TryGetFlattenedCellsForBlob(candidate.Wrapper, candidate.BlobIndex, requestedMask, out cells))
            {
                availableMask = candidateMask;
                proofs = BlobCellsHelper.SelectProofs(candidate.Wrapper, candidate.BlobIndex, requestedMask);
                return true;
            }

            hasFallback = true;
        }

        if (hasFallback)
        {
            availableMask = BlobCellMask.Empty;
            cells = [];
            proofs = [];
            return true;
        }

        availableMask = default;
        cells = default;
        proofs = default;
        return false;
    }

    protected static bool TryGetFlattenedCellsForBlob(ShardBlobNetworkWrapper wrapper, int blobIndex, BlobCellMask requestedMask, out byte[][]? cells)
    {
        BlobCellMask availableMask = wrapper.GetAvailableCellMask() & requestedMask;
        if (availableMask.IsEmpty)
        {
            cells = default;
            return false;
        }

        int cellsPerBlob = availableMask.Count;

        if (wrapper.HasFullBlobs())
        {
            cells = new byte[cellsPerBlob][];
            using ArrayPoolSpan<byte> allCells = new(Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob);
            ReadOnlySpan<byte> blob = wrapper.Blobs[blobIndex];
            KzgPolynomialCommitments.ComputeCells(blob, allCells.Slice(0, allCells.Length));
            int i = 0;
            foreach (int cellIndex in availableMask.EnumerateSetBits())
            {
                cells[i++] = allCells.Slice(cellIndex * Ckzg.BytesPerCell, Ckzg.BytesPerCell).ToArray();
            }

            return true;
        }

        if (wrapper.Cells is null)
        {
            cells = default;
            return false;
        }

        cells = new byte[cellsPerBlob][];
        int sourceCellsPerBlob = wrapper.CellMask.Count;
        int sourceOffset = blobIndex * sourceCellsPerBlob;
        int sourcePosition = 0;
        int targetPosition = 0;
        foreach (int cellIndex in wrapper.CellMask.EnumerateSetBits())
        {
            if (availableMask.Contains(cellIndex))
            {
                cells[targetPosition++] = wrapper.Cells[sourceOffset + sourcePosition];
            }

            sourcePosition++;
        }

        return true;
    }

    protected readonly record struct BlobCellsCandidate(ShardBlobNetworkWrapper Wrapper, int BlobIndex);
}
