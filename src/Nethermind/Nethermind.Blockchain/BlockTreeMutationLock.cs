// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Blockchain;

/// <summary>Coordinates canonical-chain changes and debug maintenance within a node.</summary>
public sealed class BlockTreeMutationLock
{
    private static readonly TimeSpan MutationWaitInterval = TimeSpan.FromSeconds(1);
    private readonly Lock _lock = new();
    private int _maintenanceVersion;

    /// <summary>Acquires the mutation lock; dispose the returned scope on the acquiring thread.</summary>
    public Scope Enter()
    {
        _lock.Enter();
        return new Scope(this, false);
    }

    /// <summary>Enters a chain mutation, refusing overlap with debug maintenance.</summary>
    /// <remarks>Maintenance is non-reentrant and waits at most one second for an ordinary mutation. Overlapping maintenance is refused. Ordinary mutations serialize with each other and recheck maintenance once per second while waiting.</remarks>
    public bool TryEnter(out Scope scope, bool maintenance = false) => TryEnter(out scope, maintenance, MutationWaitInterval);

    internal bool TryEnter(out Scope scope, bool maintenance, TimeSpan waitInterval)
    {
        scope = default;
        if (maintenance)
        {
            int version = Volatile.Read(ref _maintenanceVersion);
            if (_lock.IsHeldByCurrentThread || (version & 1) != 0 || !_lock.TryEnter(waitInterval)) return false;
            if (version != Volatile.Read(ref _maintenanceVersion))
            {
                _lock.Exit();
                return false;
            }
            Interlocked.Increment(ref _maintenanceVersion);
        }
        else if (!_lock.IsHeldByCurrentThread)
        {
            int version = Volatile.Read(ref _maintenanceVersion);
            if ((version & 1) != 0) return false;
            while (!_lock.TryEnter(waitInterval))
            {
                if (version != Volatile.Read(ref _maintenanceVersion)) return false;
            }
            // A caller queued before maintenance must not apply its stale canonical-chain decision afterward.
            if (version != Volatile.Read(ref _maintenanceVersion))
            {
                _lock.Exit();
                return false;
            }
        }
        else
        {
            _lock.Enter();
        }
        scope = new Scope(this, maintenance);
        return true;
    }

    /// <summary>Owns one acquisition of the mutation lock.</summary>
    /// <remarks>Dispose exactly once, on the acquiring thread.</remarks>
    public readonly struct Scope : IDisposable
    {
        private readonly BlockTreeMutationLock? _owner;
        private readonly bool _maintenance;
        internal Scope(BlockTreeMutationLock owner, bool maintenance) => (_owner, _maintenance) = (owner, maintenance);
        /// <inheritdoc/>
        public void Dispose()
        {
            if (_owner is null) return;
            if (_maintenance) Interlocked.Increment(ref _owner._maintenanceVersion);
            _owner._lock.Exit();
        }
    }
}
