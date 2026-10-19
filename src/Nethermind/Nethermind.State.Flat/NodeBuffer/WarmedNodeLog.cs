// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>The trie nodes fetched from persistence for one block, as records in a private node-record buffer, also indexed by node position.</summary>
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
        _pin = _buffer.Pin();
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

    /// <returns>True when the position holds a record.</returns>
    /// <param name="pointer">The pointer to the record, or zero on a miss.</param>
    public bool TryGet(Hash256? address, in TreePath path, out NodeRecord record, out ulong pointer)
    {
        if (_pointers.TryGetValue((address, path), out pointer) && TryLocate(pointer, out record)) return true;

        record = default;
        pointer = 0;
        return false;
    }

    /// <summary>Appends the record of the node <paramref name="rlp"/> with the known child pointers, unless the position already holds a record.</summary>
    /// <remarks>
    /// The node is trusted to be the one of <paramref name="hash"/>, which only debug builds check. Nodes are appended along a traversal
    /// from a known root, each read for the hash its parent references, so a correct traversal appends correct nodes and links a correct
    /// graph. For the same reason, a record read back from the log is not verified either: it is always reached through that traversal.
    /// </remarks>
    /// <param name="childPointers">Pointers by child slot, or empty to leave every pointer zero.</param>
    /// <param name="record">The appended record, or the one already held for the same position.</param>
    /// <exception cref="TrieException">The RLP is not a valid trie node.</exception>
    public void Append(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp, ReadOnlySpan<ulong> childPointers,
        out NodeRecord record, out ulong pointer)
    {
        if (TryGet(address, path, out record, out pointer)) return;

        Debug.Assert(ValueKeccak.Compute(rlp) == hash, "A traversal only appends the node its parent references.");
        ulong localPointer = _buffer.AppendRecordFromRlp(address, path, hash, rlp, childPointers);
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
        _pin = _buffer.Pin();
    }

    public void Dispose()
    {
        _pin.Dispose();
        _buffer.Dispose();
    }
}
