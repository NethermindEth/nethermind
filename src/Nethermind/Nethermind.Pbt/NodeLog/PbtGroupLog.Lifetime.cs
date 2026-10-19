// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Pbt.NodeLog;

/// <remarks>
/// Lifetime events are ordered by an epoch: an operation records the epoch it begins in, and each drop, unlink or retirement
/// takes the current epoch and advances it. An event is complete for every operation once no operation that began at or
/// before its epoch remains, so a dropped generation is compacted or unlinked only when no operation may still write it,
/// and unlinked storage is freed only when no operation may still borrow from it.
/// </remarks>
public sealed partial class PbtGroupLog
{
    private readonly SortedList<ulong, int> _operationEpochs = [];
    private readonly List<Generation> _dropped = [];
    private readonly List<(ulong Epoch, IDisposable Owner)> _retired = [];
    private ulong _epoch = 1;

    /// <summary>Protects borrowed records and reservations for an entire operation, including all of its workers.</summary>
    public OperationLease BeginOperation()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            _operationEpochs.TryGetValue(_epoch, out int count);
            _operationEpochs[_epoch] = count + 1;
            return new OperationLease(this, _epoch);
        }
    }

    /// <summary>Keeps the active generation and every later generation readable until the pin is disposed.</summary>
    /// <remarks>The pin's <see cref="GenerationPin.WriteBoundary"/> is the allocation frontier at this call.</remarks>
    public GenerationPin PinActiveGeneration()
    {
        lock (_lock)
        {
            Generation? active = _active;
            uint generation = active?.Id ?? checked(_lastGeneration + 1);
            return PinGeneration(generation, new PbtGroupReference(generation, active is null ? 0 : Volatile.Read(ref active.Used)));
        }
    }

    private GenerationPin PinGeneration(uint generation, PbtGroupReference writeBoundary)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            _pins.TryGetValue(generation, out int count);
            _pins[generation] = count + 1;
            return new GenerationPin(this, generation, writeBoundary);
        }
    }

    /// <summary>Stops new operations and writes, releasing storage after outstanding operations, pins and compaction finish.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            Trim();
        }
    }

    private uint OldestPinnedGeneration => _pins.Count == 0 ? uint.MaxValue : _pins.Keys[0];

    private ulong OldestOperationEpoch => _operationEpochs.Count == 0 ? ulong.MaxValue : _operationEpochs.Keys[0];

    private bool CompactsDropped => _compressedMemoryBudget > 0 && !_disposed && _compactionFailure is null;

    private bool KeepOpportunistically(Generation generation) =>
        !_disposed && _lastGeneration - generation.Id < (uint)_retainedGenerations;

    private void Trim()
    {
        uint oldestPinned = OldestPinnedGeneration;
        foreach ((uint id, Generation generation) in _generations)
        {
            if (generation.DroppedAt != 0 || generation.Buffer is null || id >= oldestPinned || KeepOpportunistically(generation)) continue;
            generation.DroppedAt = _epoch++;
            _dropped.Add(generation);
            if (ReferenceEquals(_active, generation)) Volatile.Write(ref _active, null);
        }

        ulong oldestOperation = OldestOperationEpoch;
        if (CompactsDropped) ScheduleCompaction(oldestOperation);
        else
        {
            for (int index = _dropped.Count - 1; index >= 0; index--)
                if (_dropped[index].DroppedAt < oldestOperation) Unlink(_dropped[index]);
            EvictCompressed();
        }
        FreeRetired(oldestOperation);
    }

    /// <summary>Removes a generation from lookups and retires its storage.</summary>
    private void Unlink(Generation generation)
    {
        ulong epoch = _epoch++;
        _generations.TryRemove(generation.Id, out _);
        _dropped.Remove(generation);
        if (generation.Buffer is { } buffer)
        {
            Interlocked.Add(ref _residentBytes, -buffer.Capacity);
            Retire(epoch, buffer);
        }
        if (generation.Compressed is { } compressed)
        {
            _compressedBytes -= compressed.Memory;
            Retire(epoch, compressed);
        }
        Retire(epoch, generation.Bloom);
    }

    private void Retire(ulong epoch, IDisposable? owner)
    {
        if (owner is not null) _retired.Add((epoch, owner));
    }

    private void FreeRetired(ulong oldestOperation)
    {
        int kept = 0;
        for (int index = 0; index < _retired.Count; index++)
        {
            (ulong epoch, IDisposable owner) = _retired[index];
            if (epoch < oldestOperation) owner.Dispose();
            else _retired[kept++] = _retired[index];
        }
        _retired.RemoveRange(kept, _retired.Count - kept);
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _compactionFailure) is { } failure) throw new InvalidOperationException("PBT group log compaction failed.", failure);
    }

    /// <summary>An operation-wide lease shared by workers without taking a lock per group read.</summary>
    /// <remarks>The owner must join all workers and stop using borrowed records before disposing this lease.</remarks>
    public sealed class OperationLease : IDisposable
    {
        private readonly PbtGroupLog _log;
        private readonly ulong _epoch;
        private int _disposed;

        internal OperationLease(PbtGroupLog log, ulong epoch)
        {
            _log = log;
            _epoch = epoch;
        }

        internal bool IsActive => Volatile.Read(ref _disposed) == 0;

        internal void EnsureActive() => ObjectDisposedException.ThrowIf(!IsActive, this);

        internal void EnsureActive(PbtGroupLog log)
        {
            EnsureActive();
            if (!ReferenceEquals(_log, log)) throw new ArgumentException("The operation belongs to a different group log.", nameof(log));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_log._lock)
            {
                int count = _log._operationEpochs[_epoch];
                if (count == 1) _log._operationEpochs.Remove(_epoch);
                else _log._operationEpochs[_epoch] = count - 1;
                _log.Trim();
            }
        }
    }

    /// <summary>Keeps one generation and every later generation available to snapshot readers.</summary>
    public sealed class GenerationPin : IDisposable
    {
        private readonly PbtGroupLog _log;
        private int _disposed;

        internal GenerationPin(PbtGroupLog log, uint generation, PbtGroupReference writeBoundary)
        {
            _log = log;
            Generation = generation;
            WriteBoundary = writeBoundary;
        }

        /// <summary>The first generation kept by this pin.</summary>
        public uint Generation { get; }

        /// <summary>The inclusive allocation frontier captured when the pin was first acquired.</summary>
        /// <remarks>Every record reserved after the pin compares greater than or equal to it.</remarks>
        public PbtGroupReference WriteBoundary { get; }

        /// <summary>Keeps the same generation range independently of this pin.</summary>
        public GenerationPin Copy() => _log.PinGeneration(Generation, WriteBoundary);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_log._lock)
            {
                int count = _log._pins[Generation];
                if (count == 1) _log._pins.Remove(Generation);
                else _log._pins[Generation] = count - 1;
                _log.Trim();
            }
        }
    }
}
