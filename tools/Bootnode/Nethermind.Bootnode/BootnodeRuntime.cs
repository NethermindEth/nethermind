// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class BootnodeRuntime(
    IDiscoveryApp discoveryApp,
    BootnodeDiscoverySource[] discoverySources,
    DiscoveredNodeStore nodeStore,
    BootnodeMetrics metrics,
    BootnodeKademliaBucketRegistry bucketRegistry,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IAsyncDisposable
{
    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<BootnodeRuntime>();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<(INodeSource Source, EventHandler<NodeEventArgs> Handler)> _nodeRemovedSubscriptions = [];
    private Task[] _discoveryTasks = [];
    private Task? _metricsTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeToNodeRemovals();
        await discoveryApp.StartAsync();

        _discoveryTasks = new Task[discoverySources.Length];
        for (int i = 0; i < discoverySources.Length; i++)
        {
            BootnodeDiscoverySource source = discoverySources[i];
            _discoveryTasks[i] = Task.Run(() => TrackDiscoveredNodes(source, _stopCts.Token), cancellationToken);
        }

        _metricsTask = Task.Run(() => TrackDiscoveryMetrics(_stopCts.Token), cancellationToken);
    }

    public async Task StopAsync()
    {
        UnsubscribeFromNodeRemovals();
        await _stopCts.CancelAsync();

        for (int i = 0; i < _discoveryTasks.Length; i++)
        {
            await StopBackgroundTask(_discoveryTasks[i]);
        }

        await StopBackgroundTask(_metricsTask);

        await discoveryApp.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
    }

    private async Task TrackDiscoveredNodes(BootnodeDiscoverySource source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Node node in source.NodeSource.DiscoverNodes(cancellationToken))
            {
                metrics.RecordSeen(source.Protocol);
                metrics.UpdateSnapshot(nodeStore.AddOrUpdate(node, source.Protocol, isActive: true));
                if (_logger.IsDebug) _logger.Debug($"Discovered {source.Protocol} node {node:s}");
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
            metrics.UpdateDiscoveryTrafficCounters();
            metrics.UpdateKademliaBucketStats(bucketRegistry.CreateSnapshot());

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                metrics.UpdateDiscoveryMessageCounters();
                metrics.UpdateDiscoveryTrafficCounters();
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

    private void OnNodeRemoved(string protocol, NodeEventArgs args)
    {
        metrics.RecordRemoved(protocol);
        metrics.UpdateSnapshot(nodeStore.Remove(args.Node, protocol));
        if (_logger.IsDebug) _logger.Debug($"Removed discovery node {args.Node:s}");
    }

    private void SubscribeToNodeRemovals()
    {
        for (int i = 0; i < discoverySources.Length; i++)
        {
            BootnodeDiscoverySource source = discoverySources[i];
            EventHandler<NodeEventArgs> handler = (_, args) => OnNodeRemoved(source.Protocol, args);
            source.NodeSource.NodeRemoved += handler;
            _nodeRemovedSubscriptions.Add((source.NodeSource, handler));
        }
    }

    private void UnsubscribeFromNodeRemovals()
    {
        for (int i = 0; i < _nodeRemovedSubscriptions.Count; i++)
        {
            (INodeSource source, EventHandler<NodeEventArgs> handler) = _nodeRemovedSubscriptions[i];
            source.NodeRemoved -= handler;
        }

        _nodeRemovedSubscriptions.Clear();
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

internal readonly record struct BootnodeDiscoverySource(string Protocol, INodeSource NodeSource);
