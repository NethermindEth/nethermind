// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.TxPool;

/// <summary>
/// Keeps the blobs and proofs of blob transactions included in the most recent blocks, keyed by versioned hash,
/// so the Engine API can serve them after the transactions have left the blob pool.
/// </summary>
/// <remarks>
/// Entries are matched by versioned hash, which commits to the blob, so an entry stays correct if its block is
/// reorganized out and simply ages out. Readers never block and see a consistent snapshot of the batch array.
/// <see cref="StartBlock"/> and <see cref="Add"/> are not thread-safe and must be called by a single writer at a time;
/// the pool calls them only from its head-processing loop, under its new-head lock.
/// Memory is bounded by <see cref="RetainedBlocks"/> and <see cref="MaxRetainedBatches"/>. The blob and proof arrays
/// are shared with the transaction they came from, not copied.
/// </remarks>
internal sealed class RecentlyIncludedBlobs
{
    internal const ulong RetainedBlocks = 4;

    // A reorg can add several batches at one height; this keeps those from growing the map.
    internal const int MaxRetainedBatches = 2 * (int)RetainedBlocks;

    private volatile Batch[] _batches = [];
    private ulong _openBlockNumber;
    private Batch? _openBatch;

    /// <summary>
    /// Opens the batch for block <paramref name="blockNumber"/> and drops what has fallen out of the retention window.
    /// </summary>
    /// <remarks>Blobs added until the next call belong to this block.</remarks>
    public void StartBlock(ulong blockNumber)
    {
        _openBlockNumber = blockNumber;
        _openBatch = null;

        Batch[] current = _batches;
        int retainedCount = 0;
        foreach (Batch batch in current)
        {
            if (IsRetained(batch, blockNumber))
            {
                retainedCount++;
            }
        }

        if (retainedCount == current.Length)
        {
            return;
        }

        Batch[] retained = new Batch[retainedCount];
        int next = 0;
        foreach (Batch batch in current)
        {
            if (IsRetained(batch, blockNumber))
            {
                retained[next++] = batch;
            }
        }

        _batches = retained;
    }

    /// <summary>
    /// Records the blobs of <paramref name="tx"/> as included in the block opened by <see cref="StartBlock"/>.
    /// </summary>
    /// <remarks>
    /// Meant to be called before the transaction leaves the blob pool, so a blob is always served by one of the two.
    /// A transaction without all of its blobs is skipped.
    /// </remarks>
    public void Add(Transaction tx)
    {
        if (tx.BlobVersionedHashes is not { Length: > 0 } hashes
            || tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper
            || !wrapper.HasFullBlobs()
            || wrapper.Blobs.Length != hashes.Length)
        {
            return;
        }

        int proofsPerBlob = wrapper.Version == ProofVersion.V1 ? Ckzg.CellsPerExtBlob : 1;
        if (wrapper.Proofs.Length != proofsPerBlob * hashes.Length)
        {
            return;
        }

        if (_openBatch is { } openBatch)
        {
            Fill(openBatch, hashes, wrapper, proofsPerBlob);
            return;
        }

        // The batch is filled before it is published, so a reader never sees an empty one.
        Batch batch = new(_openBlockNumber);
        Fill(batch, hashes, wrapper, proofsPerBlob);
        _batches = Append(_batches, batch);
        _openBatch = batch;
    }

    private static bool IsRetained(Batch batch, ulong headBlockNumber) => batch.BlockNumber + RetainedBlocks > headBlockNumber;

    private static void Fill(Batch batch, byte[]?[] hashes, ShardBlobNetworkWrapper wrapper, int proofsPerBlob)
    {
        for (int i = 0; i < hashes.Length; i++)
        {
            if (hashes[i] is { } hash)
            {
                batch.Blobs[hash] = new IncludedBlob(wrapper.Blobs[i], new ReadOnlyMemory<byte[]>(wrapper.Proofs, proofsPerBlob * i, proofsPerBlob), wrapper.Version);
            }
        }
    }

    private static Batch[] Append(Batch[] current, Batch batch)
    {
        int dropped = Math.Max(0, current.Length + 1 - MaxRetainedBatches);
        Batch[] appended = new Batch[current.Length + 1 - dropped];
        Array.Copy(current, dropped, appended, 0, current.Length - dropped);
        appended[^1] = batch;
        return appended;
    }

    public bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[]? proof)
    {
        if (TryGet(blobVersionedHash, ProofVersion.V0, out blob, out ReadOnlyMemory<byte[]> proofs))
        {
            proof = proofs.Span[0];
            return true;
        }

        proof = null;
        return false;
    }

    public bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[][]? cellProofs)
    {
        if (TryGet(blobVersionedHash, ProofVersion.V1, out blob, out ReadOnlyMemory<byte[]> proofs))
        {
            cellProofs = proofs.ToArray();
            return true;
        }

        cellProofs = null;
        return false;
    }

    /// <summary>
    /// Fills the entries of <paramref name="blobs"/> and <paramref name="proofs"/> that are still empty and whose
    /// versioned hash is held with cell proofs.
    /// </summary>
    /// <returns>The number of entries filled.</returns>
    public int TryFillBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
        Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs)
    {
        Batch[] batches = _batches;
        if (batches.Length == 0)
        {
            return 0;
        }

        int filled = 0;
        for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
        {
            if (blobs[i] is null
                && TryGet(batches, requestedBlobVersionedHashes[i], ProofVersion.V1, out byte[]? blob, out ReadOnlyMemory<byte[]> blobProofs))
            {
                blobs[i] = blob;
                proofs[i] = blobProofs;
                filled++;
            }
        }

        return filled;
    }

    private bool TryGet(byte[] blobVersionedHash, ProofVersion version,
        [NotNullWhen(true)] out byte[]? blob, out ReadOnlyMemory<byte[]> proofs)
        => TryGet(_batches, blobVersionedHash, version, out blob, out proofs);

    private static bool TryGet(Batch[] batches, byte[] blobVersionedHash, ProofVersion version,
        [NotNullWhen(true)] out byte[]? blob, out ReadOnlyMemory<byte[]> proofs)
    {
        for (int i = batches.Length - 1; i >= 0; i--)
        {
            if (batches[i].Blobs.TryGetValue(blobVersionedHash, out IncludedBlob included) && included.Version == version)
            {
                blob = included.Blob;
                proofs = included.Proofs;
                return true;
            }
        }

        blob = null;
        proofs = default;
        return false;
    }

    private readonly record struct IncludedBlob(byte[] Blob, ReadOnlyMemory<byte[]> Proofs, ProofVersion Version);

    private sealed class Batch(ulong blockNumber)
    {
        public ulong BlockNumber { get; } = blockNumber;

        public ConcurrentDictionary<byte[], IncludedBlob> Blobs { get; } = new(Bytes.EqualityComparer);
    }
}
