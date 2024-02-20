// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner
    {
        private readonly INethermindApi _api;
        private readonly ILogger _logger;
        private readonly EthereumStepsManager _stepManager;

        public EthereumRunner(INethermindApi api, EthereumStepsManager stepsManager, ILogger logger)
        {
            _api = api;
            _stepManager = stepsManager;
            _logger = logger;
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");

            await _stepManager.InitializeAll(cancellationToken);

            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }

        public async Task StopAsync()
        {
            Stop(() => _api.SessionMonitor?.Stop(), "Stopping session monitor");
            Stop(() => _api.SyncModeSelector?.Stop(), "Stopping session sync mode selector");
            Task discoveryStopTask = Stop(() => _api.DiscoveryApp?.StopAsync(), "Stopping discovery app");
            Task blockProducerTask = Stop(() => _api.BlockProducer?.StopAsync(), "Stopping block producer");
            Task syncPeerPoolTask = Stop(() => _api.SyncPeerPool?.StopAsync(), "Stopping sync peer pool");
            Task peerPoolTask = Stop(() => _api.PeerPool?.StopAsync(), "Stopping peer pool");
            Task peerManagerTask = Stop(() => _api.PeerManager?.StopAsync(), "Stopping peer manager");
            Task synchronizerTask = Stop(() => _api.Synchronizer?.StopAsync(), "Stopping synchronizer");
            Task blockchainProcessorTask = Stop(() => _api.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
            Task rlpxPeerTask = Stop(() => _api.RlpxPeer?.Shutdown(), "Stopping rlpx peer");
            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, syncPeerPoolTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

            while (_api.DisposeStack.Count != 0)
            {
                IAsyncDisposable disposable = _api.DisposeStack.Pop();
                await Stop(async () => await disposable.DisposeAsync(), $"Disposing {disposable}");
            }

            if (_logger.IsInfo) _logger.Info("All DBs closed.");
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void Stop(Action stopAction, string description)
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"{description}...");
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
                if (_logger.IsInfo) _logger.Info($"{description}...");
                return stopAction() ?? Task.CompletedTask;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
                return Task.CompletedTask;
            }
        }
    }
}
