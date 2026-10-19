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

    /// <summary>Keeps the active generation and every later generation readable until the pin is disposed.</summary>
    public GenerationPin PinActiveGeneration()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return PinGeneration(_active?.Id ?? checked(_lastGeneration + 1));
        }
    }

    private GenerationPin PinGeneration(uint generation)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _pins.TryGetValue(generation, out int count);
            _pins[generation] = count + 1;
            return new GenerationPin(this, generation);
        }
    }

    /// <summary>Stops new operations and writes, releasing buffers after outstanding operations and pins finish.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            Trim();
        }
    }

    private uint OldestPinnedGeneration
    {
        get
        {
            foreach ((uint generation, _) in _pins) return generation;
            return uint.MaxValue;
        }
    }

    private bool KeepOpportunistically(Generation generation) =>
        !_disposed && _lastGeneration - generation.Id < (uint)_retainedGenerations;

    private void Trim()
    {
        if (_operations != 0) return;
        uint oldestPinned = OldestPinnedGeneration;
        foreach ((uint id, Generation generation) in _generations)
        {
            if (id >= oldestPinned || KeepOpportunistically(generation)) continue;
            _generations.TryRemove(id, out _);
            if (ReferenceEquals(_active, generation)) Volatile.Write(ref _active, null);
            Interlocked.Add(ref _residentBytes, -generation.Buffer.Capacity);
            ((IDisposable)generation.Buffer).Dispose();
        }
    }

    /// <summary>An operation-wide lease shared by workers without taking a lock per group read.</summary>
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

    /// <summary>Keeps one generation and every later generation available to snapshot readers.</summary>
    public sealed class GenerationPin : IDisposable
    {
        private readonly PbtGroupLog _log;
        private int _disposed;

        internal GenerationPin(PbtGroupLog log, uint generation)
        {
            _log = log;
            Generation = generation;
        }

        /// <summary>The first generation kept by this pin.</summary>
        public uint Generation { get; }

        /// <summary>Keeps the same generation range independently of this pin.</summary>
        public GenerationPin Copy() => _log.PinGeneration(Generation);

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
