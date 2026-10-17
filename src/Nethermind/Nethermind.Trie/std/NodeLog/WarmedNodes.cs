// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>The trie nodes fetched from persistence for one block, as hash-verified records keyed by node position.</summary>
/// <remarks>
/// The arena has no budget beyond the <see cref="RecordTable"/> limit, so a verified insert always yields a record for its caller,
/// even when a later insert takes its slot.
/// </remarks>
/// <param name="capacity">The expected number of nodes, which sizes the slot table.</param>
internal sealed class WarmedNodes(int capacity)
{
    private const uint SlotsPerNode = 4;
    private const int MaxStackRecordLength = 1024;

    private readonly RecordTable _table = new((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 1) * SlotsPerNode));

    /// <inheritdoc cref="RecordTable.Count"/>
    public int Count => _table.Count;

    /// <inheritdoc cref="RecordTable.ArenaBytes"/>
    public long ArenaBytes => _table.ArenaBytes;

    /// <inheritdoc cref="RecordTable.TryGet(Hash256, in TreePath, in ValueHash256, out NodeRecordRef)"/>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record) =>
        _table.TryGet(address, path, hash, out record);

    /// <inheritdoc cref="RecordTable.TryGet(Hash256, in TreePath, in ValueHash256, out NodeRecordRef, out ulong)"/>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record, out ulong pointer) =>
        _table.TryGet(address, path, hash, out record, out pointer);

    /// <inheritdoc cref="RecordTable.TryGet(ulong, in ValueHash256, out NodeRecordRef)"/>
    public bool TryGet(ulong pointer, in ValueHash256 hash, out NodeRecordRef record) =>
        _table.TryGet(pointer, hash, out record);

    /// <summary>Verifies <paramref name="rlp"/> against <paramref name="expectedHash"/> and inserts its record.</summary>
    /// <param name="record">The inserted record, or the one already held for the same position and hash.</param>
    /// <returns>False when the RLP does not hash to <paramref name="expectedHash"/> or is not a valid trie node.</returns>
    public bool TryInsert(Hash256? address, in TreePath path, in ValueHash256 expectedHash, ReadOnlySpan<byte> rlp, out NodeRecordRef record) =>
        TryInsert(address, path, expectedHash, rlp, out record, out _);

    /// <inheritdoc cref="TryInsert(Hash256, in TreePath, in ValueHash256, ReadOnlySpan{byte}, out NodeRecordRef)"/>
    /// <param name="pointer">The transient pointer to the inserted or existing record, or zero when verification fails.</param>
    public bool TryInsert(Hash256? address, in TreePath path, in ValueHash256 expectedHash, ReadOnlySpan<byte> rlp, out NodeRecordRef record, out ulong pointer)
    {
        if (ValueKeccak.Compute(rlp) != expectedHash || !NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask))
        {
            record = default;
            pointer = 0;
            return false;
        }

        return TryInsertVerified(address, path, expectedHash, rlp, kind, hashMask, [], out record, out pointer);
    }

    /// <summary>Encodes verified RLP with the known child pointers before publishing its record.</summary>
    /// <param name="childPointers">Pointers by child slot, or empty to leave every pointer zero.</param>
    [SkipLocalsInit]
    internal bool TryInsertVerified(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp,
        NodeRecordKind kind, ushort hashMask, ReadOnlySpan<ulong> childPointers, out NodeRecordRef record, out ulong pointer)
    {
        int length = NodeRecord.Length(BitOperations.PopCount(hashMask), rlp.Length);
        byte[]? rented = null;
        Span<byte> buffer = length <= MaxStackRecordLength
            ? stackalloc byte[MaxStackRecordLength]
            : rented = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> recordBytes = buffer[..length];
        NodeRecord.WriteFromRlp(rlp, kind, hashMask, hash, NodeRecord.NodeKey(address, path), recordBytes);
        int pointerOffset = NodeRecord.HeaderLength;
        for (int slot = 0; slot < childPointers.Length; slot++)
        {
            if ((hashMask & (1 << slot)) == 0) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(recordBytes[pointerOffset..], childPointers[slot]);
            pointerOffset += NodeRecord.PointerLength;
        }

        bool added = _table.TryAdd(address, path, recordBytes, out record, out pointer);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return added;
    }

    /// <inheritdoc cref="RecordTable.Reset"/>
    public void Reset() => _table.Reset();
}
