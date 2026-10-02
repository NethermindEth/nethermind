// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Eip8288;
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
    private sealed class Peer(Delegate key, Func<byte[], CancellationToken, ValueTask<bool>> send)
    {
        public Delegate Key { get; } = key;
        public Func<byte[], CancellationToken, ValueTask<bool>> Send { get; } = send;
        public byte[]? Pending;
        public Task? Worker;
        public Dictionary<ValueHash256, long> Recent { get; } = [];
        public TimeSpan RefreshDelay { get; } = TimeSpan.FromSeconds(RefreshIntervalSeconds)
            + TimeSpan.FromMilliseconds(Random.Shared.Next(RefreshJitterMilliseconds));
        public int Sending;
    }

    private readonly Dictionary<Delegate, Peer> _peers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger = logManager.GetClassLogger<LeanProofGossip>();
    private Task? _loop;
    private bool _disposed;
    private byte[]? _lastWrapper;
    private ValueHash256? _lastWrapperHash;

    /// <summary>Starts the shared producer, including nodes serving wrappers before their first peer connects.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(RunAsync);
        }
    }

    public void AddPeer(Func<byte[], bool> send) => AddPeerCore(send, (bytes, _) => new(send(bytes)));

    public void AddPeer(Func<byte[], CancellationToken, ValueTask<bool>> send) => AddPeerCore(send, send);

    private void AddPeerCore(Delegate key, Func<byte[], CancellationToken, ValueTask<bool>> send)
    {
        byte[]? last;
        Peer peer = new(key, send);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_peers.TryAdd(key, peer)) return;
            _loop ??= Task.Run(RunAsync);
            last = _lastWrapper;
        }
        if (last is not null && wrappers.IsEnabled)
            TrySend(peer, last, ValueKeccak.Compute(last));
    }

    public void RemovePeer(Func<byte[], bool> send) => RemovePeerCore(send);

    public void RemovePeer(Func<byte[], CancellationToken, ValueTask<bool>> send) => RemovePeerCore(send);

    private void RemovePeerCore(Delegate key)
    {
        lock (_lock)
            if (_peers.Remove(key, out Peer? peer)) peer.Pending = null;
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
                    ValueHash256 hash = ValueKeccak.Compute(encoded);
                    lock (_lock)
                    {
                        if (_disposed) break;
                        if (_lastWrapperHash == hash) encoded = _lastWrapper!;
                        else
                        {
                            _lastWrapper = encoded;
                            _lastWrapperHash = hash;
                        }
                    }
                    foreach (Peer peer in peers)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        TrySend(peer, encoded, hash);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { if (_logger.IsWarn) _logger.Warn($"Lean proof aggregation failed: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void TrySend(Peer peer, byte[] wrapper, ValueHash256 hash)
    {
        lock (_lock)
        {
            if (_disposed || !_peers.TryGetValue(peer.Key, out Peer? current) || current != peer) return;
            if (peer.Recent.TryGetValue(hash, out long sentAt)
                && _clock.GetElapsedTime(sentAt, _clock.GetTimestamp()) < peer.RefreshDelay) return;
            peer.Pending = wrapper;
            if (peer.Sending != 0) return;
            peer.Sending = 1;
            // One active transfer and the latest selection; a cadence never cancels an active object.
            peer.Worker = RunPeerAsync(peer, _stop.Token);
        }
    }

    private async Task RunPeerAsync(Peer peer, CancellationToken cancellationToken)
    {
        bool ownsWorker = true;
        try
        {
            while (true)
            {
                byte[] wrapper;
                ValueHash256 hash;
                lock (_lock)
                {
                    if (_disposed || !_peers.TryGetValue(peer.Key, out Peer? current) || current != peer || peer.Pending is null)
                    {
                        peer.Sending = 0;
                        ownsWorker = false;
                        return;
                    }
                    wrapper = peer.Pending;
                    peer.Pending = null;
                    hash = ValueKeccak.Compute(wrapper);
                    if (peer.Recent.TryGetValue(hash, out long sentAt)
                        && _clock.GetElapsedTime(sentAt, _clock.GetTimestamp()) < peer.RefreshDelay) continue;
                }
                if (await peer.Send(wrapper, cancellationToken).ConfigureAwait(false))
                    lock (_lock)
                    {
                        if (peer.Recent.Count == 64 && !peer.Recent.ContainsKey(hash))
                        {
                            ValueHash256 oldestHash = default;
                            long oldestTime = long.MaxValue;
                            foreach ((ValueHash256 known, long timestamp) in peer.Recent)
                                if (timestamp < oldestTime) { oldestHash = known; oldestTime = timestamp; }
                            peer.Recent.Remove(oldestHash);
                        }
                        peer.Recent[hash] = _clock.GetTimestamp();
                    }
                else return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (_logger.IsDebug) _logger.Debug($"Lean proof peer send failed: {exception.Message}"); }
        finally
        {
            lock (_lock)
                if (ownsWorker)
                {
                    peer.Sending = 0;
                    if (!_disposed && !cancellationToken.IsCancellationRequested && peer.Pending is not null
                        && _peers.TryGetValue(peer.Key, out Peer? current) && current == peer)
                    {
                        peer.Sending = 1;
                        peer.Worker = RunPeerAsync(peer, cancellationToken);
                    }
                }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        List<Task> workers = [];
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Peer peer in _peers.Values)
                if (peer.Worker is not null) workers.Add(peer.Worker);
            _peers.Clear();
            _lastWrapper = null;
            _lastWrapperHash = null;
            loop = _loop;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        await Task.WhenAll(workers).ConfigureAwait(false);
        _stop.Dispose();
    }
}
