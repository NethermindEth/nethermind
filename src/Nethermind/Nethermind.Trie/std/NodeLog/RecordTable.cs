// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>A lock-free map from a node position to one node record, over an arena of record copies.</summary>
/// <remarks>
/// The table is direct-mapped by <see cref="NodeRecord.NodeKey"/>: the low key bits pick the slot, and a slot entry packs
/// <c>signature:32 | chunk:15 | offset/8:17</c>, where the signature is the high 32 key bits with bit 0 set so that 0 means empty.
/// An add that collides with another position replaces it. The arena is a list of 1 MiB chunks that never move.
/// Adds run under a lock and publish a record with a release write of its slot after copying it, so readers need no lock.
/// The address and path of every copied record are kept beside it for <see cref="CopyAllTo"/>.
/// </remarks>
/// <param name="slotCount">A power of two.</param>
/// <param name="maxArenaBytes">The arena budget; an add that does not fit fails.</param>
internal sealed class RecordTable(int slotCount, long maxArenaBytes)
{
    private const int ChunkBytes = 1 << 20;
    private const int MaxChunks = 1 << 15;
    private const int OffsetBits = 17;
    private const uint OffsetMask = (1u << OffsetBits) - 1;
    private const int AlignmentBits = 3;

    private readonly Lock _lock = new();
    private readonly ulong[] _slots = BitOperations.IsPow2(slotCount)
        ? new ulong[slotCount]
        : throw new ArgumentException("The slot count must be a power of two.", nameof(slotCount));
    private readonly ulong _slotMask = (ulong)slotCount - 1;
    private readonly long _maxArenaBytes = Math.Min(maxArenaBytes, (long)MaxChunks * ChunkBytes);
    private readonly List<(Hash256? Address, TreePath Path, uint Position)> _addedRecords = [];
    private byte[][] _chunks = [];
    private int _chunkCount;
    private int _chunkUsed;
    private long _arenaBytes;

    /// <summary>Number of records copied into the arena since the last <see cref="Reset"/>, including replaced ones.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _addedRecords.Count;
        }
    }

    /// <summary>Bytes allocated by the arena.</summary>
    public long ArenaBytes => Volatile.Read(ref _arenaBytes);

    /// <returns>True when the slot of the position holds a record of <paramref name="hash"/> at that position.</returns>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        ulong nodeKey = NodeRecord.NodeKey(address, path);
        ulong entry = Volatile.Read(ref _slots[nodeKey & _slotMask]);
        if (entry >> 32 == Signature(nodeKey))
        {
            record = RecordAt((uint)entry);
            if (Matches(record.View, nodeKey, hash)) return true;
        }

        record = default;
        return false;
    }

    /// <summary>Copies <paramref name="record"/> into the arena and maps its node key to the copy.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="path">The path of the node in its trie.</param>
    /// <param name="record">A whole record of the node at <paramref name="path"/>; its node key picks the slot.</param>
    /// <param name="added">The copy, or the record already mapped when it has the same node key and hash.</param>
    /// <returns>False when the arena is full.</returns>
    public bool TryAdd(Hash256? address, in TreePath path, ReadOnlySpan<byte> record, out NodeRecordRef added)
    {
        RecordView view = new(record);
        ulong nodeKey = view.NodeKey;
        Debug.Assert(nodeKey == NodeRecord.NodeKey(address, path), "The record is not of the node at the path.");
        ref ulong slot = ref _slots[nodeKey & _slotMask];
        lock (_lock)
        {
            if (slot >> 32 == Signature(nodeKey))
            {
                added = RecordAt((uint)slot);
                if (Matches(added.View, nodeKey, view.Hash)) return true;
            }

            int alignedLength = (record.Length + NodeRecord.RecordAlignment - 1) & -NodeRecord.RecordAlignment;
            if (!TryReserve(alignedLength, out uint position))
            {
                added = default;
                return false;
            }

            record.CopyTo(_chunks[position >> OffsetBits].AsSpan(OffsetOf(position)));
            _addedRecords.Add((address, path, position));
            Volatile.Write(ref slot, Signature(nodeKey) << 32 | position);
            added = RecordAt(position);
            return true;
        }
    }

    /// <summary>Appends every record in the arena, in arena order, with the address and path it was added for, to <paramref name="records"/>.</summary>
    public void CopyAllTo(ICollection<(Hash256? Address, TreePath Path, NodeRecordRef Record)> records)
    {
        lock (_lock)
        {
            foreach ((Hash256? address, TreePath path, uint position) in _addedRecords)
            {
                records.Add((address, path, RecordAt(position)));
            }
        }
    }

    /// <summary>Empties the table and rewinds the arena, keeping its first chunk.</summary>
    /// <remarks>No reader may be running, and no record reference handed out before may be used after, since the arena bytes are reused.</remarks>
    public void Reset()
    {
        lock (_lock)
        {
            foreach ((_, _, uint position) in _addedRecords)
            {
                _slots[RecordAt(position).View.NodeKey & _slotMask] = 0;
            }

            _addedRecords.Clear();
            if (_chunkCount == 0) return;

            Array.Clear(_chunks, 1, _chunkCount - 1);
            _chunkCount = 1;
            _chunkUsed = 0;
            Volatile.Write(ref _arenaBytes, _chunks[0].Length);
        }
    }

    private static ulong Signature(ulong nodeKey) => nodeKey >> 32 | 1;

    private static int OffsetOf(uint position) => (int)(position & OffsetMask) << AlignmentBits;

    private static bool Matches(in RecordView view, ulong nodeKey, in ValueHash256 hash) => view.NodeKey == nodeKey && view.Hash == hash;

    private NodeRecordRef RecordAt(uint position) => new(Volatile.Read(ref _chunks)[position >> OffsetBits], OffsetOf(position));

    private bool TryReserve(int alignedLength, out uint position)
    {
        if (_chunkCount == 0 || _chunkUsed + alignedLength > _chunks[_chunkCount - 1].Length)
        {
            int chunkLength = (int)Math.Min(ChunkBytes, _maxArenaBytes - _arenaBytes) & -NodeRecord.RecordAlignment;
            if (chunkLength < alignedLength)
            {
                position = 0;
                return false;
            }

            AddChunk(chunkLength);
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

        _chunks[_chunkCount++] = new byte[length];
        _chunkUsed = 0;
        Volatile.Write(ref _arenaBytes, _arenaBytes + length);
    }
}
