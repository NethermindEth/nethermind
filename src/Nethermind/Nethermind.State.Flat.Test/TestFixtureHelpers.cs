// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.Test;

internal static class TestFixtureHelpers
{
    /// <summary>
    /// Creates a real <see cref="ArenaManager"/> over <paramref name="dir"/> configured for tests: the
    /// page-residency tracker is disabled (<c>PersistedSnapshotArenaPageCacheBytes = 0</c>) so no
    /// madvise/eviction runs, and the arena file size is floored to one OS page so tiny test sizes
    /// don't trip the mmap minimum.
    /// </summary>
    public static ArenaManager CreateArenaManager(string dir, int arenaSize = 64 * 1024) =>
        new(dir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
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
    /// Read the <c>ref_ids</c> list from the metadata HSST inside <paramref name="reservation"/>
    /// and acquire a lease per id on <paramref name="blobs"/>. Mirrors what
    /// <c>SnapshotRepository</c> does at load time — the resulting
    /// <see cref="PersistedSnapshot"/>'s <c>CleanUp</c> drops one lease per id, keeping
    /// refcounts balanced. No-op when the HSST has no ref_ids (raw test bytes that aren't
    /// a real HSST).
    /// </summary>
    public static void LeaseBlobIdsFromHsst(ArenaReservation reservation, BlobArenaManager blobs)
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
    /// Read the snapshot's <c>ref_ids</c> metadata entry (column 0x00) as a <c>ushort[]</c>,
    /// or <c>null</c> when the entry is absent or malformed. Test-only convenience for
    /// asserting the referenced blob-arena id set; production resolves ref-ids lazily through
    /// <c>PersistedSnapshot</c>'s internal ref-ids enumerator instead.
    /// </summary>
    public static ushort[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        using HsstReader<TReader, TPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshotTags.MetadataTag, out _) ||
            !r.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out _))
            return null;
        Bound b = r.GetBound();
        if (b.Length == 0 || b.Length % 2 != 0) return null;
        int len = checked((int)b.Length);
        int count = len / 2;
        Span<byte> buf = stackalloc byte[256];
        if (len > buf.Length)
            buf = new byte[len];
        if (!reader.TryRead(b.Offset, buf[..len])) return null;
        ushort[] ids = new ushort[count];
        for (int i = 0; i < count; i++)
            ids[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(i * 2, 2));
        return ids;
    }

    /// <summary>
    /// Write <paramref name="data"/> into a fresh reservation on <paramref name="arena"/>,
    /// lease the blob ids referenced by its metadata HSST (skipped when
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
        if (leaseBlobIds) LeaseBlobIdsFromHsst(reservation, blobs);
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
    /// sub-slot HSST then stays large enough to exceed an <c>ArenaBufferWriter</c> buffer.
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
