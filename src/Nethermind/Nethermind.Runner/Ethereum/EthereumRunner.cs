// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner
    {
        private INethermindApi _api;

        private ILogger _logger;

        public EthereumRunner(INethermindApi api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");

            EthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetStepsAssemblies(_api));
            EthereumStepsManager stepsManager = new EthereumStepsManager(stepsLoader, _api, _api.LogManager);
            await stepsManager.InitializeAll(cancellationToken);

            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }

        private IEnumerable<Assembly> GetStepsAssemblies(INethermindApi api)
        {
            yield return typeof(IStep).Assembly;
            yield return GetType().Assembly;
            IEnumerable<IInitializationPlugin> enabledInitializationPlugins =
                _api.Plugins.OfType<IInitializationPlugin>().Where(p => p.ShouldRunSteps(api));

            foreach (IInitializationPlugin initializationPlugin in enabledInitializationPlugins)
            {
                yield return initializationPlugin.GetType().Assembly;
            }
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

            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                await Stop(async () => await plugin.DisposeAsync(), $"Disposing plugin {plugin.Name}");
            }

            while (_api.DisposeStack.Count != 0)
            {
                IAsyncDisposable disposable = _api.DisposeStack.Pop();
                await Stop(async () => await disposable.DisposeAsync(), $"Disposing {disposable}");
            }

            Stop(() => _api.DbProvider?.Dispose(), "Closing DBs");

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
