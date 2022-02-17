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

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Configs;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;

namespace Nethermind.EthStats
{
    public class EthStatsPlugin : INethermindPlugin
    {
        private IEthStatsConfig _ethStatsConfig = null!;
        private IEthStatsClient _ethStatsClient = null!;
        private IEthStatsIntegration _ethStatsIntegration = null!;
        private INethermindApi _api = null!;

        private bool _isOn;

        public string Name => "EthStats";
        public string Description => "Ethereum Statistics";
        public string Author => "Nethermind";

        public ValueTask DisposeAsync()
        {
            _ethStatsIntegration.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            var (getFromAPi, _) = _api.ForInit;
            _ethStatsConfig = getFromAPi.Config<IEthStatsConfig>();

            IInitConfig initConfig = getFromAPi.Config<IInitConfig>();
            _isOn = initConfig.WebSocketsEnabled &&
                    _ethStatsConfig.Enabled;

            if (!_isOn)
            {
                if (!initConfig.WebSocketsEnabled)
                {
                    getFromAPi.LogManager.GetClassLogger().Warn($"{nameof(EthStatsPlugin)} disabled due to {nameof(initConfig.WebSocketsEnabled)} set to false");
                }
                else
                {
                    getFromAPi.LogManager.GetClassLogger().Warn($"{nameof(EthStatsPlugin)} plugin disabled due to {nameof(EthStatsConfig)} settings set to false");
                }
            }

            return Task.CompletedTask;
        }

        public async Task InitNetworkProtocol()
        {
            var (getFromAPi, _) = _api.ForNetwork;
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();

            if (_isOn)
            {
                string instanceId = $"{_ethStatsConfig.Name}-{Keccak.Compute(getFromAPi.Enode!.Info)}";
                if (_api.LogManager.GetClassLogger().IsInfo)
                {
                    _api.LogManager.GetClassLogger().Info($"Initializing ETH Stats for the instance: {instanceId}, server: {_ethStatsConfig.Server}");
                }
                MessageSender sender = new(instanceId, _api.LogManager);
                const int reconnectionInterval = 5000;
                const string api = "no";
                const string client = "0.1.1";
                const bool canUpdateHistory = false;
                string node = ClientVersion.Description;
                int port = networkConfig.P2PPort;
                string network = _api.SpecProvider!.ChainId.ToString();
                string protocol = $"{P2PProtocolInfoProvider.DefaultCapabilitiesToString()}";

                _ethStatsClient = new EthStatsClient(
                    _ethStatsConfig.Server,
                    reconnectionInterval,
                    sender,
                    _api.LogManager);

                _ethStatsIntegration = new EthStatsIntegration(
                    _ethStatsConfig.Name!,
                    node,
                    port,
                    network,
                    protocol,
                    api,
                    client,
                    _ethStatsConfig.Contact!,
                    canUpdateHistory,
                    _ethStatsConfig.Secret!,
                    _ethStatsClient,
                    sender,
                    getFromAPi.TxPool,
                    getFromAPi.BlockTree,
                    getFromAPi.PeerManager,
                    getFromAPi.GasPriceOracle,
                    getFromAPi.EthSyncingInfo!,
                    initConfig.IsMining,
                    getFromAPi.LogManager);

                await _ethStatsIntegration.InitAsync();
            }
        }

        public Task InitRpcModules() => Task.CompletedTask;
    }
}
