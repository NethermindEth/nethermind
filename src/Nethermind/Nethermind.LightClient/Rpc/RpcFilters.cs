// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

/// <summary>Maintains bounded polling cursors over authenticated head changes and locally admitted transactions.</summary>
internal sealed class RpcFilters(TimeProvider? timeProvider = null)
{
    internal const int MaxFilters = 32;
    internal const int MaxHeadChanges = 32;
    internal const int MaxPendingHashes = 1_024;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<UInt256, Filter> _filters = [];
    private readonly Queue<HeadChange> _history = new(MaxHeadChanges);
    private long _sequence;
    private long _lostThrough;

    internal long PublicationSequence => Volatile.Read(ref _sequence);
    internal long StartPublication() => Interlocked.Increment(ref _sequence);

    internal UInt256 Install(RpcBackend.LogQuery? query = null, bool pending = false)
    {
        if (query is { FromFinalized: true } or { ToFinalized: true })
            throw new RpcException(ErrorCodes.InvalidParams, "Polling log filters accept latest or numbered block bounds.");
        lock (_lock)
        {
            long now = _clock.GetTimestamp();
            Expire(now);
            if (_filters.Count == MaxFilters) throw new RpcException(ErrorCodes.LimitExceeded, "The polling filter limit is 32.");
            Filter filter = new(query, pending, PublicationSequence, now);
            UInt256 id;
            Span<byte> random = stackalloc byte[16];
            do
            {
                RandomNumberGenerator.Fill(random);
                id = new(random, isBigEndian: true);
            } while (!_filters.TryAdd(id, filter));
            return id;
        }
    }

    internal bool Uninstall(UInt256 id)
    {
        lock (_lock)
        {
            Expire(_clock.GetTimestamp());
            return _filters.Remove(id);
        }
    }

    internal bool HeadInterests(long sequence, ref bool logs)
    {
        lock (_lock)
        {
            Expire(_clock.GetTimestamp());
            bool interested = false;
            foreach (Filter filter in _filters.Values)
            {
                if (filter.Pending is not null || filter.Cursor >= sequence || filter.Cursor < _lostThrough) continue;
                if (filter.Query is not null)
                {
                    logs = true;
                    return true;
                }
                interested = true;
            }
            if (!interested) _history.Clear();
            return interested;
        }
    }

    internal void RecordHead(HeadChange change)
    {
        lock (_lock)
        {
            if (change.Sequence <= _lostThrough) return;
            if (_history.Count == MaxHeadChanges) _lostThrough = _history.Dequeue().Sequence;
            _history.Enqueue(change);
        }
    }

    internal void LoseHeadHistory(long sequence)
    {
        lock (_lock)
        {
            if (sequence <= _lostThrough) return;
            _lostThrough = sequence;
            _history.Clear();
        }
    }

    internal void PublishPending(Hash256 hash)
    {
        lock (_lock)
        {
            Expire(_clock.GetTimestamp());
            foreach (Filter filter in _filters.Values)
            {
                if (filter.Pending is not { } hashes || filter.PendingOverflow) continue;
                if (hashes.Count == MaxPendingHashes)
                {
                    hashes.Clear();
                    filter.PendingOverflow = true;
                }
                else hashes.Add(hash);
            }
        }
    }

    internal RpcBackend.LogQuery GetLogQuery(UInt256 id)
    {
        lock (_lock) return GetFilter(id, _clock.GetTimestamp()).Query ?? throw FilterNotFound();
    }

    internal async Task<JsonElement> PollAsync(UInt256 id, RpcBackend rpc, CancellationToken token, bool webSocket = false)
    {
        Filter filter;
        HeadChange[] changes;
        Hash256[]? hashes = null;
        lock (_lock)
        {
            token.ThrowIfCancellationRequested();
            long now = _clock.GetTimestamp();
            filter = GetFilter(id, now);
            if (filter.Polling) throw new RpcException(ErrorCodes.LimitExceeded, "A poll for this filter is already active.");
            filter.LastPoll = now;
            if (filter.Pending is { } pending)
            {
                if (filter.PendingOverflow) throw RemoveOverflowedFilter(id);
                if (pending.Count == 0) return Empty;
                hashes = [.. pending];
                pending.Clear();
                changes = [];
            }
            else
            {
                if (filter.Cursor < _lostThrough) throw RemoveOverflowedFilter(id);
                changes = [.. _history.Where(head => head.Sequence > filter.Cursor)];
                if (changes.Length == 0) return Empty;
                if (filter.Query is null)
                {
                    hashes = [.. changes.SelectMany(change => change.AddedHashes)];
                    filter.Cursor = changes[^1].Sequence;
                }
                else filter.Polling = true;
            }
        }
        if (hashes is not null) return BoundedJson.ToElement(hashes);
        try
        {
            JsonElement? result = await rpc.GetLogChangesAsync(filter.Query!, filter.Start, changes, token, webSocket);
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (!_filters.TryGetValue(id, out Filter? installed) || !ReferenceEquals(filter, installed)) throw FilterNotFound();
                if (changes[^1].Sequence < _lostThrough) throw RemoveOverflowedFilter(id);
                if (result is null)
                {
                    _filters.Remove(id);
                    throw new RpcException(ErrorCodes.LimitExceeded, "Polling filter changes exceed the HTTP response limit; recover logs by range and install a new filter.");
                }
                filter.Cursor = changes[^1].Sequence;
            }
            return result.Value;
        }
        finally
        {
            lock (_lock) filter.Polling = false;
        }
    }

    private Filter GetFilter(UInt256 id, long now)
    {
        Expire(now);
        return _filters.TryGetValue(id, out Filter? filter) ? filter : throw FilterNotFound();
    }

    private void Expire(long now)
    {
        foreach ((UInt256 id, Filter filter) in _filters)
            if (_clock.GetElapsedTime(filter.LastPoll, now) >= IdleTimeout) _filters.Remove(id);
    }

    private static RpcException FilterNotFound() => new(ErrorCodes.InvalidInput, "filter not found");

    private RpcException RemoveOverflowedFilter(UInt256 id)
    {
        _filters.Remove(id);
        return new(ErrorCodes.LimitExceeded, "Polling filter history was lost; install a new filter.");
    }

    internal readonly record struct LogBlock(long AddedAt, BlockHeader Header);
    internal sealed record HeadChange(long Sequence, LogBlock[] Removed, Hash256[] AddedHashes, BlockHeader[] AddedLogHeaders);

    private sealed class Filter(RpcBackend.LogQuery? query, bool pending, long start, long now)
    {
        internal RpcBackend.LogQuery? Query { get; } = query;
        internal List<Hash256>? Pending { get; } = pending ? [] : null;
        internal bool PendingOverflow { get; set; }
        internal long Start { get; } = start;
        internal long Cursor { get; set; } = start;
        internal long LastPoll { get; set; } = now;
        internal bool Polling { get; set; }
    }
}
