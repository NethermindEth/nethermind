// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth;

/// <summary>Owns bounded request snapshots and reservations until claimed, expired, or disposed.</summary>
internal sealed class PooledTransactionRequests : IDisposable
{
    private readonly Lock _lock = new();
    private readonly ArrayPool<ValueHash256> _pool;
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private ITimer? _timer;
    private readonly Dictionary<long, int> _indices = [];
    private Entry[] _entries = [];
    private int _allocated;
    private int _free = -1;
    private int _first = -1;
    private int _last = -1;
    private bool _disposed;

    internal PooledTransactionRequests(int capacity, ArrayPool<ValueHash256>? pool = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _pool = pool ?? ArrayPool<ValueHash256>.Shared;
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? LimboLogs.Instance.GetClassLogger<PooledTransactionRequests>();
    }

    internal bool TryAdd(long id, ReadOnlySpan<ValueHash256> hashes, PeerWorkQuota.Reservation? reservation = null,
        RequestLifecycle? lifecycle = null, ReadOnlySpan<RetryRequestToken<ValueHash256>> tokens = default)
    {
        List<Request>? expired = null;
        try
        {
            lock (_lock)
            {
                if (_disposed) return false;
                Expire(ref expired);
                if (_indices.ContainsKey(id) || _indices.Count == _capacity) return false;
                if (_entries.Length == 0)
                {
                    _indices.EnsureCapacity(_capacity);
                    _entries = new Entry[_capacity];
                }

                ValueHash256[] array = _pool.Rent(hashes.Length);
                PeerWorkQuota.Reservation? retained = null;
                try
                {
                    hashes.CopyTo(array);
                    retained = reservation?.Retain();
                    _timer ??= _timeProvider.CreateTimer(static state => ((PooledTransactionRequests)state!).OnTimer(),
                        this, Timeouts.Eth, Timeouts.Eth);
                    Request request = new(_pool, array, hashes.Length, retained, lifecycle, tokens);
                    long sentAt = _timeProvider.GetTimestamp();
                    int index;
                    if (_free >= 0)
                    {
                        index = _free;
                        _free = _entries[index].Next;
                    }
                    else index = _allocated++;

                    _entries[index] = new Entry(id, request, _last, sentAt);
                    if (_last >= 0) _entries[_last].Next = index;
                    else _first = index;
                    _last = index;
                    _indices.Add(id, index);
                    return true;
                }
                catch
                {
                    _pool.Return(array);
                    retained?.Dispose();
                    throw;
                }
            }
        }
        finally { CompleteExpired(expired); }
    }

    internal bool TryClaim(long id, [NotNullWhen(true)] out Request? request, DateTimeOffset? receivedAt = null)
    {
        List<Request>? expired = null;
        Request? claimed = null;
        try
        {
            lock (_lock)
            {
                if (_indices.TryGetValue(id, out int index)
                    && _entries[index].Request.Lifecycle is { } candidateLifecycle
                    && candidateLifecycle.IsOnTime(receivedAt))
                {
                    claimed = Remove(index);
                    Expire(ref expired);
                }
                else
                {
                    Expire(ref expired);
                    if (_indices.TryGetValue(id, out index))
                    {
                        if (_entries[index].Request.Lifecycle is null) claimed = Remove(index);
                        else (expired ??= []).Add(Remove(index));
                    }
                }
            }
        }
        catch
        {
            if (claimed is not null) CompleteRequest(claimed, expired: false, dispose: true);
            throw;
        }
        finally { CompleteExpired(expired); }
        if (claimed is not null) CompleteRequest(claimed, expired: false, dispose: false);
        request = claimed;
        return request is not null;
    }

    internal void ConfirmSent(long id)
    {
        RequestLifecycle? lifecycle = null;
        try
        {
            lock (_lock)
            {
                if (_indices.TryGetValue(id, out int index))
                {
                    lifecycle = _entries[index].Request.Lifecycle;
                    lifecycle?.ConfirmSent();
                }
            }
            lifecycle?.NotifySent();
        }
        catch
        {
            Cancel(id);
            throw;
        }
    }

    internal void Cancel(long id)
    {
        Request? canceled = null;
        lock (_lock)
        {
            if (_indices.TryGetValue(id, out int index)) canceled = Remove(index);
        }
        if (canceled is null) return;
        CompleteRequest(canceled, expired: false, dispose: true);
    }

    internal void ExpirePending()
    {
        List<Request>? expired = null;
        try
        {
            lock (_lock)
                if (!_disposed) Expire(ref expired);
        }
        finally { CompleteExpired(expired); }
    }

    private void OnTimer()
    {
        try { ExpirePending(); }
        catch (Exception exception) { _logger.Error("Failed to complete expired pooled transaction requests.", exception); }
    }

    private void Expire(ref List<Request>? expired)
    {
        long now = _timeProvider.GetTimestamp();
        for (int index = _first; index >= 0;)
        {
            Entry entry = _entries[index];
            bool due = entry.Request.Lifecycle is { } lifecycle
                ? lifecycle.CanExpire()
                : _timeProvider.GetElapsedTime(entry.SentAt, now) >= Timeouts.Eth;
            if (due) (expired ??= []).Add(Remove(index));
            index = entry.Next;
        }
    }

    private void CompleteExpired(List<Request>? expired)
        => CompleteRequests(expired, expired: true);

    private void CompleteRequests(List<Request>? requests, bool expired)
    {
        if (requests is null) return;
        foreach (Request request in requests)
            CompleteRequest(request, expired, dispose: true);
    }

    private void CompleteRequest(Request request, bool expired, bool dispose)
    {
        try { request.Lifecycle?.Complete(expired); }
        catch (Exception exception) { _logger.Error("Failed to complete a pooled transaction request.", exception); }
        if (!dispose) return;
        try { request.Dispose(); }
        catch (Exception exception) { _logger.Error("Failed to release a pooled transaction request.", exception); }
    }

    private Request Remove(int index)
    {
        ref Entry entry = ref _entries[index];
        _indices.Remove(entry.Id);
        if (entry.Previous >= 0) _entries[entry.Previous].Next = entry.Next;
        else _first = entry.Next;
        if (entry.Next >= 0) _entries[entry.Next].Previous = entry.Previous;
        else _last = entry.Previous;
        Request request = entry.Request;
        entry = default;
        entry.Next = _free;
        _free = index;
        return request;
    }

    public void Dispose()
    {
        List<Request>? pending = null;
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            while (_first >= 0) (pending ??= []).Add(Remove(_first));
        }
        CompleteRequests(pending, expired: false);
    }

    internal sealed class RequestLifecycle(PacketReceiptTracker receipts, ITimestamper clock, Action<bool> completed, Action? sent = null, Action<ValueHash256, bool>? omitted = null)
    {
        private readonly PacketReceiptTracker.ExpiryGuard _guard = receipts.CreateExpiryGuard();
        private DateTimeOffset _deadline;
        private bool _sent;
        private int _completed;

        internal void ConfirmSent()
        {
            _deadline = clock.UtcNowOffset + Timeouts.Eth;
            _sent = true;
        }

        internal void NotifySent() => sent?.Invoke();

        internal void RecordOmission(ValueHash256 hash, bool retainCellAnnouncement) => omitted?.Invoke(hash, retainCellAnnouncement);

        internal bool IsOnTime(DateTimeOffset? receivedAt)
            => !_sent || (receivedAt ?? clock.UtcNowOffset) <= _deadline;

        internal bool CanExpire() => receipts.IsClosed || (_sent && clock.UtcNowOffset >= _deadline && _guard.CanExpire());

        internal void Complete(bool expired)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) completed(expired && _sent);
        }
    }

    private struct Entry(long id, Request request, int previous, long sentAt)
    {
        internal long Id = id;
        internal long SentAt = sentAt;
        internal Request Request = request;
        internal int Previous = previous;
        internal int Next = -1;
    }

    /// <summary>A claimed snapshot whose reservation remains live through response processing.</summary>
    internal sealed class Request(ArrayPool<ValueHash256> pool, ValueHash256[] hashes, int count,
        PeerWorkQuota.Reservation? reservation, RequestLifecycle? lifecycle, ReadOnlySpan<RetryRequestToken<ValueHash256>> tokens) : IDisposable
    {
        private ValueHash256[]? _hashes = hashes;
        private readonly ArrayPoolList<RetryRequestToken<ValueHash256>> _tokens = [with(tokens)];
        internal PeerWorkQuota.Reservation? Reservation => reservation;
        internal RequestLifecycle? Lifecycle => lifecycle;
        internal ReadOnlySpan<ValueHash256> Hashes => (_hashes ?? ThrowRequestDisposed()).AsSpan(0, count);

        internal ResponseContext CreateResponseContext(ReadOnlySpan<Transaction> transactions)
        {
            ResponseContext context = new(transactions.Length);
            int requestedIndex = 0;
            ReadOnlySpan<ValueHash256> requested = Hashes;
            foreach (Transaction transaction in transactions)
            {
                while (requestedIndex < count && requested[requestedIndex] != transaction.Hash!.ValueHash256) requestedIndex++;
                RetryRequestToken<ValueHash256> token = requestedIndex < _tokens.Count ? _tokens[requestedIndex] : default;
                context.Tokens.Add(token);
                requestedIndex++;
            }
            return context;
        }

        public void Dispose()
        {
            ValueHash256[]? owned = Interlocked.Exchange(ref _hashes, null);
            if (owned is null) return;
            try { pool.Return(owned); }
            finally
            {
                _tokens.Dispose();
                reservation?.Dispose();
            }
        }
    }

    internal sealed class ResponseContext(int capacity, Action? completed = null) : IDisposable
    {
        internal ArrayPoolList<RetryRequestToken<ValueHash256>> Tokens { get; } = [with(capacity)];
        private int _disposed;

        internal RetryRequestToken<ValueHash256> GetToken(int index) => index < Tokens.Count ? Tokens[index] : default;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Tokens.Dispose();
            completed?.Invoke();
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static ValueHash256[] ThrowRequestDisposed() =>
        throw new ObjectDisposedException(nameof(Request));
}
