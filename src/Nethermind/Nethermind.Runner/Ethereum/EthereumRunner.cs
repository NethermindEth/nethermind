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
using System.Text;
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
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner : IRunner
    {
        private EthereumRunnerContext _context;

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
            _context = new EthereumRunnerContextFactory(configurationProvider, ethereumJsonSerializer, logManager).Context;
            _context.LogManager = logManager;
            _context.GrpcServer = grpcServer;
            _context.NdmConsumerChannelManager = ndmConsumerChannelManager;
            _context.NdmDataPublisher = ndmDataPublisher;
            _context.NdmInitializer = ndmInitializer;
            _context.WebSocketsManager = webSocketsManager;
            _context.EthereumJsonSerializer = ethereumJsonSerializer;
            _context.MonitoringService = monitoringService;

            _context.ConfigProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _context.RpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));

            INetworkConfig networkConfig = _context.Config<INetworkConfig>();
            _context.IpResolver = new IPResolver(networkConfig, _context.LogManager);
            networkConfig.ExternalIp = _context.IpResolver.ExternalIp.ToString();
            networkConfig.LocalIp = _context.IpResolver.LocalIp.ToString();
        }

        public async Task Start()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");
            _context.RunnerCancellation = new CancellationTokenSource();
            _context.DisposeStack.Push(_context.RunnerCancellation);

            EthereumStepsManager stepsManager = new EthereumStepsManager(_context);
            stepsManager.DiscoverAll();
            await stepsManager.InitializeAll();
            
            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Shutting down...");
            _context.RunnerCancellation?.Cancel();

            if (_logger.IsInfo) _logger.Info("Stopping session monitor...");
            _context.SessionMonitor?.Stop();

            if (_logger.IsInfo) _logger.Info("Stopping discovery app...");
            Task discoveryStopTask = _context.DiscoveryApp?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping block producer...");
            Task blockProducerTask = _context.BlockProducer?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping sync peer pool...");
            Task peerPoolTask = _context.SyncPeerPool?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping peer manager...");
            Task peerManagerTask = _context.PeerManager?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping synchronizer...");
            Task synchronizerTask = (_context.Synchronizer?.StopAsync() ?? Task.CompletedTask)
                .ContinueWith(t => _context.Synchronizer?.Dispose());

            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            Task blockchainProcessorTask = (_context.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping rlpx peer...");
            Task rlpxPeerTask = _context.RlpxPeer?.Shutdown() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);
            
            while (_context.DisposeStack.Count != 0)
            {
                IAsyncDisposable disposable = _context.DisposeStack.Pop();
                if (_logger.IsDebug) _logger.Debug($"Disposing {disposable.GetType().Name}");
                await disposable.DisposeAsync();
            }
            
            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _context.DbProvider?.Dispose();
            
            if (_logger.IsInfo) _logger.Info("All DBs closed.");
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }
    }
}