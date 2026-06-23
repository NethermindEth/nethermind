// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;
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
/// <see cref="IHsstReaderSource{TReader,TPin}"/> that mints a fresh reader on demand (production
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
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(bloom);

        long estimatedKeys = 0;
        for (int i = 0; i < views.Length; i++)
        {
            TReader r = views[i].CreateReader();
            if (SortedTable.TryReadFooter<TReader, TPin>(in r, new Bound(0, r.Length), out long c, out _))
                estimatedKeys += c;
        }

        SortedTableBuilder<TWriter> table = new(ref writer, (int)Math.Min(estimatedKeys + 8, int.MaxValue));
        try
        {
            MergeMetadata<TWriter, TView, TReader, TPin>(views, ref table);
            MergeEntries<TWriter, TView, TReader, TPin>(views, ref table, bloom);
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
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
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
        Span<int> matching = stackalloc int[n];

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
            // On any address change (or leaving the per-address column), flush the previous
            // address's buffered slots using the barrier resolved from its self-destruct record.
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
                    barrier = MergeSelfDestruct<TWriter, TView, TReader, TPin>(views, enums, ref table, bloom, key, matching[..matchCount]);
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

        if (haveAddr) FlushPendingSlots(ref table, bloom, curAddr, barrier, pendingKeys, pendingValues, pending);
    }

    private static void BufferSlot<TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ReadOnlySpan<byte> key, int newest,
        NativeMemoryList<byte> pendingKeys, NativeMemoryList<byte> pendingValues, NativeMemoryList<PendingSlot> pending)
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
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

    /// <summary>Emit the self-destruct record (destructed if any source destructed, else new) and
    /// return the truncation barrier — the newest source index that destructed, or -1.</summary>
    private static int MergeSelfDestruct<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<int> matching)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int barrier = -1;
        for (int k = 0; k < matching.Length; k++)
        {
            int i = matching[k];
            byte flag = 0;
            TReader r = views[i].CreateReader();
            r.TryRead(enums[i].CurrentValue.Offset, new Span<byte>(ref flag));
            if (flag == PersistedSnapshotTags.SelfDestructDestructedMarkerByte) barrier = i; // newest destructed
        }

        table.Add(key, barrier >= 0
            ? PersistedSnapshotTags.SelfDestructDestructedMarker
            : PersistedSnapshotTags.SelfDestructNewMarker);
        bloom.Add(PersistedSnapshotBloomBuilder.AddressKey(PersistedSnapshotKey.PerAddressAddress(key)));
        return barrier;
    }

    /// <summary>Emit the newest source's value for <paramref name="key"/> (account / state node /
    /// storage node) and add the matching bloom key.</summary>
    private static void EmitNewest<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, SortedTableEnumerator<TReader, TPin>[] enums,
        ref SortedTableBuilder<TWriter> table, BloomFilter bloom, scoped ReadOnlySpan<byte> key, int newest)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
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
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        TReader oldest = views[0].CreateReader();
        Bound oldestTable = new(0, oldest.Length);
        TReader newest = views[n - 1].CreateReader();
        Bound newestTable = new(0, newest.Length);

        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in oldest, oldestTable, PersistedSnapshotTags.MetadataFromHashKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToBlockKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataToHashKey);
        AddMetadataField<TWriter, TReader, TPin>(ref table, in newest, newestTable, PersistedSnapshotTags.MetadataVersionKey);

        Span<byte> noderefsKey = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int noderefsLen = PersistedSnapshotKey.WriteMetadataKey(noderefsKey, PersistedSnapshotTags.MetadataNodeRefsKey);
        table.Add(noderefsKey[..noderefsLen], PersistedSnapshotTags.MetadataNodeRefsPresentMarker);

        MergeRefIds<TWriter, TView, TReader, TPin>(views, ref table);
    }

    private static void AddMetadataField<TWriter, TReader, TPin>(
        ref SortedTableBuilder<TWriter> table, scoped in TReader reader, Bound metaTable, ReadOnlySpan<byte> name)
        where TWriter : IByteBufferWriter
        where TReader : IHsstByteReader<TPin>, allows ref struct
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

    /// <summary>Union of every source's sorted little-endian ushort ref_ids run, emitted sorted.</summary>
    private static void MergeRefIds<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref SortedTableBuilder<TWriter> table)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        Span<byte> key = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int keyLen = PersistedSnapshotKey.WriteMetadataKey(key, PersistedSnapshotTags.MetadataRefIdsKey);

        SortedSet<ushort> ids = [];
        for (int i = 0; i < views.Length; i++)
        {
            TReader r = views[i].CreateReader();
            if (!SortedTableReader.TrySeek<TReader, TPin>(in r, new Bound(0, r.Length), key[..keyLen], out Bound vb)
                || vb.Length == 0 || vb.Length % 2 != 0)
                continue;
            using TPin pin = r.PinBuffer(vb);
            ReadOnlySpan<byte> bytes = pin.Buffer;
            for (int o = 0; o + 2 <= bytes.Length; o += 2)
                ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes[o..]));
        }

        byte[] buf = new byte[ids.Count * 2];
        int w = 0;
        foreach (ushort id in ids)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(w), id);
            w += 2;
        }
        table.Add(key[..keyLen], buf);
    }
}
