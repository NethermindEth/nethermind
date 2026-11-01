// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>Opened records shared by every historical state, keyed by the parent state they describe, within a byte
/// budget. A record is read and validated once however many readers ask for it at the same time.</summary>
/// <remarks>
/// The cache holds one lease on each record it keeps; a reader takes its own. Eviction drops only the cache's lease,
/// so a record in use stays valid until its last reader ends. A missing or unusable record is remembered for
/// <see cref="NegativeLifetime"/>, or until the recorder writes that state's record and invalidates it. A record's
/// pooled buffer is rounded up to a power of two, and the budget charges the rounded size. A lookup never throws: a
/// failing store answers "no record", and the reader keeps reading history.
/// </remarks>
public sealed class PrestateRecordCache(PrestateRecordStore store, IFlatDbConfig config, ILogManager logManager)
{
    internal static readonly TimeSpan NegativeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>A remembered miss: the entry object, its dictionary slot and its recency node.</summary>
    internal const long NegativeEntryBytes = 256;

    private readonly long _budget = Math.Max(0, config.HistoryPrestateRecordsCacheBytes);
    private readonly ILogger _logger = logManager.GetClassLogger<PrestateRecordCache>();
    private readonly object _sync = new();
    private readonly Dictionary<StateId, Entry> _entries = [];
    private readonly LinkedList<StateId> _recency = new();
    private long _bytes;
    private int _disabled;

    /// <summary>History reads a state makes before it opens a record that is not cached.</summary>
    public int LazyOpenReads { get; } = Math.Max(0, config.HistoryPrestateRecordsLazyOpenReads);

    /// <summary>Share of record reads also read from history and compared.</summary>
    public int VerifyPercent { get; } = Math.Clamp(config.HistoryPrestateRecordsVerifyPercent, 0, 100);

    /// <summary>False after a verification mismatch, until restart.</summary>
    public bool IsEnabled => Volatile.Read(ref _disabled) == 0;

    internal long Bytes
    {
        get
        {
            lock (_sync) return _bytes;
        }
    }

    /// <summary>A lease on the cached record of <paramref name="parent"/>, without any store read. False below the
    /// store floor, as <see cref="TryLease"/> is.</summary>
    public bool TryLeaseCached(in StateId parent, out PrestateRecord? record)
    {
        record = null;
        if (!IsEnabled || parent.BlockNumber < store.Floor) return false;

        lock (_sync)
        {
            if (!_entries.TryGetValue(parent, out Entry? entry) || entry.Record is null) return false;
            record = Lease(entry);
        }

        Metrics.RecordPrestateRecordLeaseHit();
        return true;
    }

    /// <summary>A lease on the record of <paramref name="parent"/>, read from the store when not cached. False when
    /// there is none, it is unusable or below the floor, or the store failed.</summary>
    public bool TryLease(in StateId parent, out PrestateRecord? record)
    {
        record = null;
        if (!IsEnabled || parent.BlockNumber < store.Floor) return false;

        Entry? loading = null;
        lock (_sync)
        {
            while (loading is null)
            {
                if (!_entries.TryGetValue(parent, out Entry? entry))
                {
                    loading = new Entry(parent);
                    _entries.Add(parent, loading);
                    break;
                }

                if (entry.Record is not null)
                {
                    record = Lease(entry);
                    break;
                }

                if (entry.Loading)
                {
                    Monitor.Wait(_sync);
                    continue;
                }

                if (Stopwatch.GetElapsedTime(entry.CreatedAt) < NegativeLifetime) return false;

                Remove(entry);
            }
        }

        if (record is not null)
        {
            Metrics.RecordPrestateRecordLeaseHit();
            return true;
        }

        PrestateRecord? opened = Load(parent);
        lock (_sync)
        {
            loading!.Loading = false;
            if (loading.Invalidated)
            {
                _entries.Remove(parent);
            }
            else
            {
                loading.Record = opened;
                loading.Node = _recency.AddFirst(parent);
                Charge(opened?.ByteSize ?? NegativeEntryBytes);
            }

            record = opened is null ? null : loading.Invalidated ? opened : Lease(loading);
            Evict();
            Monitor.PulseAll(_sync);
        }

        if (record is null) return false;
        Metrics.RecordPrestateRecordLeaseHit();
        return true;
    }

    /// <summary>Forgets what is cached for <paramref name="parent"/>, so the record just written there is read on the
    /// next lookup instead of a remembered miss.</summary>
    public void Invalidate(in StateId parent)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(parent, out Entry? entry)) return;
            if (entry.Loading) entry.Invalidated = true;
            else Remove(entry);
        }
    }

    /// <summary>Stops every record read after a value differed from history.</summary>
    public void Disable(string reason)
    {
        if (Interlocked.Exchange(ref _disabled, 1) != 0) return;
        Metrics.RecordPrestateRecordVerifyMismatch();
        if (_logger.IsError) _logger.Error($"Pre-state records disagree with flat history and are no longer read until restart: {reason}");
    }

    private PrestateRecord? Load(in StateId parent)
    {
        try
        {
            PrestateRecord? record = store.Read(parent, out bool found);
            if (record is not null) return record;

            if (found) Metrics.RecordPrestateRecordInvalid();
            else Metrics.RecordPrestateRecordLeaseAbsent();
            return null;
        }
        catch (Exception exception)
        {
            Metrics.RecordPrestateRecordInvalid();
            if (_logger.IsWarn) _logger.Warn($"Pre-state record of state {parent} could not be read; reading flat history instead: {exception.Message}");
            return null;
        }
    }

    private PrestateRecord Lease(Entry entry)
    {
        if (entry.Node is not null)
        {
            _recency.Remove(entry.Node);
            _recency.AddFirst(entry.Node);
        }

        // The cache's own lease is held while the entry is in the dictionary, so this cannot fail.
        entry.Record!.TryLease();
        return entry.Record;
    }

    private void Evict()
    {
        while (_bytes > _budget && _recency.Last is { } last)
        {
            Remove(_entries[last.Value]);
        }
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry.Parent);
        if (entry.Node is not null)
        {
            _recency.Remove(entry.Node);
            Charge(-(entry.Record?.ByteSize ?? NegativeEntryBytes));
        }

        entry.Record?.Dispose();
    }

    private void Charge(long bytes)
    {
        _bytes += bytes;
        Metrics.PrestateRecordCacheBytes = _bytes;
    }

    private sealed class Entry(StateId parent)
    {
        public readonly StateId Parent = parent;
        public readonly long CreatedAt = Stopwatch.GetTimestamp();
        public PrestateRecord? Record;
        public bool Loading = true;
        public bool Invalidated;
        public LinkedListNode<StateId>? Node;
    }
}
