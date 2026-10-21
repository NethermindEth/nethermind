// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>The node groups fetched from persistence for one block, as records in a <see cref="PbtPrivateGroupRecordBuffer"/>, indexed by group path.</summary>
/// <remarks>
/// No record of the global buffer links into this log: a group is found here only by its path.
/// Every record stays readable until the next <see cref="Reset"/>.
/// </remarks>
/// <param name="capacity">The expected number of groups, which sizes the path index.</param>
public sealed class PbtWarmedGroupLog(int capacity) : IDisposable
{
    private const int GenerationBytes = 1024 * 1024;

    private readonly PbtPrivateGroupRecordBuffer _buffer = new(GenerationBytes);
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

    /// <summary>Appends the record of the group <paramref name="payload"/>, with no links, and indexes it at its path in place of any earlier one.</summary>
    /// <remarks>The group is trusted to be the one its parent references, as it is read along a traversal from a known root.</remarks>
    /// <param name="record">The appended record.</param>
    public void Append(in PbtStorageNodePath path, ReadOnlySpan<byte> payload, out PbtGroupRecord record)
    {
        ulong pointer = _buffer.Append(path, payload, default);
        _pointers[path] = pointer;
        bool located = _buffer.TryLocate(pointer, out record);
        Debug.Assert(located, "A record stays readable until the next reset.");
    }

    /// <summary>Empties the path index and releases the records appended so far.</summary>
    /// <remarks>No reader or writer may be running, and no record handed out before may be used after.</remarks>
    public void Reset()
    {
        _pointers.NoResizeClear();
        _buffer.Clear();
    }

    public void Dispose() => _buffer.Dispose();
}
