// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>Drives background aggregation on the EIP-8288 cadence, producing mode-1 wrappers over bounded subsets that together
/// cover the pending dependency-bearing transactions, and offers each new one over <c>eth/73</c>.</summary>
/// <remarks>Peers fetch the announced wrapper on demand, so an unchanged wrapper is never sent or announced again.</remarks>
public sealed class ProofWrapperGossip(ProofWrapperService wrappers, ProofWrapperTransport transport, ILogManager logManager,
    TimeProvider? timeProvider = null) : IAsyncDisposable, IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logManager.GetClassLogger<ProofWrapperGossip>();
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
            // A chain without a scheduled EIP-8288 fork never aggregates.
            if (!wrappers.IsScheduled) return;
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
                    if (_logger.IsWarn) _logger.Warn($"Proof wrapper refresh failed: {exception.Message}");
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
            // Each bounded wrapper of the cycle is offered as soon as it is built; a cycle proves for at most one interval.
            wrappers.RefreshWrapper(PublishLatest, TimeSpan.FromMilliseconds(Eip8288Constants.AggregationInterval), _clock, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Proof wrapper aggregation failed: {exception.Message}");
        }
    }

    internal void PublishLatest()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Result<byte[]> latest = wrappers.GetLatestWrapper(_published, out ValueHash256 hash);
            if (!latest.IsSuccess || latest.Data!.Length == 0) return;
            // Already stored counts as published too, so an unchanged wrapper is not hashed again every cycle; one that failed
            // to publish, for example during a verifier change, is offered again on the next cycle.
            if (transport.PublishWrapper(latest.Data) || transport.IsStored(hash)) _published = hash;
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
