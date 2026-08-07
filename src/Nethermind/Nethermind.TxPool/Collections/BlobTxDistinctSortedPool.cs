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
using Nethermind.Int256;
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
        => TryGetAvailableCellMetadata(hash, out availableMask, out _, out _);

    public bool TryGetAvailableCellMetadata(
        ValueHash256 hash,
        out BlobCellMask availableMask,
        out int blobCount,
        out int materializationWork)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();
        if (!base.TryGetValueNonLocked(hash, out Transaction? blobTx))
        {
            availableMask = default;
            blobCount = 0;
            materializationWork = 0;
            return false;
        }

        availableMask = blobTx switch
        {
            LightTransaction lightTx => lightTx.BlobCellMask,
            { NetworkWrapper: ShardBlobNetworkWrapper wrapper } => wrapper.GetAvailableCellMask(),
            _ => default
        };
        blobCount = blobTx.BlobVersionedHashes?.Length ?? 0;
        int materializedCellsPerBlob = blobTx switch
        {
            LightTransaction => availableMask.Count,
            { NetworkWrapper: ShardBlobNetworkWrapper wrapper } when wrapper.HasFullBlobs() => BlobCellMask.CellCount,
            _ => 0
        };
        materializationWork = checked(blobCount * materializedCellsPerBlob);
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
                candidates = new(Math.Min(txHashes.Count, requestedMask.Count + 1));
                BlobCellMask capturedMask = BlobCellMask.Empty;
                foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
                {
                    if (!TryGetValueNonLocked(hash, out Transaction? blobTx)
                        || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } currentWrapper
                        || !TryFindBlobIndex(blobTx, requestedBlobVersionedHash, out int blobIndex))
                    {
                        continue;
                    }

                    BlobCellMask candidateMask = currentWrapper.GetAvailableCellMask() & requestedMask;
                    if (candidates.Count != 0 && candidateMask.Except(capturedMask).IsEmpty)
                    {
                        continue;
                    }

                    candidates.Add(new BlobCellsCandidate(currentWrapper, blobIndex));
                    capturedMask |= candidateMask;
                    if (requestedMask.IsEmpty || capturedMask == requestedMask)
                    {
                        break;
                    }
                }
            }
        }

        return TryBuildBlobCellsAndProofsResponse(candidates, requestedMask, out availableMask, out cells, out proofs);
    }

    public bool TryMergeCells(ValueHash256 hash, BlobCellMask cellMask, byte[][] cells) =>
        MergeCells(hash, cellMask, cells) == BlobCellMergeResult.Accepted;

    public BlobCellMergeResult MergeCells(ValueHash256 hash, BlobCellMask cellMask, byte[][] cells)
    {
        if (!TryGetValueForCellMerge(hash, out Transaction? blobTx)
            || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper
            || wrapper.Version is not ProofVersion.V1)
        {
            return BlobCellMergeResult.TransactionUnavailable;
        }

        int blobCount = wrapper.Commitments.Length;
        if (cellMask.IsEmpty || cells.Length != blobCount * cellMask.Count)
        {
            return BlobCellMergeResult.InvalidCells;
        }

        if (wrapper.HasFullBlobs())
        {
            return BlobCellMergeResult.Accepted;
        }

        BlobCellMask verifiedMask = cellMask.Except(wrapper.CellMask);
        if (verifiedMask.IsEmpty)
        {
            return BlobCellMergeResult.Accepted;
        }

        byte[][] verifiedCells = verifiedMask == cellMask
            ? cells
            : BlobCellsHelper.SelectFlattenedCells(cells, cellMask, verifiedMask, blobCount);
        ShardBlobNetworkWrapper verificationWrapper = wrapper with { CellMask = verifiedMask, Cells = verifiedCells };
        if (!BlobCellsHelper.ValidateCells(verificationWrapper))
        {
            return BlobCellMergeResult.InvalidCells;
        }

        ShardBlobNetworkWrapper? recoveredWrapper = null;
        while (true)
        {
            BlobCellMask maskToAdd = verifiedMask.Except(wrapper.CellMask);
            if (maskToAdd.IsEmpty)
            {
                return BlobCellMergeResult.Accepted;
            }

            byte[][] cellsToAdd = maskToAdd == verifiedMask
                ? verifiedCells
                : BlobCellsHelper.SelectFlattenedCells(cells, cellMask, maskToAdd, blobCount);
            BlobCellMask mergedMask = wrapper.CellMask | maskToAdd;
            byte[][] mergedCells = BlobCellsHelper.MergeFlattenedCells(
                wrapper.Cells,
                wrapper.CellMask,
                cellsToAdd,
                maskToAdd,
                blobCount);
            ShardBlobNetworkWrapper mergedWrapper = wrapper with { CellMask = mergedMask, Cells = mergedCells };

            if (mergedMask.Count >= BlobCellsHelper.RequiredCellsForRecovery)
            {
                if (recoveredWrapper is null
                    && !BlobCellsHelper.TryRecoverBlobsFromVerifiedCells(mergedWrapper, out recoveredWrapper))
                {
                    return BlobCellMergeResult.InvalidCells;
                }

                mergedWrapper = recoveredWrapper;
            }

            UInt256 timestamp;
            using (McsLock.Disposable lockRelease = Lock.Acquire())
            {
                if (!TryGetValueForCellMergeNonLocked(hash, out blobTx)
                    || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper currentWrapper)
                {
                    return BlobCellMergeResult.TransactionUnavailable;
                }

                if (!ReferenceEquals(currentWrapper, wrapper))
                {
                    if (currentWrapper.HasFullBlobs())
                    {
                        return BlobCellMergeResult.Accepted;
                    }

                    if (!HasSameProofMaterial(currentWrapper, wrapper))
                    {
                        return BlobCellMergeResult.TransactionUnavailable;
                    }

                    wrapper = currentWrapper;
                    recoveredWrapper = null;
                    continue;
                }

                blobTx.NetworkWrapper = mergedWrapper;
                blobTx.ClearLengthCache();
                OnBlobTransactionUpdatedNonLocked(blobTx);
                timestamp = blobTx.Timestamp;
            }

            OnBlobTransactionUpdated(hash, timestamp);
            return BlobCellMergeResult.Accepted;
        }
    }

    private static bool HasSameProofMaterial(ShardBlobNetworkWrapper current, ShardBlobNetworkWrapper validated)
    {
        if (current.Version != validated.Version
            || current.Commitments.Length != validated.Commitments.Length
            || current.Proofs.Length != validated.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < current.Commitments.Length; i++)
        {
            if (!Bytes.AreEqual(current.Commitments[i], validated.Commitments[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < current.Proofs.Length; i++)
        {
            if (!Bytes.AreEqual(current.Proofs[i], validated.Proofs[i]))
            {
                return false;
            }
        }

        return true;
    }

    protected virtual bool TryGetValueForCellMerge(ValueHash256 hash, [NotNullWhen(true)] out Transaction? blobTx)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();
        return TryGetValueForCellMergeNonLocked(hash, out blobTx);
    }

    protected virtual bool TryGetValueForCellMergeNonLocked(ValueHash256 hash, [NotNullWhen(true)] out Transaction? blobTx)
        => TryGetValueNonLocked(hash, out blobTx);

    protected virtual void OnBlobTransactionUpdatedNonLocked(Transaction blobTx) { }

    protected virtual void OnBlobTransactionUpdated(ValueHash256 hash, in UInt256 timestamp) { }

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

        if (candidates.Count == 0)
        {
            availableMask = default;
            cells = default;
            proofs = default;
            return false;
        }

        BlobCellMask aggregateMask = BlobCellMask.Empty;
        int bestCandidateIndex = -1;
        int bestCandidateCellCount = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            BlobCellMask candidateMask = candidates[i].Wrapper.GetAvailableCellMask() & requestedMask;
            aggregateMask |= candidateMask;
            if (candidateMask.Count > bestCandidateCellCount)
            {
                bestCandidateCellCount = candidateMask.Count;
                bestCandidateIndex = i;
            }
        }

        if (aggregateMask.IsEmpty)
        {
            availableMask = BlobCellMask.Empty;
            cells = [];
            proofs = [];
            return true;
        }

        cells = new byte[aggregateMask.Count][];
        proofs = new byte[aggregateMask.Count][];
        BlobCellMask remainingMask = aggregateMask;

        for (int pass = -1; pass < candidates.Count; pass++)
        {
            int candidateIndex = pass < 0 ? bestCandidateIndex : pass;
            if (candidateIndex < 0 || (pass >= 0 && candidateIndex == bestCandidateIndex))
            {
                continue;
            }

            BlobCellsCandidate candidate = candidates[candidateIndex];
            BlobCellMask selectedMask = candidate.Wrapper.GetAvailableCellMask() & remainingMask;
            if (selectedMask.IsEmpty)
            {
                continue;
            }

            if (!TryGetFlattenedCellsForBlob(candidate.Wrapper, candidate.BlobIndex, selectedMask, out byte[][]? selectedCells))
            {
                availableMask = default;
                cells = default;
                proofs = default;
                return false;
            }

            byte[][] selectedProofs = BlobCellsHelper.SelectProofs(candidate.Wrapper, candidate.BlobIndex, selectedMask);
            int selectedPosition = 0;
            int targetPosition = 0;
            foreach (int cellIndex in aggregateMask.EnumerateSetBits())
            {
                if (selectedMask.Contains(cellIndex))
                {
                    cells[targetPosition] = selectedCells[selectedPosition];
                    proofs[targetPosition] = selectedProofs[selectedPosition++];
                }

                targetPosition++;
            }

            remainingMask = remainingMask.Except(selectedMask);
            if (remainingMask.IsEmpty)
            {
                availableMask = aggregateMask;
                return true;
            }
        }

        availableMask = default;
        cells = default;
        proofs = default;
        return false;
    }

    protected static bool TryGetFlattenedCellsForBlob(
        ShardBlobNetworkWrapper wrapper,
        int blobIndex,
        BlobCellMask requestedMask,
        [NotNullWhen(true)] out byte[][]? cells)
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
