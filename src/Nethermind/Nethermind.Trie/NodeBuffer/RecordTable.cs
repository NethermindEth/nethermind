// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A lock-free map from a node position to one node record, over an arena of record copies.</summary>
/// <remarks>
/// The table is direct-mapped by the low bits of <see cref="NodeRecordCodec.NodeKey"/> and checks the full address and path.
/// An add that collides with another position replaces it. The arena is a list of 1 MiB chunks that never move.
/// Adds run under a lock and publish an immutable entry after copying its record, so readers need no lock.
/// Pointers encode a resource/reset generation and an arena position, independent of the replaceable index entries.
/// </remarks>
/// <param name="slotCount">A power of two.</param>
internal sealed class RecordTable(int slotCount)
{
    private const int ChunkBytes = 1 << 20;
    private const int MaxChunks = 1 << 15;
    private const int OffsetBits = 17;
    private const uint OffsetMask = (1u << OffsetBits) - 1;
    private const int AlignmentBits = 3;

    private static long _epochCounter;

    private readonly Lock _lock = new();
    private readonly Entry?[] _slots = BitOperations.IsPow2(slotCount)
        ? new Entry?[slotCount]
        : throw new ArgumentException("The slot count must be a power of two.", nameof(slotCount));
    private readonly ulong _slotMask = (ulong)slotCount - 1;
    private int _count;
    private uint _generation = NodeRecordCodec.NextTransientGeneration(ref _epochCounter);
    private byte[][] _chunks = [];
    private int _chunkCount;
    private int _chunkUsed;
    private long _arenaBytes;

    /// <summary>Number of records copied into the arena since the last <see cref="Reset"/>, including replaced ones.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Bytes allocated by the arena.</summary>
    public long ArenaBytes => Volatile.Read(ref _arenaBytes);

    /// <returns>True when the slot of the position holds a record of <paramref name="hash"/> at that position.</returns>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecord record) =>
        TryGet(address, path, hash, out record, out _);

    /// <inheritdoc cref="TryGet(Hash256, in TreePath, in ValueHash256, out NodeRecord)"/>
    /// <param name="pointer">The transient pointer to the matching record, or zero on a miss.</param>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecord record, out ulong pointer)
    {
        ulong nodeKey = NodeRecordCodec.NodeKey(address, path);
        Entry? entry = Volatile.Read(ref _slots[nodeKey & _slotMask]);
        if (entry is not null && entry.Address == address && entry.Path == path)
        {
            record = RecordAt(entry.Position);
            if (record.Hash == hash)
            {
                pointer = PointerAt(entry.Position);
                return true;
            }
        }

        record = default;
        pointer = 0;
        return false;
    }

    /// <summary>Resolves a pointer in this resource's current generation, independent of the position index.</summary>
    public bool TryGet(ulong pointer, in ValueHash256 hash, out NodeRecord record)
    {
        if (pointer >> NodeRecordCodec.OffsetBits == Volatile.Read(ref _generation))
        {
            uint position = (uint)pointer;
            int chunk = (int)(position >> OffsetBits);
            int offset = OffsetOf(position);
            int chunkCount = Volatile.Read(ref _chunkCount);
            if (chunk < chunkCount)
            {
                byte[] bytes = Volatile.Read(ref _chunks)[chunk];
                if (offset <= bytes.Length - NodeRecordCodec.HeaderLength)
                {
                    record = new NodeRecord(bytes, offset);
                    if (record.Hash == hash) return true;
                }
            }
        }

        record = default;
        return false;
    }

    /// <summary>Copies <paramref name="record"/> into the arena and maps its node position to the copy.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="path">The path of the node in its trie.</param>
    /// <param name="record">A whole record of the node at <paramref name="path"/>; its node key picks the slot.</param>
    /// <param name="added">The copy, or the record already mapped when it has the same position and hash.</param>
    /// <param name="pointer">The transient pointer to the added or existing record, or zero when the arena is full.</param>
    /// <returns>False when the arena is full.</returns>
    public bool TryAdd(Hash256? address, in TreePath path, ReadOnlySpan<byte> record, out NodeRecord added, out ulong pointer)
    {
        NodeRecordRef view = new(record);
        ulong nodeKey = view.NodeKey;
        Debug.Assert(nodeKey == NodeRecordCodec.NodeKey(address, path), "The record is not of the node at the path.");
        ref Entry? slot = ref _slots[nodeKey & _slotMask];
        lock (_lock)
        {
            if (slot is { } existing && existing.Address == address && existing.Path == path)
            {
                added = RecordAt(existing.Position);
                if (added.Hash == view.Hash)
                {
                    pointer = PointerAt(existing.Position);
                    return true;
                }
            }

            int alignedLength = NodeRecordCodec.AlignedLength(record.Length);
            if (!TryReserve(alignedLength, out uint position))
            {
                added = default;
                pointer = 0;
                return false;
            }

            record.CopyTo(_chunks[position >> OffsetBits].AsSpan(OffsetOf(position)));
            Entry entry = new(address, path, position);
            Volatile.Write(ref slot, entry);
            Volatile.Write(ref _count, _count + 1);
            added = RecordAt(position);
            pointer = PointerAt(position);
            return true;
        }
    }

    /// <summary>Empties the table and rewinds the arena, keeping its first chunk.</summary>
    /// <remarks>No reader may be running, and no record reference handed out before may be used after, since the arena bytes are reused.</remarks>
    public void Reset()
    {
        lock (_lock)
        {
            uint generation = NodeRecordCodec.NextTransientGeneration(ref _epochCounter);
            Array.Clear(_slots);
            Volatile.Write(ref _generation, generation);
            Volatile.Write(ref _count, 0);
            if (_chunkCount == 0) return;

            Array.Clear(_chunks, 1, _chunkCount - 1);
            _chunkCount = 1;
            _chunkUsed = 0;
            Volatile.Write(ref _arenaBytes, _chunks[0].Length);
        }
    }

    private ulong PointerAt(uint position) => (ulong)_generation << NodeRecordCodec.OffsetBits | position;

    private static int OffsetOf(uint position) => (int)(position & OffsetMask) << AlignmentBits;

    private NodeRecord RecordAt(uint position) => new(Volatile.Read(ref _chunks)[position >> OffsetBits], OffsetOf(position));

    private bool TryReserve(int alignedLength, out uint position)
    {
        if (_chunkCount == 0 || _chunkUsed + alignedLength > _chunks[_chunkCount - 1].Length)
        {
            if (_chunkCount == MaxChunks || alignedLength > ChunkBytes)
            {
                position = 0;
                return false;
            }

            AddChunk(ChunkBytes);
        }

        position = (uint)(_chunkCount - 1) << OffsetBits | (uint)_chunkUsed >> AlignmentBits;
        _chunkUsed += alignedLength;
        return true;
    }

    private void AddChunk(int length)
    {
        if (_chunkCount == _chunks.Length)
        {
            byte[][] grown = new byte[Math.Max(4, _chunkCount * 2)][];
            _chunks.CopyTo(grown, 0);
            Volatile.Write(ref _chunks, grown);
        }

        _chunks[_chunkCount] = new byte[length];
        _chunkUsed = 0;
        Volatile.Write(ref _chunkCount, _chunkCount + 1);
        Volatile.Write(ref _arenaBytes, _arenaBytes + length);
    }

    private sealed class Entry(Hash256? address, in TreePath path, uint position)
    {
        public readonly Hash256? Address = address;
        public readonly TreePath Path = path;
        public readonly uint Position = position;
    }
}
