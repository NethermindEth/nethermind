//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner : IRunner
    {
        private INethermindApi _api;

        private ILogger _logger;

        public EthereumRunner(INethermindApi api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger();
            
            // this should be outside of Ethereum Runner I guess
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            _api.IpResolver = new IPResolver(networkConfig, _api.LogManager);
            networkConfig.ExternalIp = _api.IpResolver.ExternalIp.ToString();
            networkConfig.LocalIp = _api.IpResolver.LocalIp.ToString();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");

            // all plugins init
            
            EthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetType().Assembly);
            EthereumStepsManager stepsManager = new EthereumStepsManager(stepsLoader, _api, _api.LogManager);
            await stepsManager.InitializeAll(cancellationToken);

            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }
        
        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Stopping session monitor...");
            _api.SessionMonitor?.Stop();

            if (_logger.IsInfo) _logger.Info("Stopping discovery app...");
            Task discoveryStopTask = _api.DiscoveryApp?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping block producer...");
            Task blockProducerTask = _api.BlockProducer?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping sync peer pool...");
            Task peerPoolTask = _api.SyncPeerPool?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping peer manager...");
            Task peerManagerTask = _api.PeerManager?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping synchronizer...");
            Task synchronizerTask = _api.Synchronizer?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            Task blockchainProcessorTask = (_api.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping rlpx peer...");
            Task rlpxPeerTask = _api.RlpxPeer?.Shutdown() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);
            
            while (_api.DisposeStack.Count != 0)
            {
                IAsyncDisposable disposable = _api.DisposeStack.Pop();
                if (_logger.IsDebug) _logger.Debug($"Disposing {disposable}");
                await disposable.DisposeAsync();
            }
            
            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _api.DbProvider?.Dispose();
            
            if (_logger.IsInfo) _logger.Info("All DBs closed.");
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }
    }
}