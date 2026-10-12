// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;

namespace Nethermind.LightClient;

/// <summary>Bounds peer serving without constructing the full block-processing pipeline.</summary>
internal sealed class LightPeerRequestScheduler : IBackgroundTaskScheduler, IAsyncDisposable
{
    private readonly Channel<Func<CancellationToken, Task>> _requests = Channel.CreateBounded<Func<CancellationToken, Task>>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly Task[] _workers;

    public LightPeerRequestScheduler(ILogManager logs)
    {
        _logger = logs.GetClassLogger<LightPeerRequestScheduler>();
        _workers = [RunAsync(), RunAsync()];
    }

    public bool TryScheduleTask<TRequest>(TRequest request, Func<TRequest, CancellationToken, Task> fulfill, TimeSpan? timeout = null)
        where TRequest : notnull, IBackgroundTaskRequest<TRequest>
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        return _requests.Writer.TryWrite(async stop =>
        {
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) cancellation.Cancel();
            else cancellation.CancelAfter(remaining);
            await fulfill(request, cancellation.Token);
        });
    }

    private async Task RunAsync()
    {
        await foreach (Func<CancellationToken, Task> request in _requests.Reader.ReadAllAsync())
        {
            try { await request(_stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Light-client peer serving failed: {exception.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _stop.CancelAsync();
        await Task.WhenAll(_workers);
        _stop.Dispose();
    }
}
