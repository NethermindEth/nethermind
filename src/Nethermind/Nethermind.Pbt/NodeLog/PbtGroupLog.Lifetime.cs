// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Pbt.NodeLog;

public sealed partial class PbtGroupLog
{
    /// <summary>Protects borrowed records and reservations for an entire operation, including all of its workers.</summary>
    public OperationLease BeginOperation()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _operations++;
            return new OperationLease(this);
        }
    }

    /// <summary>Creates a set of authoritative record owners, normally one per snapshot content.</summary>
    public RecordOwner CreateOwner()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return new RecordOwner(this);
        }
    }

    /// <summary>Stops new operations and reservations, releasing buffers after outstanding operations and owners finish.</summary>
    /// <remarks>Does not wait for leases, which may be released by the disposing thread. Repeated calls have no effect.</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            Trim();
        }
    }

    private bool KeepOpportunistically(Generation generation) =>
        !_disposed && _lastGeneration - generation.Id < (uint)_retainedGenerations;

    private void Trim()
    {
        if (_operations != 0) return;
        foreach ((uint id, Generation generation) in _generations)
        {
            if (generation.Owners != 0 || KeepOpportunistically(generation)) continue;
            _generations.TryRemove(id, out _);
            if (ReferenceEquals(_active, generation)) Volatile.Write(ref _active, null);
            Interlocked.Add(ref _residentBytes, -generation.Buffer.Capacity);
            ((IDisposable)generation.Buffer).Dispose();
        }
    }

    /// <summary>An operation-wide lease that can be shared by parallel workers without taking a lock per group read.</summary>
    /// <remarks>The owner must join all workers and stop using borrowed records before disposing this lease.</remarks>
    public sealed class OperationLease : IDisposable
    {
        private readonly PbtGroupLog _log;
        private int _disposed;

        internal OperationLease(PbtGroupLog log) => _log = log;

        internal bool IsActive => Volatile.Read(ref _disposed) == 0;

        internal void EnsureActive() => ObjectDisposedException.ThrowIf(!IsActive, this);

        internal void EnsureActive(PbtGroupLog log)
        {
            EnsureActive();
            if (!ReferenceEquals(_log, log)) throw new ArgumentException("The operation belongs to a different group log.", nameof(log));
        }

        /// <summary>Releases operation protection once all workers and borrowed views are finished.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_log._lock)
            {
                _log._operations--;
                _log.Trim();
            }
        }
    }

    /// <summary>Keeps only the generations containing authoritative records, with balanced retention per held reference.</summary>
    /// <remarks>
    /// Retain a replacement before releasing the record it replaces. Tombstones need no storage. Dispose releases every
    /// generation held by the owner; retained-generation counts are safe to update from several partition writers.
    /// </remarks>
    public sealed class RecordOwner : IDisposable
    {
        private readonly PbtGroupLog _log;
        private readonly Dictionary<uint, int> _counts = [];
        private bool _disposed;

        internal RecordOwner(PbtGroupLog log) => _log = log;

        /// <summary>Retains the generation of one authoritative record, or does nothing for a tombstone.</summary>
        /// <exception cref="InvalidOperationException">The reference is unresolved or retired.</exception>
        public void Retain(PbtGroupReference reference)
        {
            lock (_log._lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _log.ThrowIfDisposed();
                if (reference.IsNone) return;
                if (!_log.TryLocate(reference, out _)) throw new InvalidOperationException("An authoritative group reference must resolve to a record.");
                uint id = reference.Generation;
                if (_counts.TryGetValue(id, out int count)) _counts[id] = count + 1;
                else
                {
                    _counts.Add(id, 1);
                    _log._generations[id].Owners++;
                }
            }
        }

        /// <summary>Releases one held reference; the generation can retire after its last owner and operation finish.</summary>
        public void Release(PbtGroupReference reference)
        {
            lock (_log._lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (reference.IsNone) return;
                uint id = reference.Generation;
                if (!_counts.TryGetValue(id, out int count)) throw new InvalidOperationException("The group generation is not held by this owner.");
                if (count > 1) _counts[id] = count - 1;
                else
                {
                    _counts.Remove(id);
                    _log._generations[id].Owners--;
                    _log.Trim();
                }
            }
        }

        /// <summary>Releases all authoritative generation leases owned by this set.</summary>
        public void Dispose()
        {
            lock (_log._lock)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (uint id in _counts.Keys) _log._generations[id].Owners--;
                _counts.Clear();
                _log.Trim();
            }
        }
    }
}
