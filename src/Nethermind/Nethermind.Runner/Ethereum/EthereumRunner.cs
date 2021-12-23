//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Steps;

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

            EthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetStepsAssemblies());
            EthereumStepsManager stepsManager = new EthereumStepsManager(stepsLoader, _api, _api.LogManager);
            await stepsManager.InitializeAll(cancellationToken);

            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }

        private IEnumerable<Assembly> GetStepsAssemblies()
        {
            yield return typeof(IStep).Assembly;
            yield return GetType().Assembly;
            foreach (IConsensusPlugin consensus in _api.Plugins.OfType<IConsensusPlugin>())
            {
                yield return consensus.GetType().Assembly;
            }
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Persisting trie...");
            _api.TrieStore?.HackPersistOnShutdown();
            
            Stop(() => _api.SessionMonitor?.Stop(), "Stopping session monitor");
            Task discoveryStopTask = Stop(() => _api.DiscoveryApp?.StopAsync(), "Stopping discovery app");
            Task blockProducerTask = Stop(() => _api.BlockProducer?.StopAsync(), "Stopping block producer");
            Task peerPoolTask = Stop(() => _api.SyncPeerPool?.StopAsync(), "Stopping sync peer pool");
            Task peerManagerTask = Stop(() => _api.PeerManager?.StopAsync(), "Stopping peer manager");
            Task synchronizerTask = Stop(() => _api.Synchronizer?.StopAsync(), "Stopping synchronizer");
            Task blockchainProcessorTask = Stop(() => _api.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
            Task rlpxPeerTask = Stop(() => _api.RlpxPeer?.Shutdown(), "Stopping rlpx peer");
            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

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
        
        private ValueTask Stop(Func<ValueTask?> stopAction, string description)
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"{description}...");
                return stopAction() ?? default;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
                return default;
            }
        }
    }
}
