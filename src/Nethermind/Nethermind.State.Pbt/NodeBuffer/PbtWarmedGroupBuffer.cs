// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>The node groups warmed from persistence for one block, as records in a <see cref="PbtPrivateGroupRecordBuffer"/>, also indexed by group path.</summary>
/// <remarks>
/// Pointers carry <see cref="PbtGroupRecordCodec.TransientPointerMask"/>, so <see cref="PbtWarmedGroupRecordBuffer"/> can tell them from the global buffer's.
/// Transient pointers live only in this buffer's records and path index: records committed to the global buffer drop them,
/// so a pointer from before a <see cref="Reset"/> is unreachable after it.
/// Every record stays readable until the next <see cref="Reset"/>.
/// </remarks>
/// <param name="capacity">The expected number of groups, which sizes the path index.</param>
public sealed class PbtWarmedGroupBuffer(int capacity) : IDisposable
{
    private const int GenerationBytes = 1024 * 1024;

    private readonly PbtPrivateGroupRecordBuffer _buffer = new(GenerationBytes);
    // The path to pointer index is only for the warmer. The global buffer is not connected to the warmed one: its records never point
    // into it, as committed records drop transient pointers, so a warmed group below a global record is found by its path.
    private readonly ConcurrentDictionary<PbtStorageNodePath, ulong> _pointers = new(Environment.ProcessorCount, Math.Max(capacity, 1));

    /// <summary>Number of indexed group paths.</summary>
    /// <remarks>Takes every lock of the path index.</remarks>
    public int Count => _pointers.Count;

    /// <summary>Finds the record at a pointer of this buffer; misses on any pointer without the transient prefix.</summary>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        if (PbtGroupRecordCodec.IsTransient(pointer)) return _buffer.TryLocate(pointer & ~PbtGroupRecordCodec.TransientPointerMask, out record);

        record = default;
        return false;
    }

    /// <returns>True when the path holds a record.</returns>
    public bool TryGet(in PbtStorageNodePath path, out PbtGroupRecord record)
    {
        if (_pointers.TryGetValue(path, out ulong pointer) && TryLocate(pointer, out record)) return true;

        record = default;
        return false;
    }

    /// <summary>Reserves bytes for records as <see cref="IPbtStore.StartWriteBatch"/> does, addressed by transient pointers.</summary>
    /// <remarks>
    /// Records are trusted to be the groups their parents reference. Groups are written along a traversal from a known root, each read
    /// for the path its parent's boundary leads to, so a correct traversal writes correct groups and links a correct graph. For the same
    /// reason, a record read back from the buffer is not verified either: it is always reached through that traversal.
    /// </remarks>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new TransientWriteBatch(_buffer.StartWriteBatch(alignedTotalLength));

    /// <summary>Indexes the record at <paramref name="pointer"/> at its path, in place of any earlier one.</summary>
    /// <remarks>Called only once the record is written, so a reader of the path never sees a partial record.</remarks>
    public void Index(in PbtStorageNodePath path, ulong pointer) => _pointers[path] = pointer;

    /// <summary>Empties the path index and releases the records appended so far.</summary>
    /// <remarks>No reader or writer may be running, and no record or pointer handed out before may be used after.</remarks>
    public void Reset()
    {
        _pointers.NoResizeClear();
        _buffer.Clear();
    }

    public void Dispose() => _buffer.Dispose();

    private sealed class TransientWriteBatch(IPbtGroupRecordWriteBatch batch) : IPbtGroupRecordWriteBatch
    {
        public ulong BasePointer => batch.BasePointer | PbtGroupRecordCodec.TransientPointerMask;

        public Span<byte> Bytes => batch.Bytes;

        public void Dispose() => batch.Dispose();
    }
}
