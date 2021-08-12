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
// 

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(RegisterRpcModules))]
    public class StartRpc : IStep
    {
        private readonly INethermindApi _api;

        public StartRpc(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            ILogger logger = _api.LogManager.GetClassLogger();

            if (jsonRpcConfig.Enabled)
            {
                IInitConfig initConfig = _api.Config<IInitConfig>();
                JsonRpcLocalStats jsonRpcLocalStats = new(
                    _api.Timestamper,
                    jsonRpcConfig,
                    _api.LogManager);

                JsonRpcService jsonRpcService = new(_api.RpcModuleProvider, _api.LogManager);

                JsonRpcProcessor jsonRpcProcessor = new(
                    jsonRpcService,
                    _api.EthereumJsonSerializer,
                    jsonRpcConfig,
                    _api.FileSystem,
                    _api.LogManager);

                if (initConfig.WebSocketsEnabled)
                {
                    JsonRpcWebSocketsModule webSocketsModule = new (jsonRpcProcessor, jsonRpcService, jsonRpcLocalStats, _api.LogManager, _api.EthereumJsonSerializer);
                    _api.WebSocketsManager!.AddModule(webSocketsModule, true);
                }

                Bootstrap.Instance.JsonRpcService = jsonRpcService;
                Bootstrap.Instance.LogManager = _api.LogManager;
                Bootstrap.Instance.JsonSerializer = _api.EthereumJsonSerializer;
                Bootstrap.Instance.JsonRpcLocalStats = jsonRpcLocalStats;
                JsonRpcRunner? jsonRpcRunner = new(
                    jsonRpcProcessor,
                    _api.WebSocketsManager!,
                    _api.ConfigProvider,
                    _api.LogManager,
                    _api);

                await jsonRpcRunner.Start(cancellationToken).ContinueWith(x =>
                {
                    if (x.IsFaulted && logger.IsError)
                        logger.Error("Error during jsonRpc runner start", x.Exception);
                }, cancellationToken);

                JsonRpcIpcRunner jsonIpcRunner = new(jsonRpcProcessor, jsonRpcService, _api.ConfigProvider, _api.LogManager, jsonRpcLocalStats, _api.EthereumJsonSerializer);
                jsonIpcRunner.Start(cancellationToken);

#pragma warning disable 4014
                _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => jsonRpcRunner.StopAsync())); // do not await
                _api.DisposeStack.Push(jsonIpcRunner); // do not await
#pragma warning restore 4014
            }
            else
            {
                if (logger.IsInfo) logger.Info("Json RPC is disabled");
            }
        }
    }
}
