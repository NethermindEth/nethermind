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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeNetwork), typeof(InitializeBlockchain))]
    public class StartEthStatsClient : IStep
    {
        private readonly NethermindApi _api;
        private ILogger _logger;

        public StartEthStatsClient(NethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
        }

        bool IStep.MustInitialize => false;
        
        public async Task Execute(CancellationToken _)
        {
            IEthStatsConfig ethStatsConfig = _api.Config<IEthStatsConfig>();
            if (!ethStatsConfig.Enabled)
            {
                return;
            }
            
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();

            if (_api.Enode == null) throw new StepDependencyException(nameof(_api.Enode));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));

            string instanceId = $"{ethStatsConfig.Name}-{Keccak.Compute(_api.Enode.Info)}";
            if (_logger.IsInfo) _logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {ethStatsConfig.Server}");
            MessageSender sender = new MessageSender(instanceId, _api.LogManager);
            const int reconnectionInterval = 5000;
            const string api = "no";
            const string client = "0.1.1";
            const bool canUpdateHistory = false;
            string node = ClientVersion.Description ?? string.Empty;
            int port = networkConfig.P2PPort;
            string network = _api.SpecProvider.ChainId.ToString();
            string protocol = "eth/65";
            
            EthStatsClient ethStatsClient = new EthStatsClient(
                ethStatsConfig.Server,
                reconnectionInterval,
                sender,
                _api.LogManager);
            
            EthStatsIntegration ethStatsIntegration = new EthStatsIntegration(
                ethStatsConfig.Name,
                node,
                port,
                network,
                protocol,
                api,
                client,
                ethStatsConfig.Contact,
                canUpdateHistory,
                ethStatsConfig.Secret,
                ethStatsClient,
                sender,
                _api.BlockTree,
                _api.PeerManager,
                _api.LogManager);
            
            await ethStatsIntegration.InitAsync();
            _api.DisposeStack.Push(ethStatsIntegration);
            // TODO: handle failure
        }
    }
}