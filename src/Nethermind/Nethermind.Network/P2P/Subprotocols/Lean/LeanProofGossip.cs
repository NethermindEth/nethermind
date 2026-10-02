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
    private sealed class Peer(Func<byte[], bool> send)
    {
        public Func<byte[], bool> Send { get; } = send;
        public Dictionary<ValueHash256, long> Recent { get; } = [];
        public TimeSpan RefreshDelay { get; } = TimeSpan.FromSeconds(RefreshIntervalSeconds)
            + TimeSpan.FromMilliseconds(Random.Shared.Next(RefreshJitterMilliseconds));
        public int Sending;
    }

    private readonly Dictionary<Func<byte[], bool>, Peer> _peers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger = logManager.GetClassLogger<LeanProofGossip>();
    private Task? _loop;
    private bool _disposed;
    private byte[]? _lastWrapper;

    /// <summary>Starts the shared producer, including nodes serving wrappers before their first peer connects.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(RunAsync);
        }
    }

    public void AddPeer(Func<byte[], bool> send)
    {
        byte[]? last;
        Peer peer = new(send);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_peers.TryAdd(send, peer)) return;
            _loop ??= Task.Run(RunAsync);
            last = _lastWrapper;
        }
        if (last is not null && wrappers.IsEnabled)
            TrySend(peer, last, ValueKeccak.Compute(last));
    }

    public void RemovePeer(Func<byte[], bool> send)
    {
        lock (_lock) _peers.Remove(send);
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
                        _lastWrapper = encoded;
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
        if (Interlocked.CompareExchange(ref peer.Sending, 1, 0) != 0) return;
        try
        {
            long now = _clock.GetTimestamp();
            lock (_lock)
                if (_disposed || !_peers.TryGetValue(peer.Send, out Peer? current) || current != peer
                    || (peer.Recent.TryGetValue(hash, out long sentAt) && _clock.GetElapsedTime(sentAt, now) < peer.RefreshDelay)) return;
            if (peer.Send(wrapper))
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
                    peer.Recent[hash] = now;
                }
        }
        catch (Exception exception) { if (_logger.IsDebug) _logger.Debug($"Lean proof peer send failed: {exception.Message}"); }
        finally { Volatile.Write(ref peer.Sending, 0); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _peers.Clear();
            _lastWrapper = null;
            loop = _loop;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
