// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core.Timers;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    internal sealed class NodeStatsCapacityException() : InvalidOperationException("Node statistics capacity is exhausted.");

    /// <summary>Retains bounded node statistics, preserving entries while their owners hold leases.</summary>
    /// <remarks>Compatibility protection expires after fifteen minutes. Pin growth reserves one sixteenth of capacity for admission. Displaced bans retain at most one cache-capacity of identities in FIFO history.</remarks>
    public class NodeStatsManager : INodeStatsManager, IDisposable
    {
        private sealed class NodeComparer : IEqualityComparer<Node>
        {
            public bool Equals(Node? x, Node? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.Id == y.Id);
            public int GetHashCode(Node obj) => obj.GetHashCode();
        }

        private sealed class Entry(Node node, TimeProvider timeProvider, long ordinal)
        {
            internal readonly Node Node = node;
            internal readonly NodeStatsLight Stats = new(node, timeProvider: timeProvider);
            internal readonly long Ordinal = ordinal;
            internal LinkedListNode<Entry>? Unpinned;
            internal DateTime CompatUntil;
            internal DateTime BanUntil;
            internal DateTime NextExpiry;
            internal bool ProtectionSuppressed;
            internal bool PinnedFromBan;
            internal int RecentlyUsed;
            internal int Pins;
            internal int OrdinaryPins;
        }

        private sealed class ExpiryComparer : IComparer<Entry>
        {
            internal static readonly ExpiryComparer Instance = new();
            public int Compare(Entry? x, Entry? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                int result = x!.NextExpiry.CompareTo(y!.NextExpiry);
                return result != 0 ? result : x.Ordinal.CompareTo(y.Ordinal);
            }
        }

        private enum AdmissionPurpose { Plain, Compatibility, Ban }

        private sealed class Lease(NodeStatsManager manager, Entry entry, bool priority) : IDisposable
        {
            private NodeStatsManager? _manager = manager;
            internal bool Priority = priority;
            internal readonly Entry Entry = entry;
            internal NodeStatsManager? Manager => Volatile.Read(ref _manager);
            public void Dispose() => Interlocked.Exchange(ref _manager, null)?.Release(this);
        }

        private const int MaximumReservedPins = 1024;
        private readonly Lock _lock = new();
        private readonly ConcurrentDictionary<Node, Entry> _nodeStats = new(new NodeComparer());
        private readonly LinkedList<Entry> _free = new();
        private readonly LinkedList<Entry> _compatibility = new();
        private readonly LinkedList<Entry> _bans = new();
        private readonly SortedSet<Entry> _expiry = [with(ExpiryComparer.Instance)];
        private sealed record BanHistory(PublicKeyAsKey Identity, NodeStatsLight.InboundBan Evidence);
        private readonly Dictionary<PublicKeyAsKey, LinkedListNode<BanHistory>> _banHistory = [];
        private readonly LinkedList<BanHistory> _banHistoryOrder = new();
        private readonly TimeProvider _timeProvider;
        private DateTime _nextExpiry = DateTime.MaxValue;
        private long _nextOrdinal;
        private int _count;
        private int _pinnedEntries;
        private static readonly TimeSpan CompatibilityRetention = TimeSpan.FromMinutes(15);
        private readonly int _maxCount;
        private readonly int _ordinaryPinLimit;
        private int _ordinaryPinnedEntries;

        internal int ProtectedCount { get { lock (_lock) return _compatibility.Count + _bans.Count; } }

        /// <summary>Creates a bounded node statistics cache.</summary>
        /// <remarks>The timer and log arguments remain for constructor compatibility; admission enforces the bound without a cleanup timer.</remarks>
        public NodeStatsManager(ITimerFactory timerFactory, ILogManager logManager, int maxCount = 10000)
            : this(timerFactory, logManager, maxCount, TimeProvider.System) { }

        internal NodeStatsManager(ITimerFactory timerFactory, ILogManager logManager, int maxCount, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(logManager);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
            _maxCount = maxCount;
            _timeProvider = timeProvider;
            _ordinaryPinLimit = maxCount - Math.Min(MaximumReservedPins, maxCount / 4);
        }

        public INodeStats GetOrAdd(Node node) => TryGetOrAdd(node, out INodeStats? stats)
            ? stats : ThrowCapacityExhausted();

        /// <inheritdoc/>
        public INodeStats? GetStatsOrNull(Node node) => TryGetOrAdd(node, out INodeStats? stats) ? stats : null;

        internal bool TryGetOrAdd(Node node, [NotNullWhen(true)] out INodeStats? stats)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (_nodeStats.TryGetValue(node, out Entry? existing))
            {
                // Ordinary samples may reach an evicted entry; rejection reports recheck ownership under the lock.
                if (Volatile.Read(ref existing.RecentlyUsed) == 0) Volatile.Write(ref existing.RecentlyUsed, 1);
                stats = existing.Stats;
                return true;
            }
            lock (_lock)
            {
                Entry? entry = GetOrAddEntry(node);
                stats = entry?.Stats;
                return entry is not null;
            }
        }

        /// <inheritdoc/>
        public bool TryAcquire(Node node, bool priority, [NotNullWhen(true)] out INodeStats? stats, out IDisposable? lease)
        {
            ArgumentNullException.ThrowIfNull(node);
            lock (_lock)
            {
                stats = null;
                lease = null;
                ExpireProtection(_timeProvider.GetUtcNow().UtcDateTime);
                _nodeStats.TryGetValue(node, out Entry? entry);
                if (!priority && (entry is null || entry.OrdinaryPins == 0) && _ordinaryPinnedEntries >= _ordinaryPinLimit) return false;
                entry ??= GetOrAddEntry(node);
                if (entry is null) return false;
                if (entry.Pins == 0)
                {
                    entry.PinnedFromBan = entry.Unpinned!.List == _bans;
                    entry.Unpinned!.List!.Remove(entry.Unpinned);
                    entry.Unpinned = null;
                    _pinnedEntries++;
                    EnforceProtectedLimit(AvailableCapacity < FreeCapacityFloor);
                }
                if (!priority && entry.OrdinaryPins++ == 0) _ordinaryPinnedEntries++;
                entry.Pins++;
                stats = entry.Stats;
                lease = new Lease(this, entry, priority);
                return true;
            }
        }

        private Entry? GetOrAddEntry(Node node, AdmissionPurpose purpose = AdmissionPurpose.Plain)
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            ExpireProtection(now);
            if (_nodeStats.TryGetValue(node, out Entry? entry))
            {
                if (entry.Unpinned is { } link && link.List == _free)
                {
                    LinkedList<Entry> list = link.List!;
                    list.Remove(link);
                    list.AddLast(link);
                }
                return entry;
            }
            if (_count == _maxCount)
            {
                LinkedListNode<Entry>? oldest = null;
                int remaining = _free.Count * 2;
                while (oldest is null && remaining-- > 0 && _free.First is { } candidate)
                {
                    Entry value = candidate.Value;
                    if (Interlocked.Exchange(ref value.RecentlyUsed, 0) == 0)
                    {
                        oldest = candidate;
                        break;
                    }
                    _free.RemoveFirst();
                    _free.AddLast(candidate);
                }
                oldest ??= _free.First;
                if (oldest is null && purpose is AdmissionPurpose.Ban or AdmissionPurpose.Plain) oldest = _compatibility.First ?? _bans.First;
                if (oldest is null && purpose == AdmissionPurpose.Compatibility) oldest = _compatibility.First;
                if (oldest is null) return null;
                if (oldest.List == _bans) RememberBan(oldest.Value);
                oldest.List!.Remove(oldest);
                _expiry.Remove(oldest.Value);
                _nodeStats.TryRemove(oldest.Value.Node, out _);
                _count--;
                UpdateNextExpiry();
            }
            entry = new Entry(node, _timeProvider, checked(++_nextOrdinal));
            if (FindBanHistory(node.Id, now) is { } evidence)
            {
                entry.Stats.RestoreInboundBan(evidence);
                entry.BanUntil = evidence.Until;
                entry.ProtectionSuppressed = true;
                IndexExpiry(entry);
            }
            entry.Unpinned = _free.AddLast(entry);
            _nodeStats.TryAdd(node, entry);
            _count++;
            return entry;
        }

        private int ProtectedLimit => Math.Max(1, (_maxCount - _pinnedEntries) / 2);
        private int FreeCapacityFloor => Math.Max(1, _maxCount / 16);
        private int AvailableCapacity => _maxCount - _count + _free.Count;

        private void Classify(Entry entry, DateTime now)
        {
            if (entry.Pins != 0) return;
            if (entry.Unpinned is { } old) old.List!.Remove(old);
            LinkedList<Entry> list = entry.ProtectionSuppressed ? _free
                : entry.BanUntil > now ? _bans : entry.CompatUntil > now ? _compatibility : _free;
            entry.Unpinned = list.AddLast(entry);
        }

        private void EnforceProtectedLimit(bool banPressure)
        {
            while (_compatibility.Count + _bans.Count > ProtectedLimit ||
                (banPressure && _pinnedEntries > 0 && AvailableCapacity < Math.Min(FreeCapacityFloor, _maxCount - _pinnedEntries)))
            {
                LinkedListNode<Entry>? candidate = _compatibility.First ?? (banPressure ? _bans.First : null);
                if (candidate is null) break;
                if (candidate.List == _bans) RememberBan(candidate.Value);
                candidate.List!.Remove(candidate);
                candidate.Value.ProtectionSuppressed = true;
                _free.AddLast(candidate);
            }
        }

        private void RememberBan(Entry entry)
        {
            if (entry.Stats.BanEvidence is { } evidence) RememberBan(entry.Node.Id, evidence);
        }

        private void RememberBan(PublicKeyAsKey identity, NodeStatsLight.InboundBan evidence)
        {
            if (evidence.Until <= _timeProvider.GetUtcNow().UtcDateTime) return;
            if (_banHistory.TryGetValue(identity, out LinkedListNode<BanHistory>? existing))
            {
                if (existing.Value.Evidence.Until < evidence.Until) existing.Value = new BanHistory(identity, evidence);
                return;
            }
            if (_banHistory.Count == _maxCount)
            {
                _banHistory.Remove(_banHistoryOrder.First!.Value.Identity);
                _banHistoryOrder.RemoveFirst();
            }
            _banHistory[identity] = _banHistoryOrder.AddLast(new BanHistory(identity, evidence));
        }

        private NodeStatsLight.InboundBan? FindBanHistory(PublicKeyAsKey identity, DateTime now)
        {
            if (!_banHistory.TryGetValue(identity, out LinkedListNode<BanHistory>? record)) return null;
            if (record.Value.Evidence.Until > now) return record.Value.Evidence;
            _banHistory.Remove(identity);
            _banHistoryOrder.Remove(record);
            return null;
        }

        /// <inheritdoc/>
        public bool IsInboundBanned(Node node, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(node);
            lock (_lock)
            {
                return (_nodeStats.TryGetValue(node, out Entry? entry) && entry.Stats.IsInboundBanned(nowUtc)) ||
                    FindBanHistory(node.Id, nowUtc) is not null;
            }
        }

        private void UpdateNextExpiry() => _nextExpiry = _expiry.Min?.NextExpiry ?? DateTime.MaxValue;

        private void IndexExpiry(Entry entry)
        {
            entry.NextExpiry = entry.CompatUntil == default ? entry.BanUntil
                : entry.BanUntil == default ? entry.CompatUntil : entry.CompatUntil < entry.BanUntil ? entry.CompatUntil : entry.BanUntil;
            if (entry.NextExpiry != default) _expiry.Add(entry);
            UpdateNextExpiry();
        }

        private void ExpireProtection(DateTime now)
        {
            if (now < _nextExpiry) return;
            while (_expiry.Min is { } entry && entry.NextExpiry <= now)
            {
                _expiry.Remove(entry);
                if (entry.CompatUntil <= now) entry.CompatUntil = default;
                if (entry.BanUntil <= now)
                {
                    entry.BanUntil = default;
                    entry.PinnedFromBan = false;
                }
                Classify(entry, now);
                IndexExpiry(entry);
            }
            UpdateNextExpiry();
        }

        /// <inheritdoc/>
        public void PromoteLease(IDisposable? lease)
        {
            if (lease is not Lease owned || owned.Manager is not { } manager || !ReferenceEquals(manager, this)) return;
            lock (manager._lock)
            {
                if (owned.Manager != manager || owned.Priority) return;
                owned.Priority = true;
                if (--owned.Entry.OrdinaryPins == 0) manager._ordinaryPinnedEntries--;
            }
        }

        private void Release(Lease lease)
        {
            lock (_lock)
            {
                Entry entry = lease.Entry;
                if (!lease.Priority && --entry.OrdinaryPins == 0) _ordinaryPinnedEntries--;
                if (--entry.Pins == 0)
                {
                    _pinnedEntries--;
                    DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
                    ExpireProtection(now);
                    Classify(entry, now);
                    EnforceProtectedLimit(entry.Unpinned?.List == _bans && !entry.PinnedFromBan);
                }
            }
        }

        public void ReportHandshakeEvent(Node node, ConnectionDirection direction)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            stats.AddNodeStatsHandshakeEvent(direction);
        }

        public void ReportSyncEvent(Node node, NodeStatsEventType nodeStatsEvent)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            stats.AddNodeStatsSyncEvent(nodeStatsEvent);
        }

        public void ReportEvent(Node node, NodeStatsEventType eventType)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            stats.AddNodeStatsEvent(eventType);
        }

        public (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(Node node)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return (true, null);
            return stats.IsConnectionDelayed(_timeProvider.GetUtcNow().UtcDateTime);
        }

        public CompatibilityValidationType? FindCompatibilityValidationResult(Node node)
            => TryGetOrAdd(node, out INodeStats? stats) ? stats.FailedCompatibilityValidation : CompatibilityValidationType.StatsUnavailable;

        public long GetCurrentReputation(Node node)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return 0;
            return stats.CurrentNodeReputation(_timeProvider.GetUtcNow().UtcDateTime);
        }

        public void ReportP2PInitializationEvent(Node node, P2PNodeDetails p2PNodeDetails)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            stats.AddNodeStatsP2PInitializedEvent(p2PNodeDetails);
        }

        public void ReportSyncPeerInitializeEvent(string protocol, Node node, SyncPeerNodeDetails syncPeerNodeDetails)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            if (protocol == "eth")
                stats.AddNodeStatsEth62InitializedEvent(syncPeerNodeDetails);
            else if (protocol == "les")
                stats.AddNodeStatsLesInitializedEvent(syncPeerNodeDetails);
            else
                throw new ArgumentException($"Unknown protocol: {protocol}");
        }

        public void ReportFailedValidation(Node node, CompatibilityValidationType validationType)
        {
            ArgumentNullException.ThrowIfNull(node);
            lock (_lock)
            {
                if (GetOrAddEntry(node, AdmissionPurpose.Compatibility) is { } entry)
                {
                    _expiry.Remove(entry);
                    entry.Stats.FailedCompatibilityValidation = validationType;
                    DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
                    entry.CompatUntil = now + CompatibilityRetention;
                    if (entry.BanUntil <= now) entry.ProtectionSuppressed = false;
                    IndexExpiry(entry);
                    Classify(entry, now);
                    EnforceProtectedLimit(false);
                }
            }
        }

        public void ReportDisconnect(Node node, DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            ArgumentNullException.ThrowIfNull(node);
            lock (_lock)
            {
                bool ban = NodeStatsLight.TryGetInboundBanDelay(disconnectType, disconnectReason, out TimeSpan banDelay);
                Entry? entry = GetOrAddEntry(node, ban ? AdmissionPurpose.Ban : AdmissionPurpose.Plain);
                if (entry is not null)
                {
                    entry.Stats.AddNodeStatsDisconnectEvent(disconnectType, disconnectReason);
                    if (ban || disconnectType == DisconnectType.Local)
                    {
                        _expiry.Remove(entry);
                        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
                        entry.BanUntil = entry.Stats.InboundBanUntil;
                        if (ban && _banHistory.ContainsKey(node.Id) && entry.Stats.BanEvidence is { } evidence)
                            RememberBan(node.Id, evidence);
                        if (entry.BanUntil <= now) entry.PinnedFromBan = false;
                        if (ban) entry.ProtectionSuppressed = false;
                        IndexExpiry(entry);
                        Classify(entry, now);
                        EnforceProtectedLimit(ban && entry.Pins == 0);
                    }
                }
                else if (ban)
                {
                    DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
                    RememberBan(node.Id, new NodeStatsLight.InboundBan(disconnectReason, now, now + banDelay));
                }
            }
        }

        public long GetNewPersistedReputation(Node node)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return 0;
            return stats.NewPersistedNodeReputation(_timeProvider.GetUtcNow().UtcDateTime);
        }

        public long GetCurrentPersistedReputation(Node node)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return 0;
            return stats.CurrentPersistedNodeReputation;
        }

        public bool HasFailedValidation(Node node) => FindCompatibilityValidationResult(node).HasValue;

        public void ReportTransferSpeedEvent(Node node, TransferSpeedType type, long value)
        {
            if (!TryGetOrAdd(node, out INodeStats? stats)) return;
            stats.AddTransferSpeedCaptureEvent(type, value);
        }

        /// <summary>Preserves the disposable contract for existing consumers.</summary>
        public void Dispose() { }

        [DoesNotReturn, StackTraceHidden]
        private static INodeStats ThrowCapacityExhausted() =>
            throw new NodeStatsCapacityException();
    }
}
