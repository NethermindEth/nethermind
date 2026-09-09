// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Read-by-key helpers for a persisted snapshot's single-level <see cref="SortedTable"/>. Each
/// helper materializes the verbose <see cref="PersistedSnapshotKey"/> for the entity and binary
/// searches the table; the returned <see cref="Bound"/> covers the entity's value, which the caller
/// (<see cref="PersistedSnapshot"/>) materializes. Streaming column scans live in
/// <see cref="PersistedSnapshotScanner"/>.
/// </summary>
public static class PersistedSnapshotReader
{
    internal static bool TryGetAccount<TReader, TPin>(scoped in TReader reader, Bound table, Address address, out Bound accountBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        Span<byte> key = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        int len = PersistedSnapshotKey.WriteAccountKey(key, address.Bytes);
        return SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out accountBound);
    }

    internal static bool TryGetSlot<TReader, TPin>(scoped in TReader reader, Bound table, Address address, in UInt256 index, out Bound slotBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        Span<byte> slot = stackalloc byte[32];
        index.ToBigEndian(slot);
        Span<byte> key = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        int len = PersistedSnapshotKey.WriteSlotKey(key, address.Bytes, slot);
        return SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out slotBound);
    }

    /// <returns><c>null</c> when the address has no self-destruct record in this snapshot,
    /// <c>false</c> when destructed (<c>[0x00]</c>), <c>true</c> when newly created (<c>[0x01]</c>).</returns>
    internal static bool? TryGetSelfDestructFlag<TReader, TPin>(scoped in TReader reader, Bound table, Address address)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        Span<byte> key = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        int len = PersistedSnapshotKey.WriteSelfDestructKey(key, address.Bytes);
        if (!SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out Bound b) || b.Length == 0)
            return null;
        byte flag = 0;
        if (!reader.TryRead(b.Offset, new Span<byte>(ref flag))) return null;
        return flag != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
    }

    /// <summary>
    /// Look up a state-trie node by tree path. Returns the value <see cref="Bound"/> holding a
    /// <see cref="NodeRef"/>; the caller decodes it and dereferences into the blob arena.
    /// </summary>
    internal static bool TryLoadStateNodeRlp<TReader, TPin>(scoped in TReader reader, Bound table, scoped in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        Span<byte> key = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        int len = PersistedSnapshotKey.WriteStateNodeKey(key, in path);
        return SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out bound);
    }

    internal static bool TryLoadStorageNodeRlp<TReader, TPin>(scoped in TReader reader, Bound table, in ValueHash256 addressHash, in TreePath path, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        Span<byte> key = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
        int len = PersistedSnapshotKey.WriteStorageNodeKey(key, addressHash.Bytes, in path);
        return SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out bound);
    }
}
