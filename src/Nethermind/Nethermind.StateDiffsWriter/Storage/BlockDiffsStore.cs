// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiffsWriter.Data;
// ISortedKeyValueStore is declared in Nethermind.Core; using the namespace
// already brings it into scope. The reference is kept inline near the cast
// site (PruneOlderThan) so the call-graph reads top-down.

namespace Nethermind.StateDiffsWriter.Storage;

/// <summary>
/// Thin wrapper over the two <see cref="BlockDiffsColumns"/> column families.
/// All cross-CF mutations go through <see cref="WriteBlockDiff"/> so a single
/// per-block <see cref="BlockDiffRecord"/> persist plus the per-address
/// slot-count updates land as one atomic RocksDB write batch — RocksDB
/// guarantees batch atomicity within a single DB even across column families.
/// </summary>
public sealed class BlockDiffsStore(IColumnsDb<BlockDiffsColumns> db)
{
    public const int BlockKeyLength = 8;
    public const int AddressKeyLength = 32;
    public const int SlotCountValueLength = 8;

    private readonly IColumnsDb<BlockDiffsColumns> _db = db;
    private readonly IDb _blockDiffs = db.GetColumnDb(BlockDiffsColumns.Default);
    private readonly IDb _slotCounts = db.GetColumnDb(BlockDiffsColumns.SlotCounts);

    /// <summary>
    /// Atomically persist the per-block record and the post-block slot-count
    /// totals for every address that changed in this block. The write batch
    /// covers both column families so a crash mid-call leaves the BlockDiffs
    /// CF and the SlotCounts CF either both updated or both untouched.
    /// </summary>
    /// <returns>Number of payload bytes written to the Default CF.</returns>
    public int WriteBlockDiff(BlockDiffRecord record)
    {
        Span<byte> blockKey = stackalloc byte[BlockKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(blockKey, (ulong)record.BlockNumber);

        // Encode into a caller-allocated buffer that we hand straight to RocksDB —
        // avoids the previous double-allocation (one for the RlpStream, one for
        // stream.Data.AsSpan().ToArray()). RlpStream(byte[]) wraps the provided
        // array without copying, so the same buffer travels through the batch.
        int length = BlockDiffRecordDecoder.Instance.GetLength(record);
        byte[] payload = new byte[length];
        RlpStream stream = new(payload);
        BlockDiffRecordDecoder.Instance.Encode(stream, record);

        using IColumnsWriteBatch<BlockDiffsColumns> batch = _db.StartWriteBatch();
        IWriteBatch defaultBatch = batch.GetColumnBatch(BlockDiffsColumns.Default);
        defaultBatch.Set(blockKey, payload);

        if (record.SlotCountChanges.Count > 0)
        {
            IWriteBatch slotBatch = batch.GetColumnBatch(BlockDiffsColumns.SlotCounts);
            Span<byte> slotKey = stackalloc byte[AddressKeyLength];
            Span<byte> slotValue = stackalloc byte[SlotCountValueLength];

            foreach (SlotCountEntry entry in record.SlotCountChanges)
            {
                entry.AddressHash.Bytes.CopyTo(slotKey);
                if (entry.NewCount == 0)
                {
                    // Drop the row instead of writing eight zero bytes: RocksDB tombstones
                    // are cheap, and a missing key reads as zero in GetSlotCount below.
                    // Keeps SlotCounts size proportional to live contracts only.
                    slotBatch.Remove(slotKey);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64BigEndian(slotValue, entry.NewCount);
                    // PutSpan accepts a value span directly, so the 8-byte slot total
                    // never travels through a per-row managed allocation.
                    slotBatch.PutSpan(slotKey, slotValue);
                }
            }
        }

        return length;
    }

    public BlockDiffRecord? ReadBlockDiff(long blockNumber)
    {
        Span<byte> key = stackalloc byte[BlockKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(key, (ulong)blockNumber);
        byte[]? bytes = _blockDiffs.Get(key);
        if (bytes is null || bytes.Length == 0) return null;
        Rlp.ValueDecoderContext ctx = new(bytes);
        return BlockDiffRecordDecoder.Instance.Decode(ref ctx);
    }

    /// <summary>
    /// Read the running slot count for an address. Returns 0 when the address
    /// has never had storage (or its count dropped to zero and was tombstoned).
    /// </summary>
    public ulong GetSlotCount(in ValueHash256 addressHash)
    {
        Span<byte> key = stackalloc byte[AddressKeyLength];
        addressHash.Bytes.CopyTo(key);
        byte[]? bytes = _slotCounts.Get(key);
        if (bytes is null || bytes.Length != SlotCountValueLength) return 0;
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>
    /// Direct slot-count override used by tests; production code should not call
    /// this — the running map is maintained exclusively by <see cref="WriteBlockDiff"/>.
    /// </summary>
    internal void SetSlotCountForTesting(in ValueHash256 addressHash, ulong count)
    {
        Span<byte> key = stackalloc byte[AddressKeyLength];
        addressHash.Bytes.CopyTo(key);
        if (count == 0)
        {
            _slotCounts.Remove(key);
            return;
        }
        Span<byte> value = stackalloc byte[SlotCountValueLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, count);
        _slotCounts.Set(key, value.ToArray());
    }

    /// <summary>
    /// Best-effort delete of every <see cref="BlockDiffsColumns.Default"/> entry
    /// whose key is strictly less than the supplied <paramref name="cutoffBlock"/>.
    /// Used by <see cref="Service.DiffsPruner"/>; the slot-count CF is never pruned.
    /// <para>
    /// Backed by <see cref="ISortedKeyValueStore.GetViewBetween"/> when the
    /// underlying column DB supports it (RocksDB and the snapshotable mem DB do)
    /// so the scan terminates at the cutoff key instead of paging through every
    /// surviving row. The legacy full-table scan path is kept as a fallback for
    /// vanilla <see cref="MemDb"/>-backed tests, which do not implement the
    /// sorted-view interface.
    /// </para>
    /// </summary>
    public int PruneOlderThan(long cutoffBlock)
    {
        if (cutoffBlock <= 0) return 0;

        Span<byte> cutoffKey = stackalloc byte[BlockKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(cutoffKey, (ulong)cutoffBlock);

        return _blockDiffs is ISortedKeyValueStore sortedStore
            ? PruneOlderThanSeek(sortedStore, cutoffKey)
            : PruneOlderThanFullScan(cutoffKey);
    }

    private int PruneOlderThanSeek(ISortedKeyValueStore sortedStore, ReadOnlySpan<byte> cutoffKey)
    {
        // GetViewBetween treats lastKey as exclusive, which matches the "strictly
        // less than cutoffBlock" contract above. lowerBound is empty so the
        // iterator seeks from the first surviving key, and the view stops at the
        // cutoff — no need to inspect a single row past the deletion window.
        using ISortedView view = sortedStore.GetViewBetween([], cutoffKey);

        int removed = 0;
        using IWriteBatch batch = _blockDiffs.StartWriteBatch();
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != BlockKeyLength) continue;
            batch.Remove(key);
            removed++;
        }
        return removed;
    }

    private int PruneOlderThanFullScan(ReadOnlySpan<byte> cutoffKey)
    {
        byte[] cutoffArray = cutoffKey.ToArray();
        int removed = 0;
        List<byte[]> toDelete = [];
        foreach (byte[] key in _blockDiffs.GetAllKeys(ordered: true))
        {
            if (key.Length != BlockKeyLength) continue;
            if (CompareBigEndian(key, cutoffArray) >= 0) break;
            toDelete.Add(key);
        }

        if (toDelete.Count == 0) return 0;

        using IWriteBatch batch = _blockDiffs.StartWriteBatch();
        foreach (byte[] key in toDelete)
        {
            batch.Remove(key);
            removed++;
        }
        return removed;
    }

    private static int CompareBigEndian(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }
}
