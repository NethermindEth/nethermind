// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(RegisterPluginRpcModules))]
public class StartNetwork(
    INethermindApi api,
    ISyncPeerPool syncPeerPool,
    ISynchronizer synchronizer,
    IDiscoveryApp discoveryApp,
    Lazy<IPeerPool> peerPool,
    ISyncConfig syncConfig,
    IInitConfig initConfig,
    ILogManager logManager) : IStep
{

    ILogger _logger = logManager.GetClassLogger();

    public async Task Execute(CancellationToken cancellationToken)
    {
        await StartSync().ContinueWith(initNetTask =>
        {
            if (initNetTask.IsFaulted)
            {
                _logger.Error("Unable to start the synchronizer.", initNetTask.Exception);
            }
        });

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await StartDiscovery().ContinueWith(initDiscoveryTask =>
        {
            if (initDiscoveryTask.IsFaulted)
            {
                _logger.Error("Unable to start the discovery protocol.", initDiscoveryTask.Exception);
            }
        });

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            StartPeer();
        }
        catch (Exception e)
        {
            _logger.Error("Unable to start the peer manager.", e);
        }
    }


    private Task StartDiscovery()
    {
        if (!initConfig.DiscoveryEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to {nameof(IInitConfig.DiscoveryEnabled)} set to false");
            return Task.CompletedTask;
        }

        if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
        _ = discoveryApp.StartAsync();
        if (_logger.IsDebug) _logger.Debug("Discovery process started.");
        return Task.CompletedTask;
    }

    private void StartPeer()
    {
        if (api.PeerManager is null) throw new StepDependencyException(nameof(api.PeerManager));
        if (api.SessionMonitor is null) throw new StepDependencyException(nameof(api.SessionMonitor));

        if (!api.Config<IInitConfig>().PeerManagerEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
        }

        if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
        peerPool.Value.Start();
        api.PeerManager.Start();
        api.SessionMonitor.Start();
        if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
    }

    private Task StartSync()
    {
        if (api.BlockTree is null) throw new StepDependencyException(nameof(api.BlockTree));

        if (syncConfig.NetworkingEnabled)
        {
            syncPeerPool.Start();

            if (syncConfig.SynchronizationEnabled)
            {
                if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {api.BlockTree.Head?.Header.ToString(BlockHeader.Format.Short)}.");
                synchronizer.Start();
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to {nameof(ISyncConfig.SynchronizationEnabled)} set to false");
            }
        }
        else if (_logger.IsWarn) _logger.Warn($"Skipping connecting to peers due to {nameof(ISyncConfig.NetworkingEnabled)} set to false");


        return Task.CompletedTask;
    }
}
