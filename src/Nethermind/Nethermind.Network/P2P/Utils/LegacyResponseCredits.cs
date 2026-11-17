// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Utils;

internal sealed class LegacyResponseCredits(TimeProvider? timeProvider = null) : IDisposable
{
    private const int Capacity = 32;
    private const int MaxRequestHashes = 256;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly LinkedList<long> _pending = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<LinkedListNode<long>, Record> _records = [with(Capacity)];
    private ITimer? _timer;
    private bool _disposed;
    private long _creditDisabledUntil;

    private readonly record struct MatchEvidence(Record Record, RetryRequestToken<ValueHash256> Token, int Count);

    private sealed class Record(ReadOnlySpan<ValueHash256> hashes, ReadOnlySpan<RetryRequestToken<ValueHash256>> tokens,
        PeerWorkQuota.Reservation? reservation) : IDisposable
    {
        internal readonly ArrayPoolList<ValueHash256> Hashes = [with(hashes)];
        internal readonly ArrayPoolList<RetryRequestToken<ValueHash256>> Tokens = [with(tokens)];
        internal PeerWorkQuota.Reservation? Reservation = reservation;
        internal bool Claimed;
        internal bool Completed;

        public void Dispose()
        {
            Hashes.Dispose();
            Tokens.Dispose();
            Reservation?.Dispose();
            Reservation = null;
        }
    }

    internal LinkedListNode<long>? Reserve(PeerWorkQuota.Reservation? reservation = null,
        ReadOnlySpan<ValueHash256> hashes = default, ReadOnlySpan<RetryRequestToken<ValueHash256>> tokens = default)
    {
        lock (_lock)
        {
            if (_disposed || hashes.Length > MaxRequestHashes || tokens.Length > hashes.Length) return null;
            Expire();
            if (_records.Count >= Capacity)
            {
                LinkedListNode<long>? reusable = _pending.First;
                while (reusable is not null && !_records[reusable].Completed) reusable = reusable.Next;
                if (reusable is null) return null;
                if (_records[reusable].Hashes.Count > 0)
                    _creditDisabledUntil = Math.Max(_creditDisabledUntil,
                        reusable.Value + (long)(Timeouts.Eth.TotalSeconds * _timeProvider.TimestampFrequency));
                Remove(reusable);
            }
            _timer ??= _timeProvider.CreateTimer(static state => ((LegacyResponseCredits)state!).ExpirePending(),
                this, Timeouts.Eth, Timeouts.Eth);
            LinkedListNode<long> credit = new(_timeProvider.GetTimestamp());
            Record record = new(hashes, tokens, reservation?.Retain());
            _pending.AddLast(credit);
            _records.Add(credit, record);
            return credit;
        }
    }

    internal void Cancel(LinkedListNode<long> reservation)
    {
        lock (_lock)
            if (reservation.List is not null) Remove(reservation);
    }

    internal bool Consume()
    {
        bool consumed = TryConsume(out PeerWorkQuota.Reservation? reservation);
        reservation?.Dispose();
        return consumed;
    }

    internal bool TryConsume(out PeerWorkQuota.Reservation? reservation)
    {
        bool consumed = TryClaim([], out reservation, out PooledTransactionRequests.ResponseContext? context);
        context?.Dispose();
        return consumed;
    }

    internal bool TryClaim(ReadOnlySpan<Transaction> transactions, out PeerWorkQuota.Reservation? reservation,
        out PooledTransactionRequests.ResponseContext? context)
    {
        lock (_lock)
        {
            Expire();
            reservation = null;
            context = null;
            LinkedListNode<long>? selected = null;
            for (LinkedListNode<long>? item = _pending.First; item is not null; item = item.Next)
                if (!_records[item].Claimed && !_records[item].Completed) { selected = item; break; }
            if (selected is null) return false;
            bool bounded = transactions.Length <= MaxRequestHashes;
            bool enabled = bounded && _timeProvider.GetTimestamp() >= _creditDisabledUntil;
            HashSet<ValueHash256> returned = [];
            bool completeHashes = transactions.Length > 0;
            if (bounded)
                foreach (Transaction transaction in transactions)
                {
                    if (transaction.Hash is { } hash) returned.Add(hash.ValueHash256);
                    else completeHashes = false;
                }
            Dictionary<ValueHash256, MatchEvidence> evidence = [with(returned.Count)];
            HashSet<ValueHash256> matched = [];
            bool matchedRequest = false;
            if (bounded)
                foreach (KeyValuePair<LinkedListNode<long>, Record> entry in _records)
                {
                    Record record = entry.Value;
                    matched.Clear();
                    bool live = _timeProvider.GetElapsedTime(entry.Key.Value) < Timeouts.Eth;
                    for (int i = 0; i < record.Hashes.Count; i++)
                    {
                        ValueHash256 hash = record.Hashes[i];
                        if (!returned.Contains(hash)) continue;
                        matched.Add(hash);
                        if (!enabled || !live) continue;
                        RetryRequestToken<ValueHash256> token = i < record.Tokens.Count ? record.Tokens[i] : default;
                        if (evidence.TryGetValue(hash, out MatchEvidence previous))
                            evidence[hash] = new(record, token, previous.Count + 1);
                        else evidence.Add(hash, new(record, token, 1));
                    }
                    if (!matchedRequest && completeHashes && !record.Claimed && !record.Completed && matched.Count == returned.Count)
                    {
                        selected = entry.Key;
                        matchedRequest = true;
                    }
                }
            if (!matchedRequest && !transactions.IsEmpty) return false;
            Record claimed = _records[selected];
            claimed.Claimed = true;
            reservation = claimed.Reservation;
            claimed.Reservation = null;
            LinkedListNode<long> owned = selected;
            context = new(bounded ? transactions.Length : 0, () => Complete(owned));
            if (bounded)
                foreach (Transaction transaction in transactions)
                {
                    RetryRequestToken<ValueHash256> token = default;
                    if (completeHashes && transaction.Hash is { } hash && evidence.TryGetValue(hash.ValueHash256, out MatchEvidence match)
                        && match.Count == 1 && ReferenceEquals(match.Record, claimed)) token = match.Token;
                    context.Tokens.Add(token);
                }
            return true;
        }
    }

    private void Complete(LinkedListNode<long> node)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(node, out Record? record)) return;
            record.Claimed = false;
            record.Completed = true;
            Expire();
        }
    }

    private void Remove(LinkedListNode<long> node)
    {
        _pending.Remove(node);
        if (_records.Remove(node, out Record? record)) record.Dispose();
    }

    private void Expire()
    {
        long now = _timeProvider.GetTimestamp();
        for (LinkedListNode<long>? node = _pending.First; node is not null;)
        {
            LinkedListNode<long>? next = node.Next;
            if (!_records[node].Claimed && _timeProvider.GetElapsedTime(node.Value, now) >= Timeouts.Eth) Remove(node);
            node = next;
        }
    }

    internal void ExpirePending()
    {
        lock (_lock)
            if (!_disposed) Expire();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            foreach (Record record in _records.Values) record.Dispose();
            _records.Clear();
            _pending.Clear();
        }
    }
}
