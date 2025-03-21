// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum;

public class EthereumRunner(INethermindApi api, EthereumStepsManager stepsManager, ILifetimeScope lifetimeScope)
{
    private readonly INethermindApi _api = api;
    public INethermindApi Api => _api;
    public ILifetimeScope LifetimeScope => lifetimeScope;
    private readonly ILogger _logger = api.LogManager.GetClassLogger();

    public async Task Start(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting Ethereum runner");

        await stepsManager.InitializeAll(cancellationToken);

        string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();

        if (_logger.IsInfo) _logger.Info(infoScreen);
    }

    public async Task StopAsync()
    {
        Stop(() => _api.SessionMonitor?.Stop(), "Stopping session monitor");
        Stop(() => _api.SyncModeSelector?.Stop(), "Stopping session sync mode selector");
        Task discoveryStopTask = Stop(() => _api.DiscoveryApp?.StopAsync(), "Stopping discovery app");
        Task blockProducerTask = Stop(() => _api.BlockProducerRunner?.StopAsync(), "Stopping block producer");
        Task peerPoolTask = Stop(() => _api.PeerPool?.StopAsync(), "Stopping peer pool");
        Task peerManagerTask = Stop(() => _api.PeerManager?.StopAsync(), "Stopping peer manager");
        Task blockchainProcessorTask = Stop(() => _api.MainProcessingContext?.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
        Task rlpxPeerTask = Stop(() => _api.RlpxPeer?.Shutdown(), "Stopping RLPx peer");
        await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await Stop(async () => await plugin.DisposeAsync(), $"Disposing plugin {plugin.Name}");
        }

        await _api.DisposeStack.DisposeAsync();
        Stop(() => _api.DbProvider?.Dispose(), "Closing DBs");

        if (_logger.IsInfo)
        {
            _logger.Info("All DBs closed");
            _logger.Info("Ethereum runner stopped");
        }

        await lifetimeScope.DisposeAsync();
    }

    private void Stop(Action stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);

            stopAction();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
        }
    }

    private Task Stop(Func<Task?> stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);
            return stopAction() ?? Task.CompletedTask;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
            return Task.CompletedTask;
        }
    }
}
