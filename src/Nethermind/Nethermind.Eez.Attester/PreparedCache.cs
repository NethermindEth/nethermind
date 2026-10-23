// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Prove;

namespace Nethermind.Eez.Attester;

internal sealed class PreparedCache(ByteString identity, TimeSpan requestTimeout,
    int maxEntries = 131072, long maxBytes = 512L * 1024 * 1024, TimeProvider? clock = null)
{
    internal const int MaximumLeases = 8;
    internal const int MaximumLeaseBlocks = 65535;
    private readonly object _lock = new();
    private readonly Dictionary<Hash256, Entry> _entries = [];
    private readonly Dictionary<ByteString, Lease> _leases = [];
    private readonly LinkedList<Hash256> _lru = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _bytes;

    internal int MaxEntries { get; } = maxEntries;
    internal long MaxBytes { get; } = maxBytes;
    internal TimeSpan LeaseLifetime { get; } = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(2).Ticks, checked(requestTimeout.Ticks * 2)));

    internal ByteString Publish(IReadOnlyList<PreparedOrdinaryBlock> facts, ByteString requestedLease, CancellationToken cancellation)
    {
        lock (_lock)
        {
            Expire();
            cancellation.ThrowIfCancellationRequested();
            Lease? existing = FindLease(requestedLease, allowNew: true);
            if (existing is null && _leases.Count == MaximumLeases) throw Quota("maximum preparation leases reached");
            if (facts.Count is 0 or > 64) throw WindowAssembler.Invalid("preparation requires 1..64 ordinary blocks");
            HashSet<Hash256> keys = [];
            Dictionary<Hash256, PreparedOrdinaryBlock> incoming = [];
            for (int i = 0; i < facts.Count; i++)
            {
                PreparedOrdinaryBlock fact = facts[i];
                if (i != 0 && (fact.Number != facts[i - 1].Number + 1 || fact.ParentHash != facts[i - 1].Hash || fact.PreStateRoot != facts[i - 1].PostStateRoot))
                    throw WindowAssembler.Invalid("prepared range continuity invalid");
                if (existing is null || !existing.Keys.Contains(fact.Hash)) keys.Add(fact.Hash);
                if (!_entries.ContainsKey(fact.Hash)) incoming.TryAdd(fact.Hash, fact);
            }
            if (keys.Count + (existing?.Keys.Count ?? 0) > MaximumLeaseBlocks) throw Quota("lease exceeds 65535 blocks");
            long bytes = _bytes;
            int count = _entries.Count;
            foreach (PreparedOrdinaryBlock fact in incoming.Values)
            {
                bytes = checked(bytes + fact.RetainedBytes);
                count++;
            }
            List<Hash256> evictions = [];
            for (LinkedListNode<Hash256>? node = _lru.First; (bytes > MaxBytes || count > MaxEntries) && node is not null; node = node.Next)
            {
                Entry entry = _entries[node.Value];
                if (entry.Reservations != 0 || entry.Active != 0 || keys.Contains(node.Value)) continue;
                evictions.Add(node.Value);
                bytes -= entry.Fact.RetainedBytes;
                count--;
            }
            if (bytes > MaxBytes || count > MaxEntries) throw Quota("prepared cache capacity exhausted");
            ByteString leaseId = existing is null ? ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) : requestedLease;
            cancellation.ThrowIfCancellationRequested();
            foreach (Hash256 hash in evictions)
            {
                _lru.Remove(_entries[hash].Node);
                _entries.Remove(hash);
            }
            foreach (PreparedOrdinaryBlock fact in incoming.Values)
                _entries.Add(fact.Hash, new Entry(fact, _lru.AddLast(fact.Hash)));
            _bytes = bytes;
            if (existing is null) ReplaceLease(leaseId, null, keys);
            else
            {
                foreach (Hash256 hash in keys)
                {
                    existing.Keys.Add(hash);
                    Entry entry = _entries[hash];
                    entry.Reservations++;
                }
                foreach (PreparedOrdinaryBlock fact in facts) Touch(_entries[fact.Hash]);
                _leases[leaseId] = existing with { Started = _clock.GetTimestamp() };
            }
            return leaseId;
        }
    }

    internal BorrowedRange Borrow(CachedOrdinaryRange range)
    {
        lock (_lock)
        {
            Expire();
            PreparedOrdinaryBlock[] facts = Resolve(range);
            foreach (PreparedOrdinaryBlock fact in facts) _entries[fact.Hash].Active++;
            return new BorrowedRange(this, facts);
        }
    }

    internal RetainOrdinaryRangesResponse Retain(RetainOrdinaryRangesRequest request, CancellationToken cancellation)
    {
        lock (_lock)
        {
            Expire();
            CheckIdentity(request.ValidationIdentity);
            Lease lease = FindLease(request.LeaseId, allowNew: false)!;
            HashSet<Hash256> keys = [];
            int count = 0;
            if (request.Release)
            {
                if (request.Ranges.Count != 0) throw WindowAssembler.Invalid("lease release cannot carry ranges");
            }
            else
            {
                if (request.Ranges.Count == 0) throw WindowAssembler.Invalid("lease renewal requires ranges");
                foreach (CachedOrdinaryRange range in request.Ranges)
                {
                    ulong length = RangeLength(range);
                    if (length > (ulong)(MaximumLeaseBlocks - count)) throw Quota("lease union exceeds 65535 facts");
                    PreparedOrdinaryBlock[] facts = Resolve(range);
                    count += facts.Length;
                    foreach (PreparedOrdinaryBlock fact in facts) keys.Add(fact.Hash);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if (request.Release)
            {
                foreach (Hash256 key in lease.Keys) _entries[key].Reservations--;
                _leases.Remove(request.LeaseId);
            }
            else ReplaceLease(request.LeaseId, lease, keys);
            return new RetainOrdinaryRangesResponse
            {
                ValidationIdentity = identity,
                LeaseId = request.LeaseId,
                RetainedCount = (ulong)count,
            };
        }
    }

    internal static ulong RangeLength(CachedOrdinaryRange range)
    {
        if (range.FirstNumber == 0 || range.LastNumber < range.FirstNumber ||
            range.LastNumber - range.FirstNumber >= MaximumLeaseBlocks)
            throw WindowAssembler.Invalid("cached range bounds invalid");
        return range.LastNumber - range.FirstNumber + 1;
    }

    private PreparedOrdinaryBlock[] Resolve(CachedOrdinaryRange range)
    {
        CheckIdentity(range.ValidationIdentity);
        ulong count = RangeLength(range);
        if (range.FirstHash.Length != 32 || range.LastHash.Length != 32) throw WindowAssembler.Invalid("cached range hash length invalid");
        Hash256 firstHash = new(range.FirstHash.Span);
        Hash256 hash = new(range.LastHash.Span);
        if (!_entries.TryGetValue(firstHash, out Entry? first) || first.Fact.Number != range.FirstNumber)
            throw Miss(range.FirstNumber, firstHash);
        PreparedOrdinaryBlock[] facts = new PreparedOrdinaryBlock[(int)count];
        for (int i = facts.Length - 1; i >= 0; i--)
        {
            ulong number = range.FirstNumber + (ulong)i;
            if (!_entries.TryGetValue(hash, out Entry? entry) || entry.Fact.Number != number) throw Miss(number, hash);
            facts[i] = entry.Fact;
            if (i < facts.Length - 1 && entry.Fact.PostStateRoot != facts[i + 1].PreStateRoot)
                throw WindowAssembler.Invalid("cached range state continuity invalid");
            hash = entry.Fact.ParentHash;
        }
        if (facts[0].Hash != firstHash) throw WindowAssembler.Invalid("cached range endpoints differ");
        foreach (PreparedOrdinaryBlock fact in facts) Touch(_entries[fact.Hash]);
        return facts;
    }

    private void CheckIdentity(ByteString actual)
    {
        if (actual.Length != 32) throw WindowAssembler.Invalid("validation identity must contain 32 bytes");
        if (actual != identity) throw new RpcException(new Status(StatusCode.FailedPrecondition, "native validation identity differs"));
    }

    private Lease? FindLease(ByteString id, bool allowNew)
    {
        if (allowNew && id.Length == 0) return null;
        if (id.Length != 32) throw WindowAssembler.Invalid("lease id must contain 32 bytes");
        if (!_leases.TryGetValue(id, out Lease? lease))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "preparation lease unavailable"));
        return lease;
    }

    private void ReplaceLease(ByteString id, Lease? previous, HashSet<Hash256> keys)
    {
        if (previous is not null)
            foreach (Hash256 hash in previous.Keys) _entries[hash].Reservations--;
        foreach (Hash256 hash in keys)
        {
            Entry entry = _entries[hash];
            entry.Reservations++;
            Touch(entry);
        }
        _leases[id] = new Lease(keys, _clock.GetTimestamp());
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddLast(entry.Node);
    }

    private void Expire()
    {
        List<ByteString>? expired = null;
        foreach (KeyValuePair<ByteString, Lease> pair in _leases)
            if (_clock.GetElapsedTime(pair.Value.Started) >= LeaseLifetime) (expired ??= []).Add(pair.Key);
        if (expired is null) return;
        foreach (ByteString id in expired)
        {
            foreach (Hash256 hash in _leases[id].Keys) _entries[hash].Reservations--;
            _leases.Remove(id);
        }
    }

    private void Return(PreparedOrdinaryBlock[] facts)
    {
        lock (_lock)
            foreach (PreparedOrdinaryBlock fact in facts) _entries[fact.Hash].Active--;
    }

    private RpcException Miss(ulong number, Hash256 hash)
    {
        ProveFailure failure = new() { CacheMiss = new CacheMiss { ValidationIdentity = identity, BlockNumber = number, BlockHash = ByteString.CopyFrom(hash.Bytes) } };
        return new RpcException(new Status(StatusCode.FailedPrecondition, "cached ordinary block is unavailable"),
            new Metadata { { "grpc-status-details-bin", failure.ToByteArray() } });
    }

    private static RpcException Quota(string message) => new(new Status(StatusCode.ResourceExhausted, message));

    private sealed class Entry(PreparedOrdinaryBlock fact, LinkedListNode<Hash256> node)
    {
        internal PreparedOrdinaryBlock Fact { get; } = fact;
        internal LinkedListNode<Hash256> Node { get; } = node;
        internal int Reservations { get; set; }
        internal int Active { get; set; }
    }

    private sealed record Lease(HashSet<Hash256> Keys, long Started);

    internal sealed class BorrowedRange(PreparedCache owner, PreparedOrdinaryBlock[] facts) : IDisposable
    {
        private PreparedCache? _owner = owner;
        internal PreparedOrdinaryBlock[] Facts { get; } = facts;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Facts);
    }
}
