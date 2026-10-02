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
public sealed class LeanProofGossip(ProofWrapperService wrappers, ILogManager logManager) : IAsyncDisposable, IDisposable
{
    private readonly Lock _lock = new();
    private readonly HashSet<Action<byte[]>> _peers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger = logManager.GetClassLogger<LeanProofGossip>();
    private Task? _loop;
    private bool _disposed;
    private byte[]? _lastWrapper;

    public void AddPeer(Action<byte[]> send)
    {
        byte[]? last;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _peers.Add(send);
            _loop ??= Task.Run(RunAsync);
            last = _lastWrapper;
        }
        if (last is not null && wrappers.IsEnabled)
        {
            try { send(last); }
            catch (Exception exception) { if (_logger.IsDebug) _logger.Debug($"Lean proof peer send failed: {exception.Message}"); }
        }
    }

    public void RemovePeer(Action<byte[]> send)
    {
        lock (_lock) _peers.Remove(send);
    }

    private async Task RunAsync()
    {
        ValueHash256? previous = null;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                Action<byte[]>[] peers;
                lock (_lock) peers = [.. _peers];
                if (peers.Length == 0 || !wrappers.IsEnabled) continue;
                try
                {
                    Result<byte[]> result = wrappers.BuildWrapper(skipEmpty: true);
                    if (!result.IsSuccess) continue;
                    byte[] encoded = result.Data!;
                    ValueHash256 hash = ValueKeccak.Compute(encoded);
                    if (hash == previous) continue;
                    previous = hash;
                    lock (_lock) _lastWrapper = encoded;
                    foreach (Action<byte[]> send in peers)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        try { send(encoded); }
                        catch (Exception exception) { if (_logger.IsDebug) _logger.Debug($"Lean proof peer send failed: {exception.Message}"); }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { if (_logger.IsWarn) _logger.Warn($"Lean proof aggregation failed: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
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
            loop = _loop;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
