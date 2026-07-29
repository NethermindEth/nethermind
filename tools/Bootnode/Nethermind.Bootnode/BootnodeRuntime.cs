// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class BootnodeRuntime(
    IDiscoveryApp discoveryApp,
    DiscoveredNodeStore nodeStore,
    BootnodeMetrics metrics,
    BootnodeKademliaBucketRegistry bucketRegistry,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IAsyncDisposable
{
    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<BootnodeRuntime>();
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _discoveryTask;
    private Task? _metricsTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        discoveryApp.NodeRemoved += OnNodeRemoved;
        await discoveryApp.StartAsync();
        _discoveryTask = Task.Run(() => TrackDiscoveredNodes(_stopCts.Token), cancellationToken);
        _metricsTask = Task.Run(() => TrackDiscoveryMetrics(_stopCts.Token), cancellationToken);
    }

    public async Task StopAsync()
    {
        discoveryApp.NodeRemoved -= OnNodeRemoved;
        await _stopCts.CancelAsync();

        await StopBackgroundTask(_discoveryTask);
        await StopBackgroundTask(_metricsTask);

        await discoveryApp.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
    }

    private async Task TrackDiscoveredNodes(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Node node in discoveryApp.DiscoverNodes(cancellationToken))
            {
                string protocol = DiscoveredNodeStore.InferProtocol(node);
                metrics.RecordSeen(protocol);
                metrics.UpdateSnapshot(nodeStore.AddOrUpdate(node, protocol, isActive: true));
                if (_logger.IsInfo) _logger.Info($"Discovered {protocol} node {node:s}");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsError) _logger.Error("Discovery tracking stopped unexpectedly.");
                processExitSource.Exit(1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Discovery tracking stopped unexpectedly.", exception);
            processExitSource.Exit(1);
        }
    }

    private async Task TrackDiscoveryMetrics(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            metrics.UpdateDiscoveryMessageCounters();
            metrics.UpdateKademliaBucketStats(bucketRegistry.CreateSnapshot());

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                metrics.UpdateDiscoveryMessageCounters();
                metrics.UpdateKademliaBucketStats(bucketRegistry.CreateSnapshot());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Discovery metrics tracking stopped unexpectedly.", exception);
        }
    }

    private void OnNodeRemoved(object? sender, NodeEventArgs args)
    {
        string protocol = nodeStore.GetProtocol(args.Node) ?? DiscoveredNodeStore.InferProtocol(args.Node);
        metrics.RecordRemoved(protocol);
        metrics.UpdateSnapshot(nodeStore.Remove(args.Node));
        if (_logger.IsDebug) _logger.Debug($"Removed discovery node {args.Node:s}");
    }

    private async Task StopBackgroundTask(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
        }
    }
}
