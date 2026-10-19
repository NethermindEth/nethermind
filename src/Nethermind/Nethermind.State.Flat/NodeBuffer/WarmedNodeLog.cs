// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>The trie nodes fetched from persistence for one block, as hash-verified records in a private node-record buffer, also indexed by node position.</summary>
/// <remarks>
/// Pointers carry <see cref="NodeRecordCodec.TransientPointerMask"/>, so <see cref="WarmedNodeRecordBuffer"/> can tell them from the global buffer's.
/// Transient pointers live only in this log's records and position index: records committed to the global buffer drop them,
/// so a pointer from before a <see cref="Reset"/> is unreachable after it.
/// Every record stays readable until the next <see cref="Reset"/>.
/// </remarks>
public sealed class WarmedNodeLog : IDisposable
{
    private const int GenerationBytes = 1024 * 1024;

    private readonly NodeRecordBuffer _buffer = new(0, GenerationBytes);
    private readonly ConcurrentDictionary<(Hash256? Address, TreePath Path), ulong> _pointers;
    private IDisposable _pin;

    /// <param name="capacity">The expected number of nodes, which sizes the position index.</param>
    public WarmedNodeLog(int capacity)
    {
        _pointers = new(Environment.ProcessorCount, Math.Max(capacity, 1));
        _pin = _buffer.PinAll();
    }

    /// <summary>Number of indexed node positions.</summary>
    /// <remarks>Takes every lock of the position index.</remarks>
    public int Count => _pointers.Count;

    /// <summary>Finds the record at a pointer of this log; misses on any pointer without the transient prefix.</summary>
    public bool TryLocate(ulong pointer, out NodeRecord record)
    {
        if (NodeRecordCodec.IsTransient(pointer)) return _buffer.TryLocate(pointer & ~NodeRecordCodec.TransientPointerMask, out record);

        record = default;
        return false;
    }

    /// <returns>True when the node at the position is a record of <paramref name="hash"/>.</returns>
    /// <param name="pointer">The pointer to the record, or zero on a miss.</param>
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecord record, out ulong pointer)
    {
        if (_pointers.TryGetValue((address, path), out pointer) && TryLocate(pointer, out record) && record.Hash == hash) return true;

        record = default;
        pointer = 0;
        return false;
    }

    /// <summary>Verifies <paramref name="rlp"/> against <paramref name="expectedHash"/> and appends its record.</summary>
    /// <param name="record">The appended record, or the one already held for the same position and hash.</param>
    /// <param name="pointer">The pointer to the record, or zero when verification fails.</param>
    /// <returns>False when the RLP does not hash to <paramref name="expectedHash"/> or is not a valid trie node.</returns>
    public bool TryAppend(Hash256? address, in TreePath path, in ValueHash256 expectedHash, ReadOnlySpan<byte> rlp, out NodeRecord record, out ulong pointer)
    {
        if (ValueKeccak.Compute(rlp) != expectedHash || !NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask))
        {
            record = default;
            pointer = 0;
            return false;
        }

        AppendVerified(address, path, expectedHash, rlp, kind, hashMask, [], out record, out pointer);
        return true;
    }

    /// <summary>Appends the record of verified RLP with the known child pointers, unless the position already holds it.</summary>
    /// <param name="childPointers">Pointers by child slot, or empty to leave every pointer zero.</param>
    internal void AppendVerified(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp,
        NodeRecordKind kind, ushort hashMask, ReadOnlySpan<ulong> childPointers, out NodeRecord record, out ulong pointer)
    {
        if (TryGet(address, path, hash, out record, out pointer)) return;

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length);
        ulong localPointer;
        using (INodeRecordWriteBatch batch = _buffer.StartWriteBatch(NodeRecordCodec.AlignedLength(length)))
        {
            Span<byte> recordBytes = batch.Bytes;
            NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, NodeRecordCodec.NodeKeyHash(address, path), recordBytes);
            int pointerOffset = NodeRecordCodec.HeaderLength;
            for (int slot = 0; slot < childPointers.Length; slot++)
            {
                if ((hashMask & (1 << slot)) == 0) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(recordBytes[pointerOffset..], childPointers[slot]);
                pointerOffset += NodeRecordCodec.PointerLength;
            }

            localPointer = batch.BasePointer;
        }

        pointer = localPointer | NodeRecordCodec.TransientPointerMask;
        _pointers[(address, path)] = pointer;
        record = _buffer.GetRecord(localPointer);
    }

    /// <summary>Empties the position index and releases the records appended so far.</summary>
    /// <remarks>No reader or writer may be running, and no record or pointer handed out before may be used after.</remarks>
    public void Reset()
    {
        _pointers.NoResizeClear();
        _pin.Dispose();
        _pin = _buffer.PinAll();
    }

    public void Dispose()
    {
        _pin.Dispose();
        _buffer.Dispose();
    }
}
