// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.Test;

internal static class TestFixtureHelpers
{
    /// <summary>
    /// Creates a real <see cref="ArenaManager"/> over <paramref name="dir"/> configured for tests:
    /// the arena file size is floored to one OS page so tiny test sizes don't trip the mmap minimum.
    /// </summary>
    public static ArenaManager CreateArenaManager(string dir, int arenaSize = 64 * 1024) =>
        new(dir, new FlatDbConfig
        {
            ArenaFileSizeBytes = Math.Max(arenaSize, Environment.SystemPageSize),
        }, LimboLogs.Instance);

    /// <summary>
    /// Materialise an entire reservation's bytes through a fresh reader. Test convenience for
    /// asserting on small whole-reservation payloads (throws if the reservation exceeds int range).
    /// </summary>
    public static byte[] ReadAll(WholeReadSession session)
    {
        WholeReadSessionReader reader = session.CreateReader();
        byte[] buf = new byte[checked((int)reader.Length)];
        reader.TryRead(0, buf);
        return buf;
    }

    /// <summary>
    /// Read the <c>ref_ids</c> list from the metadata inside <paramref name="reservation"/>
    /// and acquire a lease per id on <paramref name="blobs"/>. Mirrors what
    /// <c>SnapshotRepository</c> does at load time — the resulting
    /// <see cref="PersistedSnapshot"/>'s <c>CleanUp</c> drops one lease per id, keeping
    /// refcounts balanced. No-op when there are no ref_ids (raw test bytes that aren't
    /// a real sorted table).
    /// </summary>
    public static void LeaseBlobIds(ArenaReservation reservation, BlobArenaManager blobs)
    {
        using WholeReadSession session = reservation.BeginWholeReadSession();
        WholeReadSessionReader reader = session.CreateReader();
        ushort[]? ids = ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
        if (ids is null) return;
        foreach (ushort id in ids)
        {
            if (!blobs.TryLeaseFile(id, out _))
                throw new System.InvalidOperationException(
                    $"Test fixture's BlobArenaManager has no slot for id {id}; did Build() use a different manager?");
        }
    }

    /// <summary>
    /// Read the snapshot's referenced blob-arena ids (the ref-id records in column
    /// <see cref="PersistedSnapshotKey.RefIdColumn"/>) as a <c>ushort[]</c>, or <c>null</c> when
    /// there are none (e.g. raw test bytes that aren't a real table). Test-only convenience for
    /// asserting the referenced id set; production walks them via <c>PersistedSnapshot</c>'s
    /// internal ref-ids enumerator.
    /// </summary>
    public static ushort[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        List<ushort> ids = [];
        using SortedTableEnumerator<TReader, TPin> e = new(in reader, new Bound(0, reader.Length));
        while (e.MoveNext(in reader))
        {
            ReadOnlySpan<byte> key = e.CurrentKey;
            if (key.Length == 0 || key[0] != PersistedSnapshotKey.RefIdColumn) break;
            ids.Add(PersistedSnapshotKey.ReadRefId(key));
        }
        return ids.Count == 0 ? null : ids.ToArray();
    }

    /// <summary>
    /// Counts the data blocks in the sorted table held in <paramref name="bytes"/> by walking its index
    /// block — there is one index record per data block. Test-only: the footer does not store a block
    /// count, and tests use this to assert a fixture exercised the single- vs multi-block paths it intends.
    /// </summary>
    public static long DataBlockCount(byte[] bytes)
    {
        SpanByteReader reader = new(bytes);
        Bound table = new(0, reader.Length);
        if (!SortedTable.TryReadFooter<SpanByteReader, NoOpPin>(in reader, table, out SortedTable.Footer footer))
            throw new InvalidOperationException("Not a readable sorted table.");
        long indexStart = SortedTable.IndexBlockStart(table, footer);
        if (!IndexBlockReader.TryReadRecordRange<SpanByteReader, NoOpPin>(in reader, indexStart, out long recordsStart, out long recordsEnd))
            throw new InvalidOperationException("Unreadable index block.");

        // Step over each index record ([cp u8][suffixLen u8][valCp u8][valSuffixLen u8][keySuffix][valSuffix])
        // without decoding it.
        long pos = indexStart + recordsStart;
        long end = indexStart + recordsEnd;
        long count = 0;
        Span<byte> hdr = stackalloc byte[4];
        while (pos < end)
        {
            reader.TryRead(pos, hdr);
            int suffixLen = hdr[1];
            int valSuffixLen = hdr[3];
            pos += 4 + suffixLen + valSuffixLen;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Write <paramref name="data"/> into a fresh reservation on <paramref name="arena"/>,
    /// lease the blob ids referenced by its metadata (skipped when
    /// <paramref name="leaseBlobIds"/> is false) and wrap the result in a
    /// <see cref="PersistedSnapshot"/> over <paramref name="blobs"/>.
    /// </summary>
    public static PersistedSnapshot CreatePersistedSnapshot(
        IArenaManager arena, BlobArenaManager blobs, StateId from, StateId to, byte[] data,
        bool leaseBlobIds = true)
    {
        using ArenaWriter writer = arena.CreateWriter(data.Length);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        if (leaseBlobIds) LeaseBlobIds(reservation, blobs);
        return new PersistedSnapshot(from, to, reservation, blobs, SnapshotTier.PersistedBase, RefCountedBloomFilter.AlwaysTrue());
    }

    /// <summary>
    /// Populates <paramref name="content"/> with a contiguous run of storage slots
    /// <c>[firstSlot, firstSlot + count)</c> on <paramref name="address"/>, each carrying a
    /// distinct full 32-byte value (see <see cref="SequentialSlotValue"/>).
    /// </summary>
    /// <remarks>
    /// Slot indices are stored big-endian, so a run of 65536 consecutive slots shares one
    /// 30-byte slot-prefix and forms a single dense prefix group. The values keep a non-zero
    /// leading byte so <c>WithoutLeadingZeros()</c> cannot trim them — a full group's inner
    /// sub-slot table then stays large enough to exceed an <c>ArenaBufferWriter</c> buffer.
    /// </remarks>
    public static void AddSequentialSlots(SnapshotContent content, Address address, int firstSlot, int count)
    {
        for (int slot = firstSlot; slot < firstSlot + count; slot++)
            content.Storages[(address, (UInt256)slot)] = new SlotValue(SequentialSlotValue(slot));
    }

    /// <summary>
    /// A 32-byte storage value encoding <paramref name="slot"/> in its trailing four bytes,
    /// with a non-zero leading byte so it survives <c>WithoutLeadingZeros()</c> trimming intact.
    /// </summary>
    public static byte[] SequentialSlotValue(int slot)
    {
        byte[] value = new byte[32];
        value[0] = 0xFF;
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(28, 4), slot);
        return value;
    }
}
