// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.TxPool;

/// <summary>Where an included blob's transaction is saved among the processed blob transactions.</summary>
/// <param name="BlockNumber">The number of the block the transaction was saved for.</param>
/// <param name="Position">The stored position among the blob transactions saved for that block, not the transaction's index in the block.</param>
/// <param name="BlobIndex">The index of the blob within the transaction.</param>
internal readonly record struct IncludedBlobLocation(ulong BlockNumber, int Position, int BlobIndex);

/// <summary>
/// Indexes the blobs of recently included blob transactions by versioned hash, pointing at where each transaction is
/// saved among the processed blob transactions, so their blobs can be served without being held in memory.
/// </summary>
/// <remarks>
/// <para>
/// The index holds one batch of locations per block height. Recording a block first drops the batches at its height
/// and above, as a reorg rewrites the saved rows from that height, then the batches more than the max block distance
/// below it, except that a block re-recorded with the same hash and nothing saved keeps its own batch.
/// <see cref="PruneFinalized"/> drops the batches at and below the finalized block. The distance cap alone
/// bounds the index at <c>(maxBlockDistance + 1)</c> batches, each no larger than the blob transactions of one block,
/// so it stays bounded when finality stalls.
/// </para>
/// <para>
/// A versioned hash maps to one location, the last one recorded. Dropping a batch removes a hash only while it still
/// points into that batch, so a hash also recorded by a later block keeps that block's location. A hash whose
/// latest block is dropped is not re-pointed at an earlier block that also holds it; that lookup misses.
/// </para>
/// <para>
/// Thread safety: <see cref="TryGetLocation"/> is lock-free and may run concurrently with any other member.
/// <see cref="Record"/> and <see cref="PruneFinalized"/> serialize on a private lock, which readers never take and
/// which is never held across I/O. A reader may see a location that is being dropped, or miss one that is being
/// recorded, but every location it sees was recorded for that hash; callers must verify the transaction they read
/// back, because the saved rows can change independently of this index.
/// </para>
/// </remarks>
/// <param name="maxBlockDistance">
/// How far below a recorded block a batch is kept. It is fixed at construction, so the bound holds for the
/// lifetime of the index.
/// </param>
internal sealed class IncludedBlobLocations(ulong maxBlockDistance)
{
    private readonly ConcurrentDictionary<ValueHash256, IncludedBlobLocation> _locations = new();
    // Ascending by height: a recorded block first drops every batch at or above its height.
    private readonly List<Batch> _batches = [];
    private readonly Lock _writeLock = new();

    /// <summary>The number of indexed versioned hashes.</summary>
    public int Count => _locations.Count;

    /// <summary>
    /// Indexes the blobs of the blob transactions saved for block <paramref name="blockNumber"/>, replacing whatever
    /// was indexed at that height or above.
    /// </summary>
    /// <param name="blockNumber">The number of the block the transactions were saved for.</param>
    /// <param name="blockHash">The hash of that block; <see langword="null"/> never matches a recorded block.</param>
    /// <param name="savedBlobTxs">The saved transactions, in stored position order; may be empty.</param>
    /// <remarks>
    /// Must be called for every processed block, also when nothing was saved for it, so a reorged-out block's
    /// locations are dropped. Hashes that are missing or not 32 bytes long are skipped. When the block at that height
    /// is the same block as the one already indexed and nothing was saved for it this time, its batch is kept and only
    /// the heights above are dropped: the same block is processed again when the head is rewound to it, by which point
    /// its transactions have left the pool, yet its saved rows are intact. A non-empty save replaces the batch, as it
    /// rewrote the rows and their positions.
    /// </remarks>
    public void Record(ulong blockNumber, Hash256? blockHash, ReadOnlySpan<Transaction> savedBlobTxs)
    {
        lock (_writeLock)
        {
            Batch? batch = CreateBatch(blockNumber, blockHash, savedBlobTxs);

            int firstSuperseded = _batches.Count;
            while (firstSuperseded > 0 && _batches[firstSuperseded - 1].BlockNumber >= blockNumber)
            {
                firstSuperseded--;
            }

            if (batch is null
                && blockHash is not null
                && firstSuperseded < _batches.Count
                && _batches[firstSuperseded].BlockNumber == blockNumber
                && _batches[firstSuperseded].BlockHash == blockHash)
            {
                firstSuperseded++;
            }

            RemoveBatches(firstSuperseded, _batches.Count - firstSuperseded);
            RemoveBatchesBelow(blockNumber.SaturatingSub(maxBlockDistance));

            if (batch is null)
            {
                return;
            }

            foreach ((ValueHash256 hash, IncludedBlobLocation location) in batch.Entries)
            {
                _locations[hash] = location;
            }

            _batches.Add(batch);
        }
    }

    /// <summary>Drops the locations of blocks at and below <paramref name="finalizedBlockNumber"/>.</summary>
    /// <param name="finalizedBlockNumber">The number of the finalized block; may be above the last recorded block.</param>
    public void PruneFinalized(ulong finalizedBlockNumber)
    {
        lock (_writeLock)
        {
            RemoveBatchesBelow(finalizedBlockNumber == ulong.MaxValue ? ulong.MaxValue : finalizedBlockNumber + 1);
        }
    }

    /// <summary>Looks up where the transaction carrying the blob with versioned hash <paramref name="blobVersionedHash"/> is saved.</summary>
    /// <param name="blobVersionedHash">The versioned hash; one that is not 32 bytes long misses.</param>
    /// <param name="location">The recorded location, or <see langword="default"/> on a miss.</param>
    /// <returns><see langword="true"/> if the hash is indexed.</returns>
    public bool TryGetLocation(ReadOnlySpan<byte> blobVersionedHash, out IncludedBlobLocation location)
    {
        if (blobVersionedHash.Length != ValueHash256.MemorySize)
        {
            location = default;
            return false;
        }

        return _locations.TryGetValue(new ValueHash256(blobVersionedHash), out location);
    }

    private static Batch? CreateBatch(ulong blockNumber, Hash256? blockHash, ReadOnlySpan<Transaction> savedBlobTxs)
    {
        List<(ValueHash256, IncludedBlobLocation)>? entries = null;
        for (int position = 0; position < savedBlobTxs.Length; position++)
        {
            if (savedBlobTxs[position].BlobVersionedHashes is not { } hashes)
            {
                continue;
            }

            for (int blobIndex = 0; blobIndex < hashes.Length; blobIndex++)
            {
                if (hashes[blobIndex] is { Length: ValueHash256.MemorySize } hash)
                {
                    entries ??= [];
                    entries.Add((new ValueHash256(hash), new IncludedBlobLocation(blockNumber, position, blobIndex)));
                }
            }
        }

        return entries is null ? null : new Batch(blockNumber, blockHash, entries.ToArray());
    }

    private void RemoveBatchesBelow(ulong lowestRetainedBlockNumber)
    {
        int count = 0;
        while (count < _batches.Count && _batches[count].BlockNumber < lowestRetainedBlockNumber)
        {
            count++;
        }

        RemoveBatches(0, count);
    }

    private void RemoveBatches(int index, int count)
    {
        for (int i = index; i < index + count; i++)
        {
            foreach ((ValueHash256 hash, IncludedBlobLocation location) in _batches[i].Entries)
            {
                // Compare-and-remove: a later block may have recorded the same hash since.
                _locations.TryRemove(new KeyValuePair<ValueHash256, IncludedBlobLocation>(hash, location));
            }
        }

        _batches.RemoveRange(index, count);
    }

    private sealed record Batch(ulong BlockNumber, Hash256? BlockHash, (ValueHash256 Hash, IncludedBlobLocation Location)[] Entries);
}
