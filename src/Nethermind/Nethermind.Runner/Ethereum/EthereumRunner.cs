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
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunner : IRunner
    {
        private EthereumRunnerContext _context = new EthereumRunnerContext();

        public EthereumRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configurationProvider,
            ILogManager logManager, IGrpcServer grpcServer,
            INdmConsumerChannelManager ndmConsumerChannelManager, INdmDataPublisher ndmDataPublisher,
            INdmInitializer ndmInitializer, IWebSocketsManager webSocketsManager,
            IJsonSerializer ethereumJsonSerializer, IMonitoringService monitoringService)
        {
            _context.LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _context._grpcServer = grpcServer;
            _context._ndmConsumerChannelManager = ndmConsumerChannelManager;
            _context._ndmDataPublisher = ndmDataPublisher;
            _context._ndmInitializer = ndmInitializer;
            _context._webSocketsManager = webSocketsManager;
            _context._ethereumJsonSerializer = ethereumJsonSerializer;
            _context._monitoringService = monitoringService;
            _context.Logger = _context.LogManager.GetClassLogger();

            _context._configProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _context._rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            _context._initConfig = configurationProvider.GetConfig<IInitConfig>();
            _context._txPoolConfig = configurationProvider.GetConfig<ITxPoolConfig>();

            _context.NetworkConfig = _context._configProvider.GetConfig<INetworkConfig>();
            _context._ipResolver = new IpResolver(_context.NetworkConfig, _context.LogManager);
            _context.NetworkConfig.ExternalIp = _context._ipResolver.ExternalIp.ToString();
            _context.NetworkConfig.LocalIp = _context._ipResolver.LocalIp.ToString();
        }

        public async Task Start()
        {
            if (_context.Logger.IsDebug) _context.Logger.Debug("Initializing Ethereum");
            _context._runnerCancellation = new CancellationTokenSource();

            EthereumStepsManager stepsManager = new EthereumStepsManager(_context);
            stepsManager.DiscoverAll();
            await stepsManager.InitializeAll();
            
            if (_context.Logger.IsDebug) _context.Logger.Debug("Ethereum initialization completed");
        }

        public async Task StopAsync()
        {
            if (_context.Logger.IsInfo) _context.Logger.Info("Shutting down...");
            _context._runnerCancellation.Cancel();

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping sesison monitor...");
            _context._sessionMonitor?.Stop();

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping discovery app...");
            var discoveryStopTask = _context._discoveryApp?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping block producer...");
            var blockProducerTask = _context._blockProducer?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping sync peer pool...");
            var peerPoolTask = _context._syncPeerPool?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping peer manager...");
            var peerManagerTask = _context.PeerManager?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping synchronizer...");
            var synchronizerTask = (_context._synchronizer?.StopAsync() ?? Task.CompletedTask)
                .ContinueWith(t => _context._synchronizer?.Dispose());

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = (_context._blockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping rlpx peer...");
            var rlpxPeerTask = _context._rlpxPeer?.Shutdown() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

            if (_context.Logger.IsInfo) _context.Logger.Info("Closing DBs...");
            _context._dbProvider.Dispose();
            if (_context.Logger.IsInfo) _context.Logger.Info("All DBs closed.");

            while (_context._disposeStack.Count != 0)
            {
                var disposable = _context._disposeStack.Pop();
                if (_context.Logger.IsDebug) _context.Logger.Debug($"Disposing {disposable.GetType().Name}");
            }

            if (_context.Logger.IsInfo) _context.Logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }
    }
}