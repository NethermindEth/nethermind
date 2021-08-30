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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeNetwork), typeof(InitializeBlockchain))]
    public class StartEthStatsClient : IStep
    {
        private readonly IApiWithNetwork _get;
        private ILogger _logger;

        public StartEthStatsClient(INethermindApi api)
        {
            _get = api;
            _logger = _get.LogManager.GetClassLogger();
        }

        bool IStep.MustInitialize => false;
        
        public async Task Execute(CancellationToken _)
        {
            IEthStatsConfig ethStatsConfig = _get.Config<IEthStatsConfig>();
            if (!ethStatsConfig.Enabled)
            {
                return;
            }
            
            INetworkConfig networkConfig = _get.Config<INetworkConfig>();

            if (_get.Enode == null) throw new StepDependencyException(nameof(_get.Enode));
            if (_get.SpecProvider == null) throw new StepDependencyException(nameof(_get.SpecProvider));

            string instanceId = $"{ethStatsConfig.Name}-{Keccak.Compute(_get.Enode.Info)}";
            if (_logger.IsInfo) _logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {ethStatsConfig.Server}");
            MessageSender sender = new MessageSender(instanceId, _get.LogManager);
            const int reconnectionInterval = 5000;
            const string api = "no";
            const string client = "0.1.1";
            const bool canUpdateHistory = false;
            string node = ClientVersion.Description;
            int port = networkConfig.P2PPort;
            string network = _get.SpecProvider.ChainId.ToString();
            string protocol = "eth/65";
            
            EthStatsClient ethStatsClient = new(
                ethStatsConfig.Server,
                reconnectionInterval,
                sender,
                _get.LogManager);
            
            EthStatsIntegration ethStatsIntegration = new(
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
                _get.TxPool,
                _get.BlockTree,
                _get.PeerManager,
                _get.LogManager);
            
            await ethStatsIntegration.InitAsync();
            _get.DisposeStack.Push(ethStatsIntegration);
            // TODO: handle failure
        }
    }
}
