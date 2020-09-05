﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner : IRunner
    {
        private NethermindApi _api;

        private ILogger _logger;

        public EthereumRunner(
            IRpcModuleProvider rpcModuleProvider,
            IConfigProvider configurationProvider,
            ILogManager logManager,
            IGrpcServer? grpcServer,
            INdmConsumerChannelManager? ndmConsumerChannelManager,
            INdmDataPublisher? ndmDataPublisher,
            INdmInitializer? ndmInitializer,
            IWebSocketsManager webSocketsManager,
            IJsonSerializer ethereumJsonSerializer,
            IMonitoringService monitoringService)
        {
            _logger = logManager.GetClassLogger();
            _api = new EthereumRunnerContextFactory(configurationProvider, ethereumJsonSerializer, logManager).Context;
            _api.LogManager = logManager;
            _api.GrpcServer = grpcServer;
            _api.NdmConsumerChannelManager = ndmConsumerChannelManager;
            _api.NdmDataPublisher = ndmDataPublisher;
            _api.NdmInitializer = ndmInitializer;
            _api.WebSocketsManager = webSocketsManager;
            _api.EthereumJsonSerializer = ethereumJsonSerializer;
            _api.MonitoringService = monitoringService;

            _api.ConfigProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _api.RpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));

            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            _api.IpResolver = new IPResolver(networkConfig, _api.LogManager);
            networkConfig.ExternalIp = _api.IpResolver.ExternalIp.ToString();
            networkConfig.LocalIp = _api.IpResolver.LocalIp.ToString();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");

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