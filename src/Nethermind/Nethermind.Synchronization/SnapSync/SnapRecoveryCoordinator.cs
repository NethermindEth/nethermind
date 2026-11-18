// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync;

internal sealed class SnapRecoveryCoordinator(TimeProvider timeProvider, FastSync.IStateSyncPivot? pivot = null)
{
    private const int SendsPerWindow = 4;
    private const int InactiveIdentities = 4096;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private readonly Dictionary<PublicKey, Identity> _identities = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private int _cohortSlots;
    private readonly long _origin = timeProvider.GetTimestamp();

    internal TimeProvider TimeProvider { get; } = timeProvider;
    internal bool TracksPivot => pivot is not null;
    internal Hash256? CurrentRoot => pivot?.GetPivotHeader()?.StateRoot;
    internal ISyncPeerPool? PeerPool { get; set; }
    internal bool CanPrepare(bool rooted, bool unknown, Cohort? owned = null)
    {
        if (PeerPool is null)
        {
            lock (_lock) return !unknown || owned?.Remaining > 0 || _cohortSlots < SnapProvider.MaxQueuedEmptyStreakRefreshes;
        }
        lock (_lock) EvictInactive();
        foreach (PeerInfo peer in PeerPool.InitializedPeers)
            if (peer.CanBeAllocated(AllocationContexts.Snap) && CanDispatch(peer, rooted, unknown, owned)) return true;
        return false;
    }
    internal int RetainedIdentities { get { lock (_lock) return _identities.Count; } }
    internal int CohortSlots { get { lock (_lock) return _cohortSlots; } }
    internal int PinnedIdentities
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                foreach (Identity identity in _identities.Values) if (identity.Outstanding > 0) count++;
                return count;
            }
        }
    }

    internal sealed class Identity(long generation)
    {
        internal long Generation = generation;
        internal int Unknown;
        internal int Retired;
        internal int Outstanding;
        internal long LastUsed;
    }

    private long Generation => TimeProvider.GetElapsedTime(_origin).Ticks / Window.Ticks;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool CanDispatch(PeerInfo peer, bool rooted, bool unknown, Cohort? owned = null)
    {
        if (!rooted) return true;
        lock (_lock)
        {
            if (unknown && !(owned?.Remaining > 0) && _cohortSlots >= SnapProvider.MaxQueuedEmptyStreakRefreshes) return false;
            if (!_identities.TryGetValue(peer.SyncPeer.Node.Id, out Identity identity)) return true;
            bool current = identity.Generation == Generation;
            return identity.Outstanding < SendsPerWindow && (!current || identity.Retired < SendsPerWindow && (!unknown || identity.Unknown < SendsPerWindow));
        }
    }

    internal Lease? TryReserve(PeerInfo peer, bool unknown, int candidates, Cohort? owned = null)
    {
        lock (_lock)
        {
            if (!CanDispatch(peer, true, unknown, owned)) return null;
            Cohort? cohort = unknown ? owned ?? TryReserveCohort(candidates) : null;
            if (unknown && cohort is null) return null;
            PublicKey key = peer.SyncPeer.Node.Id;
            long generation = Generation;
            if (!_identities.TryGetValue(key, out Identity identity))
            {
                EvictInactive();
                _identities.Add(key, identity = new(generation));
            }
            if (identity.Generation != generation)
            {
                identity.Generation = generation;
                identity.Unknown = identity.Retired = 0;
            }
            identity.Retired++;
            if (unknown) identity.Unknown++;
            identity.Outstanding++;
            identity.LastUsed = ++_sequence;
            return new(this, identity, generation, unknown, cohort);
        }
    }

    internal Cohort? TryReserveCohort(int candidates)
    {
        lock (_lock)
        {
            int slots = Math.Min(SnapProvider.MaxQueuedEmptyStreakRefreshes, candidates);
            if (slots <= 0 || slots > SnapProvider.MaxQueuedEmptyStreakRefreshes - _cohortSlots) return null;
            _cohortSlots += slots;
            return new(this, slots);
        }
    }

    internal sealed class Cohort(SnapRecoveryCoordinator owner, int slots)
    {
        internal int Remaining = slots;
        internal void Trim(int candidates)
        {
            lock (owner._lock)
            {
                int keep = Math.Min(Remaining, candidates);
                if (keep < Remaining) owner.Signal();
                owner._cohortSlots -= Remaining - keep;
                Remaining = keep;
            }
        }
        internal bool Take()
        {
            lock (owner._lock)
            {
                if (Remaining == 0) return false;
                Remaining--;
                return true;
            }
        }
        internal void Complete()
        {
            lock (owner._lock)
            {
                if (Remaining == 0) return;
                owner._cohortSlots -= Remaining;
                Remaining = 0;
                owner.Signal();
            }
        }
    }

    private void EvictInactive()
    {
        if (_identities.Count <= InactiveIdentities) return;
        HashSet<PublicKey> live = [];
        if (PeerPool is not null)
            foreach (PeerInfo peer in PeerPool.InitializedPeers) live.Add(peer.SyncPeer.Node.Id);
        int inactive = 0;
        PublicKey? victim = null;
        Identity? oldest = null;
        foreach (KeyValuePair<PublicKey, Identity> entry in _identities)
        {
            if (entry.Value.Outstanding != 0 || live.Contains(entry.Key)) continue;
            inactive++;
            if (oldest is null || entry.Value.Generation < oldest.Generation || entry.Value.Generation == oldest.Generation && entry.Value.LastUsed < oldest.LastUsed)
                (victim, oldest) = (entry.Key, entry.Value);
        }
        if (inactive > InactiveIdentities && victim is not null) _identities.Remove(victim);
    }

    internal void ReleaseCohortSlot()
    {
        lock (_lock)
        {
            _cohortSlots--;
            Signal();
        }
    }

    internal bool TryTakeCohortSlot()
    {
        lock (_lock)
        {
            if (_cohortSlots >= SnapProvider.MaxQueuedEmptyStreakRefreshes) return false;
            _cohortSlots++;
            return true;
        }
    }

    private void Signal()
    {
        TaskCompletionSource changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    internal Task ReadinessSignal { get { lock (_lock) return _changed.Task; } }

    internal void NotifyWork()
    {
        lock (_lock) Signal();
    }

    internal async Task WaitForReadiness(Task changed, bool rootWork, CancellationToken token)
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task tick = Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider, timer.Token);
        TimeSpan? nextWindow = null;
        if (rootWork)
        {
            lock (_lock)
                foreach (Identity identity in _identities.Values)
                    if (identity.Generation == Generation && (identity.Retired >= SendsPerWindow || identity.Unknown >= SendsPerWindow))
                    {
                        nextWindow = TimeSpan.FromTicks(Window.Ticks - TimeProvider.GetElapsedTime(_origin).Ticks % Window.Ticks);
                        break;
                    }
        }
        if (nextWindow is { } delay)
            await Task.WhenAny(changed, tick, Task.Delay(delay, TimeProvider, timer.Token));
        else await Task.WhenAny(changed, tick);
        await timer.CancelAsync();
        token.ThrowIfCancellationRequested();
    }

    internal sealed class Lease
    {
        private readonly SnapRecoveryCoordinator _owner;
        private readonly Identity _identity;
        private readonly long _generation;
        private readonly bool _unknown;
        private readonly Cohort? _cohort;
        private bool _sent;
        private bool _classified;
        private bool _ambiguous;
        private bool _retired;
        private int _completed;

        internal Lease(SnapRecoveryCoordinator owner, Identity identity, long generation, bool unknown, Cohort? cohort)
            => (_owner, _identity, _generation, _unknown, _cohort) = (owner, identity, generation, unknown, cohort);

        internal bool Sent()
        {
            lock (_owner._lock)
            {
                if (_completed != 0) return false;
                _sent = true;
                return true;
            }
        }
        internal void Classify(bool ambiguous, bool retired)
        {
            lock (_owner._lock)
            {
                if (_completed != 0) return;
                (_classified, _ambiguous, _retired) = (true, ambiguous, retired);
            }
        }
        internal bool TakeCohortSlot()
        {
            lock (_owner._lock)
            {
                return _completed == 0 && _cohort?.Take() == true;
            }
        }
        internal void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            lock (_owner._lock)
            {
                if (_identity.Generation == _generation)
                {
                    if (!_sent || _classified && !_retired) _identity.Retired--;
                    if (_unknown && (!_sent || _classified && !_ambiguous)) _identity.Unknown--;
                }
                _identity.Outstanding--;
                _cohort?.Complete();
                _identity.LastUsed = ++_owner._sequence;
                _owner.EvictInactive();
                _owner.Signal();
            }
        }
    }
}
