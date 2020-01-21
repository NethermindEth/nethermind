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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Runner.Ethereum.Subsystems;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(LoadChainspec))]
    public class StartEthStatsClient : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public StartEthStatsClient(EthereumRunnerContext context)
        {
            _context = context;
            
            EthereumSubsystemState newState = context.Config<IEthStatsConfig>().Enabled
                ? EthereumSubsystemState.AwaitingInitialization
                : EthereumSubsystemState.Disabled;

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(newState));
        }

        public async Task Execute()
        {
            IEthStatsConfig ethStatsConfig = _context.Config<IEthStatsConfig>();
            if (!ethStatsConfig.Enabled)
            {
                return;
            }
            
            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Initializing));

            string instanceId = $"{ethStatsConfig.Name}-{Keccak.Compute(_context.Enode.Info)}";
            if (_context.Logger.IsInfo) _context.Logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {ethStatsConfig.Server}");
            MessageSender sender = new MessageSender(instanceId, _context.LogManager);
            const int reconnectionInterval = 5000;
            const string api = "no";
            const string client = "0.1.1";
            const bool canUpdateHistory = false;
            string node = ClientVersion.Description;
            int port = _context.NetworkConfig.P2PPort;
            string network = _context.SpecProvider.ChainId.ToString();
            string protocol = _context.Config<ISyncConfig>().FastSync ? "eth/63" : "eth/62";
            
            EthStatsClient ethStatsClient = new EthStatsClient(
                ethStatsConfig.Server,
                reconnectionInterval,
                sender,
                _context.LogManager);
            
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
                _context.BlockTree,
                _context.PeerManager,
                _context.LogManager);
            
            await ethStatsIntegration.InitAsync();
            
            // TODO: handle failure
            
            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
        }

        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;
        
        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.EthStats;
    }
}