// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Builds one aggregate per cadence and broadcasts it to negotiated peers.</summary>
public sealed class LeanProofGossip(ProofWrapperService wrappers, ILogManager logManager, TimeProvider? timeProvider = null) : IAsyncDisposable, IDisposable
{
    internal const int RefreshIntervalSeconds = 30;
    internal const int RefreshJitterMilliseconds = 5000;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private sealed record Wrapper(ReadOnlyMemory<byte> Bytes, ValueHash256 Hash);
    private sealed class Peer(Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> send)
    {
        public Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> Send { get; } = send;
        public Wrapper? Pending;
        public TaskCompletionSource? Worker;
        public Dictionary<ValueHash256, long> Recent { get; } = [];
        public TimeSpan RefreshDelay { get; } = TimeSpan.FromSeconds(RefreshIntervalSeconds)
            + TimeSpan.FromMilliseconds(Random.Shared.Next(RefreshJitterMilliseconds));
    }

    private readonly Dictionary<Delegate, Peer> _peers = [];
    private readonly HashSet<Task> _workers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger = logManager.GetClassLogger<LeanProofGossip>();
    private Task? _loop;
    private bool _disposed;
    private Wrapper? _lastWrapper;

    /// <summary>Starts the shared producer, including nodes serving wrappers before their first peer connects.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(RunAsync);
        }
    }

    public void AddPeer(Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> send)
    {
        Wrapper? last;
        Peer peer = new(send);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_peers.TryAdd(send, peer)) return;
            _loop ??= Task.Run(RunAsync);
            last = _lastWrapper;
        }
        if (last is not null && wrappers.IsEnabled) TrySend(peer, last);
    }

    public void RemovePeer(Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> send)
    {
        lock (_lock)
            if (_peers.Remove(send, out Peer? peer)) peer.Pending = null;
    }

    private async Task RunAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                Peer[] peers;
                lock (_lock) peers = [.. _peers.Values];
                if (!wrappers.IsEnabled) continue;
                try
                {
                    Result<byte[]> result = wrappers.BuildWrapper(skipEmpty: true, cancellationToken: _stop.Token);
                    if (!result.IsSuccess) continue;
                    byte[] encoded = result.Data!;
                    Wrapper wrapper = new(encoded, ValueKeccak.Compute(encoded));
                    lock (_lock)
                    {
                        if (_disposed) break;
                        if (_lastWrapper is { } last && last.Hash == wrapper.Hash) wrapper = last;
                        else _lastWrapper = wrapper;
                    }
                    foreach (Peer peer in peers)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        TrySend(peer, wrapper);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { if (_logger.IsWarn) _logger.Warn($"Lean proof aggregation failed: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void TrySend(Peer peer, Wrapper wrapper)
    {
        TaskCompletionSource? worker = null;
        lock (_lock)
        {
            if (_disposed || !_peers.TryGetValue(peer.Send, out Peer? current) || current != peer) return;
            if (WasSentRecently(peer, wrapper.Hash)) return;
            peer.Pending = wrapper;
            if (peer.Worker is null) worker = ReserveWorker(peer);
        }
        if (worker is not null) StartWorker(peer, worker);
    }

    private bool WasSentRecently(Peer peer, ValueHash256 hash)
        => peer.Recent.TryGetValue(hash, out long sentAt)
            && _clock.GetElapsedTime(sentAt, _clock.GetTimestamp()) < peer.RefreshDelay;

    private TaskCompletionSource ReserveWorker(Peer peer)
    {
        TaskCompletionSource worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Worker = worker;
        _workers.Add(worker.Task);
        return worker;
    }

    // Reservation is tracked before dispatch, including cancellation or peer removal before startup.
    private void StartWorker(Peer peer, TaskCompletionSource worker)
        => _ = Task.Run(() => RunPeerAsync(peer, worker));

    private async Task RunPeerAsync(Peer peer, TaskCompletionSource worker)
    {
        CancellationToken cancellationToken = _stop.Token;
        try
        {
            while (true)
            {
                Wrapper wrapper;
                lock (_lock)
                {
                    if (_disposed || !_peers.TryGetValue(peer.Send, out Peer? current) || current != peer || peer.Pending is null) return;
                    wrapper = peer.Pending;
                    peer.Pending = null;
                    if (WasSentRecently(peer, wrapper.Hash)) continue;
                }
                if (!await peer.Send(wrapper.Bytes, wrapper.Hash, cancellationToken).ConfigureAwait(false)) return;
                lock (_lock)
                {
                    if (_disposed || !_peers.TryGetValue(peer.Send, out Peer? current) || current != peer) return;
                    if (peer.Recent.Count == 64 && !peer.Recent.ContainsKey(wrapper.Hash))
                    {
                        ValueHash256 oldestHash = default;
                        long oldestTime = long.MaxValue;
                        foreach ((ValueHash256 known, long timestamp) in peer.Recent)
                            if (timestamp < oldestTime) { oldestHash = known; oldestTime = timestamp; }
                        peer.Recent.Remove(oldestHash);
                    }
                    peer.Recent[wrapper.Hash] = _clock.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (_logger.IsDebug) _logger.Debug($"Lean proof peer send failed: {exception.Message}"); }
        finally
        {
            TaskCompletionSource? next = null;
            lock (_lock)
            {
                peer.Worker = null;
                if (!_disposed && !cancellationToken.IsCancellationRequested && peer.Pending is not null
                    && _peers.TryGetValue(peer.Send, out Peer? current) && current == peer)
                    next = ReserveWorker(peer);
                worker.TrySetResult();
                _workers.Remove(worker.Task);
            }
            if (next is not null) StartWorker(peer, next);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        Task[] workers;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            workers = [.. _workers];
            _peers.Clear();
            _lastWrapper = null;
            loop = _loop;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        await Task.WhenAll(workers).ConfigureAwait(false);
        _stop.Dispose();
    }
}
