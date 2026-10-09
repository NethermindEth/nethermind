// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Drives background aggregation on the EIP-8288 cadence and publishes each new wrapper as a kind-1 object.</summary>
/// <remarks>Peers fetch the announced wrapper on demand, so an unchanged wrapper is never sent or announced again.</remarks>
public sealed class LeanProofGossip(ProofWrapperService wrappers, LeanObjectTransport transport, ILogManager logManager,
    TimeProvider? timeProvider = null) : IAsyncDisposable, IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logManager.GetClassLogger<LeanProofGossip>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private Task? _loop;
    private Task? _build;
    private ValueHash256? _published;
    private bool _disposed;

    /// <summary>Starts the shared producer, including on nodes that serve wrappers before their first peer connects.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(Eip8288Constants.AggregationInterval), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (!wrappers.IsEnabled) continue;
                    ScheduleBuild();
                    PublishLatest();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (_logger.IsWarn) _logger.Warn($"Lean proof refresh failed: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void ScheduleBuild()
    {
        lock (_lock)
        {
            if (_disposed || _build is { IsCompleted: false }) return;
            _build = Task.Run(Build);
        }
    }

    private void Build()
    {
        try
        {
            if (wrappers.RefreshWrapper(_stop.Token)) PublishLatest();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Lean proof aggregation failed: {exception.Message}");
        }
    }

    internal void PublishLatest()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Result<byte[]> latest = wrappers.GetLatestWrapper(_published, out ValueHash256 hash);
            if (!latest.IsSuccess || latest.Data!.Length == 0) return;
            // Already stored counts as published too, so an unchanged wrapper is not hashed again every cycle.
            transport.PublishWrapper(latest.Data);
            _published = hash;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        Task? build;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            build = _build;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        if (build is not null) await build.ConfigureAwait(false);
        _stop.Dispose();
    }
}
