// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Helpers shared across the test fixtures that wrap synthesised
/// <see cref="PersistedSnapshot"/> instances.
/// </summary>
internal static class TestFixtureHelpers
{
    /// <summary>
    /// Read the <c>ref_ids</c> list from the metadata HSST inside <paramref name="reservation"/>
    /// and acquire a lease per id on <paramref name="blobs"/>. Mirrors what
    /// <c>PersistedSnapshotRepository</c> does at load time — the resulting
    /// <see cref="PersistedSnapshot"/>'s <c>CleanUp</c> drops one lease per id, keeping
    /// refcounts balanced. No-op when the HSST has no ref_ids (raw test bytes that aren't
    /// a real HSST).
    /// </summary>
    public static void LeaseBlobIdsFromHsst(ArenaReservation reservation, BlobArenaManager blobs)
    {
        ArenaByteReader reader = reservation.CreateReader();
        ushort[]? ids = PersistedSnapshotReader.ReadRefIdsFromMetadata<ArenaByteReader, NoOpPin>(in reader);
        if (ids is null) return;
        foreach (ushort id in ids)
        {
            if (!blobs.TryLeaseFile(id, out _))
                throw new System.InvalidOperationException(
                    $"Test fixture's BlobArenaManager has no slot for id {id}; did Build() use a different manager?");
        }
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
        return new PersistedSnapshot(from, to, reservation, blobs);
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
