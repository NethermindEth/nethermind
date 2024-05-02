// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core.PubSub;
using Nethermind.JsonRpc.Modules;
using Nethermind.TxPool;

namespace Nethermind.Analytics
{
    public class AnalyticsPlugin : INethermindPlugin
    {
        private readonly IAnalyticsConfig _analyticsConfig;
        private readonly InitConfig _initConfig;
        private IList<IPublisher> _publishers;
        private INethermindApi _api;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "Analytics";

        public string Description => "Various Analytics Extensions";

        public string Author => "Nethermind";
        public bool Enabled => _initConfig.WebSocketsEnabled &&
                    (_analyticsConfig.PluginsEnabled ||
                     _analyticsConfig.StreamBlocks ||
                     _analyticsConfig.StreamTransactions);

        public AnalyticsPlugin(InitConfig initConfig, IAnalyticsConfig analyticsConfig)
        {
            _initConfig = initConfig;
            _analyticsConfig = analyticsConfig;
        }

        public Task Init(INethermindApi api)
        {
            _api = api;
            var (getFromAPi, _) = _api.ForInit;

            IInitConfig initConfig = getFromAPi.Config<IInitConfig>();
            if (!Enabled)
            {
                if (!initConfig.WebSocketsEnabled)
                {
                    getFromAPi.LogManager.GetClassLogger().Warn($"{nameof(AnalyticsPlugin)} disabled due to {nameof(initConfig.WebSocketsEnabled)} set to false");
                }
                else
                {
                    getFromAPi.LogManager.GetClassLogger().Warn($"{nameof(AnalyticsPlugin)} plugin disabled due to {nameof(AnalyticsConfig)} settings set to false");
                }
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

        public Task InitNetworkProtocol()
        {
            var (getFromAPi, _) = _api.ForNetwork;
            if (Enabled)
            {
                getFromAPi.TxPool!.NewDiscovered += TxPoolOnNewDiscovered;
                AnalyticsWebSocketsModule webSocketsModule = new(getFromAPi.EthereumJsonSerializer, getFromAPi.LogManager);
                getFromAPi.WebSocketsManager!.AddModule(webSocketsModule, true);
                getFromAPi.Publishers.Add(webSocketsModule);
            }

            _publishers = getFromAPi.Publishers;

            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            var (getFromAPi, _) = _api.ForRpc;
            AnalyticsRpcModule analyticsRpcModule = new(
                getFromAPi.BlockTree, getFromAPi.StateReader, getFromAPi.LogManager);
            getFromAPi.RpcModuleProvider.Register(new SingletonModulePool<IAnalyticsRpcModule>(analyticsRpcModule));
            return Task.CompletedTask;
        }
    }
}
