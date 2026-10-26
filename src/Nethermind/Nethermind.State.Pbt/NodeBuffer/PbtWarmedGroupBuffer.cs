// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>The node groups warmed from persistence for one block, as records in a <see cref="PbtPrivateGroupRecordBuffer"/>, indexed by group path.</summary>
/// <remarks>
/// No record of the global buffer links into this buffer: a group is found here only by its path.
/// Every record stays readable until the next <see cref="Reset"/>.
/// </remarks>
/// <param name="capacity">The expected number of groups, which sizes the path index.</param>
public sealed class PbtWarmedGroupBuffer(int capacity) : IDisposable
{
    private const int GenerationBytes = 1024 * 1024;

    private readonly PbtPrivateGroupRecordBuffer _buffer = new(GenerationBytes);
    // The path to pointer index is only for the warmer: no record of the global buffer links into this one.
    private readonly ConcurrentDictionary<PbtStorageNodePath, ulong> _pointers = new(Environment.ProcessorCount, Math.Max(capacity, 1));

    /// <summary>Number of indexed group paths.</summary>
    /// <remarks>Takes every lock of the path index.</remarks>
    public int Count => _pointers.Count;

    /// <returns>True when the path holds a record.</returns>
    public bool TryGet(in PbtStorageNodePath path, out PbtGroupRecord record)
    {
        if (_pointers.TryGetValue(path, out ulong pointer) && _buffer.TryLocate(pointer, out record)) return true;

        record = default;
        return false;
    }

    /// <summary>Reserves bytes for records as <see cref="IPbtStore.StartWriteBatch"/> does.</summary>
    /// <remarks>
    /// Records are trusted to be the groups their parents reference, as they are read along a traversal from a known root.
    /// For the same reason, a record read back from the buffer is not verified either.
    /// </remarks>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => _buffer.StartWriteBatch(alignedTotalLength);

    /// <summary>Indexes the record at <paramref name="pointer"/> at its path, in place of any earlier one.</summary>
    /// <remarks>Called only once the record is written, so a reader of the path never sees a partial record.</remarks>
    public void Index(in PbtStorageNodePath path, ulong pointer) => _pointers[path] = pointer;

    /// <summary>Empties the path index and releases the records appended so far.</summary>
    /// <remarks>No reader or writer may be running, and no record handed out before may be used after.</remarks>
    public void Reset()
    {
        _pointers.NoResizeClear();
        _buffer.Clear();
    }

    public void Dispose() => _buffer.Dispose();
}
