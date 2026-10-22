// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
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
    /// Streaming N-way merge of every non-metadata entry. Per key the newest source wins. Within a
    /// per-address group the order is account, then self-destruct, then slots (under the reverse-tag
    /// order), so the self-destruct resolves the truncation barrier before the slots it filters — each
    /// slot is then emitted or dropped on the fly, with no per-address buffering.
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
        // Tracks how many enumerators were actually constructed so the finally disposes exactly those,
        // even if the init loop or the merge loop throws partway through.
        int initialized = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                TReader r = views[i].CreateReader();
                enums[i] = new SortedTableEnumerator<TReader, TPin>(in r, new Bound(0, r.Length));
                initialized = i + 1;
                hasMore[i] = enums[i].MoveNext(in r);
            }

            // Cached for the current per-address group: its address (for change detection + bloom keys) and
            // the self-destruct truncation barrier, resolved when the group's self-destruct record is seen
            // (it sorts before the slots it filters) and -1 when the group has none.
            Address? curAddr = null;
            ulong addrBloomKey = 0;
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
                if (key[0] == PersistedSnapshotKey.MetadataColumn) break;

                bool isPerAddr = key[0] == PersistedSnapshotKey.AccountColumn;
                // On entering a new per-address group, cache its address + bloom key and reset the barrier;
                // the account sorts first, then the self-destruct record (if any), which sets the barrier
                // before the slots.
                if (isPerAddr && (curAddr is null || !PersistedSnapshotKey.PerAddressAddress(key).SequenceEqual(curAddr.Bytes)))
                {
                    curAddr = new Address(PersistedSnapshotKey.PerAddressAddress(key));
                    addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(curAddr);
                    // addrKey is the single key the reader probes for this address's account, self-destruct,
                    // and the address half of every slot lookup — add it once per group, not per record.
                    bloom.Add(addrBloomKey);
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
                    if (sub == PersistedSnapshotKey.SelfDestructSub)
                    {
                        // Self-destruct sorts before this address's slots: resolve the truncation barrier
                        // here so the slots that follow can be filtered and streamed without buffering.
                        barrier = ComputeSelfDestructBarrier<TView, TReader, TPin>(views, enums, matching[..matchCount]);
                        EmitSelfDestruct(ref table, key, barrier);
                    }
                    else if (sub == PersistedSnapshotKey.SlotSub)
                    {
                        // Stream the slot, dropping it when its newest source predates the self-destruct.
                        if (barrier < 0 || newest >= barrier)
                            EmitSlot<TWriter, TView, TReader, TPin>(views, enums, ref table, bloom, key, newest, addrBloomKey);
                    }
                    else // account
                    {
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

        }
        finally
        {
            // Dispose every constructed enumerator, even if the init loop or the merge loop threw
            // partway — otherwise the readers/pins they hold leak.
            for (int i = 0; i < initialized; i++) enums[i].Dispose();
        }
    }

    /// <summary>Emit the newest source's value for a slot <paramref name="key"/> and its bloom keys.</summary>
    private static void EmitSlot<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> key, int newest, ulong addrBloomKey)
        where TWriter : IByteBufferWriter
        where TView : IByteReaderSource<TReader, TPin>
        where TReader : IByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        TReader r = views[newest].CreateReader();
        using TPin pin = r.PinBuffer(enums[newest].CurrentValue);
        table.Add(key, pin.Buffer);
        bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, PersistedSnapshotKey.SlotKeyBytes(key)));
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
        ref SortedTableBuilder<TWriter> table, scoped ReadOnlySpan<byte> key, int barrier)
        where TWriter : IByteBufferWriter =>
        table.Add(key, barrier >= 0
            ? PersistedSnapshotTags.SelfDestructDestructedMarker
            : PersistedSnapshotTags.SelfDestructNewMarker);

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
                break; // addrKey is added once per group when the group is entered
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
        // strict-ascending invariant holds: from_block < from_hash < noderefs < to_block < to_hash.
        // The sources' blob_range (which sorts before from_block) is intentionally not carried over:
        // it describes a base snapshot's own contiguous trie-RLP run, and a merged snapshot references
        // blobs via its ref-id records instead (BlobRange.None).
        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromHashKey);

        Span<byte> noderefsKey = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int noderefsLen = PersistedSnapshotKey.WriteMetadataKey(noderefsKey, PersistedSnapshotTags.MetadataNodeRefsKey);
        table.Add(noderefsKey[..noderefsLen], PersistedSnapshotTags.MetadataNodeRefsPresentMarker);

        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToHashKey);

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
