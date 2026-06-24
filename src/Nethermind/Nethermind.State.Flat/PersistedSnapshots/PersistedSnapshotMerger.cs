// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// N-way merge of persisted snapshots into a single <see cref="SortedTable"/>. Each input is a
/// single sorted run; the merge walks them in ascending key order, resolving collisions newest-wins
/// (newest = highest source index, inputs are oldest-first). All inputs are blob-backed
/// (<see cref="NodeRef"/> values), so trie-node values are copied verbatim and the merged snapshot
/// references the union of the inputs' blob arenas via the metadata <c>ref_ids</c> entry.
/// </summary>
/// <remarks>
/// Generic over the byte-reader source so it isn't bound to a specific reader; each input is an
/// <see cref="IByteReaderSource{TReader,TPin}"/> that mints a fresh reader on demand (production
/// drives it with <see cref="Storage.WholeReadSession"/>). The deliberately-unoptimized find-min is
/// O(N) per step.
/// </remarks>
public static class PersistedSnapshotMerger
{
    // A per-address slot deferred during the merge until that address's self-destruct barrier is
    // known. Offsets index into the run-scoped pending key/value buffers.
    private struct PendingSlot
    {
        public int KeyOffset;
        public int KeyLength;
        public int ValueOffset;
        public int ValueLength;
        public int WinningSource;
    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into <paramref name="writer"/>. Callers
    /// own the source lifecycle: open one reader source per input up front, pass them here, dispose
    /// after the merge returns.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref TWriter writer, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(bloom);

        // The table is built by streaming in strictly ascending key order: entries (ref-ids 0x00 …
        // per-address 0xFE) first via the N-way merge, then metadata (0xFF) last.
        SortedTableBuilder<TWriter> table = new(ref writer);
        try
        {
            MergeEntries<TWriter, TView, TReader, TPin>(views, ref table, bloom);
            MergeMetadata<TWriter, TView, TReader, TPin>(views, ref table);
            table.Build();
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>
    /// Streaming N-way merge of every non-metadata entry. Per key: newest source wins, except slots,
    /// which are buffered per address and flushed once that address's self-destruct barrier is known
    /// (slots sort before self-destruct, which sorts before account, under the reverse-tag order).
    /// </summary>
    private static void MergeEntries<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref SortedTableBuilder<TWriter> table, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        SortedTableEnumerator<TReader, TPin>[] enums = new SortedTableEnumerator<TReader, TPin>[n];
        bool[] hasMore = new bool[n];
        for (int i = 0; i < n; i++)
        {
            TReader r = views[i].CreateReader();
            enums[i] = new SortedTableEnumerator<TReader, TPin>(in r, new Bound(0, r.Length));
            hasMore[i] = enums[i].MoveNext(in r);
        }

        using NativeMemoryList<byte> pendingKeys = new(256);
        using NativeMemoryList<byte> pendingValues = new(256);
        using NativeMemoryList<PendingSlot> pending = new(16);
        Span<byte> curAddr = stackalloc byte[PersistedSnapshotKey.AddressKeyLength];
        bool haveAddr = false;
        int barrier = -1;

        Span<byte> minKey = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        // n is the number of merged inputs (small in practice); cap the stackalloc and fall back to
        // the heap for an unusually large compaction batch to avoid a stack overflow.
        Span<int> matching = n <= 64 ? stackalloc int[64] : new int[n];

        while (true)
        {
            int minIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (!hasMore[i]) continue;
                if (minIdx < 0 || enums[i].CurrentKey.SequenceCompareTo(enums[minIdx].CurrentKey) < 0)
                    minIdx = i;
            }
            if (minIdx < 0) break;

            ReadOnlySpan<byte> minKeySrc = enums[minIdx].CurrentKey;
            int keyLen = minKeySrc.Length;
            minKeySrc.CopyTo(minKey);
            ReadOnlySpan<byte> key = minKey[..keyLen];

            // Metadata (column 0xFF) sorts last and is produced separately by MergeMetadata.
            if (key[0] == PersistedSnapshotKey.MetadataColumn)
            {
                if (haveAddr) FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);
                break;
            }

            bool isPerAddr = key[0] == PersistedSnapshotKey.AccountColumn;
            // Safety net for a slots-only address (no self-destruct / account record to trigger the
            // flush): on address change or leaving the per-address column, flush any still-buffered
            // slots (barrier resolved from this address's self-destruct, or -1 if none).
            if (haveAddr && (!isPerAddr || !PersistedSnapshotKey.PerAddressAddress(key).SequenceEqual(curAddr)))
            {
                FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);
                haveAddr = false;
            }
            if (isPerAddr && !haveAddr)
            {
                PersistedSnapshotKey.PerAddressAddress(key).CopyTo(curAddr);
                haveAddr = true;
                barrier = -1;
            }

            int matchCount = 0;
            for (int i = 0; i < n; i++)
                if (hasMore[i] && enums[i].CurrentKey.SequenceEqual(key))
                    matching[matchCount++] = i;
            int newest = matching[matchCount - 1];

            if (isPerAddr)
            {
                byte sub = PersistedSnapshotKey.PerAddressSubColumn(key);
                if (sub == PersistedSnapshotKey.SlotSub)
                {
                    BufferSlot<TView, TReader, TPin>(views, enums, key, newest, pendingKeys, pendingValues, pending);
                }
                else if (sub == PersistedSnapshotKey.SelfDestructSub)
                {
                    // Slots (0xFD) sort before self-destruct (0xFE): resolve the barrier from the
                    // self-destruct record, flush the now barrier-filtered slots so they land in their
                    // ascending position, then emit the self-destruct record.
                    barrier = ComputeSelfDestructBarrier<TView, TReader, TPin>(views, enums, matching[..matchCount]);
                    FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);
                    EmitSelfDestruct(ref table, bloom, key, barrier);
                }
                else // account
                {
                    // Account (0xFF) sorts after slots and self-destruct; flush any slots not already
                    // flushed by a self-destruct (barrier == -1 ⇒ no truncation) before it.
                    FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);
                    EmitNewest<TWriter, TView, TReader, TPin>(views, enums, ref table, bloom, key, newest);
                }
            }
            else // state / storage trie node
            {
                EmitNewest<TWriter, TView, TReader, TPin>(views, enums, ref table, bloom, key, newest);
            }

            for (int k = 0; k < matchCount; k++)
            {
                int i = matching[k];
                TReader r = views[i].CreateReader();
                hasMore[i] = enums[i].MoveNext(in r);
            }
        }

        if (haveAddr) FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);

        for (int i = 0; i < n; i++) enums[i].Dispose();
    }

    private static void BufferSlot<TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ReadOnlySpan<byte> key, int newest,
        NativeMemoryList<byte> pendingKeys, NativeMemoryList<byte> pendingValues, NativeMemoryList<PendingSlot> pending)
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        TReader r = views[newest].CreateReader();
        using TPin pin = r.PinBuffer(enums[newest].CurrentValue);
        PendingSlot slot = new()
        {
            KeyOffset = pendingKeys.Count,
            KeyLength = key.Length,
            ValueOffset = pendingValues.Count,
            ValueLength = pin.Buffer.Length,
            WinningSource = newest,
        };
        pendingKeys.AddRange(key);
        pendingValues.AddRange(pin.Buffer);
        pending.Add(slot);
    }

    /// <summary>Flush this address's buffered slots, dropping any whose newest contributing source is
    /// older than the self-destruct <paramref name="barrier"/>, then clear the pending buffers.</summary>
    private static void FlushPendingSlots<TWriter>(
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> addr, int barrier,
        NativeMemoryList<byte> pendingKeys, NativeMemoryList<byte> pendingValues, NativeMemoryList<PendingSlot> pending)
        where TWriter : IByteBufferWriter
    {
        ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(addr);
        Span<byte> keys = pendingKeys.AsSpan();
        Span<byte> values = pendingValues.AsSpan();
        for (int i = 0; i < pending.Count; i++)
        {
            PendingSlot s = pending[i];
            if (barrier >= 0 && s.WinningSource < barrier) continue; // truncated by self-destruct
            ReadOnlySpan<byte> key = keys.Slice(s.KeyOffset, s.KeyLength);
            table.Add(key, values.Slice(s.ValueOffset, s.ValueLength));
            bloom.Add(addrBloomKey);
            bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, PersistedSnapshotKey.SlotKeyBytes(key)));
        }
        pendingKeys.Clear();
        pendingValues.Clear();
        pending.Clear();
    }

    /// <summary>The truncation barrier for a self-destruct key — the newest source index that
    /// destructed, or -1 if none in the merged range did.</summary>
    private static int ComputeSelfDestructBarrier<TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums, scoped ReadOnlySpan<int> matching)
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int barrier = -1;
        for (int k = 0; k < matching.Length; k++)
        {
            int i = matching[k];
            byte flag = 0;
            TReader r = views[i].CreateReader();
            // Skip unreadable entries — do not let a failed read fall through as flag == 0, which is
            // the destructed marker and would set a spurious truncation barrier.
            if (!r.TryRead(enums[i].CurrentValue.Offset, new Span<byte>(ref flag))) continue;
            if (flag == PersistedSnapshotTags.SelfDestructDestructedMarkerByte) barrier = i; // newest destructed
        }
        return barrier;
    }

    /// <summary>Emit the self-destruct record — destructed if any source in the merged range destructed
    /// (<paramref name="barrier"/> &gt;= 0), else new.</summary>
    /// <remarks>
    /// The emitted tag is "destructed" whenever any source in the merged range destructed, even if a
    /// newer source re-created the contract. This is deliberate and matches the only consumer of the
    /// flag value, <see cref="PersistenceManager"/>: when a CompactSized snapshot is written to
    /// RocksDB it does <c>if (SelfDestructFlag is false) batch.SelfDestruct(addr)</c> and only then
    /// re-applies the account and the (already barrier-filtered) post-destruct slots. The
    /// <c>SelfDestruct</c> clears any storage carried in RocksDB from before this range, so a
    /// re-created contract ends with exactly its new slots. Emitting "new" here would skip that clear
    /// and leak the pre-destruct storage. The flag value is otherwise unused on the read path, which
    /// keys off the barrier (presence) via <see cref="PersistedSnapshotStack.TryGetSelfDestruct"/>.
    /// </remarks>
    private static void EmitSelfDestruct<TWriter>(
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> key, int barrier)
        where TWriter : IByteBufferWriter
    {
        table.Add(key, barrier >= 0
            ? PersistedSnapshotTags.SelfDestructDestructedMarker
            : PersistedSnapshotTags.SelfDestructNewMarker);
        bloom.Add(PersistedSnapshotBloomBuilder.AddressKey(PersistedSnapshotKey.PerAddressAddress(key)));
    }

    /// <summary>Emit the newest source's value for <paramref name="key"/> (account / state node /
    /// storage node) and add the matching bloom key.</summary>
    private static void EmitNewest<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> key, int newest)
        where TWriter : IByteBufferWriter
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        TReader r = views[newest].CreateReader();
        using TPin pin = r.PinBuffer(enums[newest].CurrentValue);
        table.Add(key, pin.Buffer);
        AddBloomForKey(bloom, key);
    }

    private static void AddBloomForKey(BloomFilter bloom, ReadOnlySpan<byte> key)
    {
        switch (key[0])
        {
            case PersistedSnapshotKey.RefIdColumn:
                break; // ref-id presence records are not bloom-gated
            case PersistedSnapshotKey.AccountColumn:
                bloom.Add(PersistedSnapshotBloomBuilder.AddressKey(PersistedSnapshotKey.PerAddressAddress(key)));
                break;
            case PersistedSnapshotKey.StorageColumn:
                ulong addrHashKey = MemoryMarshal.Read<ulong>(PersistedSnapshotKey.StorageAddressHash(key));
                bloom.Add(addrHashKey ^ PersistedSnapshotBloomBuilder.StatePathKey(PersistedSnapshotKey.StoragePathBytes(key)));
                break;
            default: // state-trie node columns
                bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(PersistedSnapshotKey.StatePathBytes(key)));
                break;
        }
    }

    /// <summary>
    /// Merge metadata: from_block / from_hash from the oldest source, to_block / to_hash / version
    /// from the newest, the union of every source's ref_ids, and a noderefs presence marker.
    /// </summary>
    private static void MergeMetadata<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref SortedTableBuilder<TWriter> table)
        where TWriter : IByteBufferWriter
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        TReader oldest = views[0].CreateReader();
        Bound oldestTable = new(0, oldest.Length);
        TReader newest = views[n - 1].CreateReader();
        Bound newestTable = new(0, newest.Length);

        // Metadata keys (column 0xFF) are emitted in ascending name order so the streaming builder's
        // strict-ascending invariant holds: from_block < from_hash < noderefs < to_block < to_hash < version.
        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromHashKey);

        Span<byte> noderefsKey = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int noderefsLen = PersistedSnapshotKey.WriteMetadataKey(noderefsKey, PersistedSnapshotTags.MetadataNodeRefsKey);
        table.Add(noderefsKey[..noderefsLen], PersistedSnapshotTags.MetadataNodeRefsPresentMarker);

        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToHashKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataVersionKey);

        // ref-id records (column 0x00) are not metadata — they flow through the normal entry merge
        // (MergeEntries), which dedups them across sources into the union for free.
    }

    private static void AddMetadataField<TWriter, TReader, TPin>(
        ref SortedTableBuilder<TWriter> table, scoped in TReader reader, Bound metaTable, ReadOnlySpan<byte> name)
        where TWriter : IByteBufferWriter
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        Span<byte> key = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int len = PersistedSnapshotKey.WriteMetadataKey(key, name);
        if (SortedTableReader.TrySeek<TReader, TPin>(in reader, metaTable, key[..len], out Bound vb))
        {
            using TPin pin = reader.PinBuffer(vb);
            table.Add(key[..len], pin.Buffer);
        }
    }

}
