// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
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

    private readonly RecordTable _table = new((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 1) * SlotsPerNode), long.MaxValue);

    /// <inheritdoc cref="RecordTable.Count"/>
    public int Count => _table.Count;

    /// <inheritdoc cref="RecordTable.ArenaBytes"/>
    public long ArenaBytes => _table.ArenaBytes;

    /// <inheritdoc cref="RecordTable.TryGet"/>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record) =>
        _table.TryGet(address, path, hash, out record);

    /// <summary>Verifies <paramref name="rlp"/> against <paramref name="expectedHash"/> and inserts its record.</summary>
    /// <param name="record">The inserted record, or the one already held for the same position and hash.</param>
    /// <returns>False when the RLP does not hash to <paramref name="expectedHash"/> or is not a valid trie node.</returns>
    [SkipLocalsInit]
    public bool TryInsert(Hash256? address, in TreePath path, in ValueHash256 expectedHash, ReadOnlySpan<byte> rlp, out NodeRecordRef record)
    {
        if (ValueKeccak.Compute(rlp) != expectedHash || !NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask))
        {
            record = default;
            return false;
        }

        int length = NodeRecord.Length(BitOperations.PopCount(hashMask), rlp.Length);
        byte[]? rented = null;
        Span<byte> buffer = length <= MaxStackRecordLength
            ? stackalloc byte[MaxStackRecordLength]
            : rented = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> recordBytes = buffer[..length];
        NodeRecord.WriteFromRlp(rlp, kind, hashMask, expectedHash, NodeRecord.NodeKey(address, path), recordBytes);
        bool added = _table.TryAdd(recordBytes, out record);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return added;
    }

    /// <inheritdoc cref="RecordTable.CopyAllTo"/>
    public void CopyAllTo(ICollection<NodeRecordRef> records) => _table.CopyAllTo(records);

    /// <inheritdoc cref="RecordTable.Reset"/>
    public void Reset() => _table.Reset();
}
