//  Copyright (c) 2020 Demerzel Solutions Limited
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


using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.PubSub;
using Nethermind.TxPool;

namespace Nethermind.Analytics
{
    public class AnalyticsPlugin : INethermindPlugin
    {
        private IAnalyticsConfig _analyticsConfig;
        private IList<IPublisher> _publishers;
        private IBasicApi _basicApi;

        private bool _isOn;

        public void Dispose() { }

        public string Name => "Analytics";

        public string Description => "Various Analytics Extensions";

        public string Author => "Nethermind";

        public Task Init(IBasicApi api)
        {
            _analyticsConfig = api.Config<IAnalyticsConfig>();
            _basicApi = api;
            
            IInitConfig initConfig = api.Config<IInitConfig>();
            _isOn = initConfig.WebSocketsEnabled &&
                    (_analyticsConfig.PluginsEnabled ||
                     _analyticsConfig.StreamBlocks ||
                     _analyticsConfig.StreamTransactions);
            return Task.CompletedTask;
        }

        public Task InitBlockchain(IBlockchainApi api)
        {
            if (_isOn)
            {
                api.TxPool!.NewDiscovered += TxPoolOnNewDiscovered;
            }
            
            return Task.CompletedTask;
        }

        private void TxPoolOnNewDiscovered(object sender, TxEventArgs e)
        {
            if (_analyticsConfig.StreamTransactions)
            {
                foreach (IPublisher publisher in _publishers)
                {
                    // TODO: probably need to serialize first
                    publisher.PublishAsync(e.Transaction);
                }
            }
        }

        public Task InitNetworkProtocol(INetworkApi api)
        {
            if (_isOn)
            {
                AnalyticsWebSocketsModule webSocketsModule = new AnalyticsWebSocketsModule(_basicApi.EthereumJsonSerializer);
                api.WebSocketsManager!.AddModule(webSocketsModule, true);
                api.Publishers.Add(webSocketsModule);
            }

            _publishers = api.Publishers;

            return Task.CompletedTask;
        }

        public Task InitRpcModules(INethermindApi api)
        {
            AnalyticsModule analyticsModule = new AnalyticsModule(api.BlockTree, api.StateReader, api.LogManager);
            api.RpcModuleProvider.Register(new SingletonModulePool<IAnalyticsModule>(analyticsModule));
            return Task.CompletedTask;
        }
    }
}