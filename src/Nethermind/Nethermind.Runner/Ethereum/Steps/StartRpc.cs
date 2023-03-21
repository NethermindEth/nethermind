// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;

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
                IJsonRpcUrlCollection jsonRpcUrlCollection = new JsonRpcUrlCollection(_api.LogManager, jsonRpcConfig, initConfig.WebSocketsEnabled);

                IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider!;
                JsonRpcService jsonRpcService = new(rpcModuleProvider, _api.LogManager, jsonRpcConfig);

                IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
                IRpcAuthentication auth = jsonRpcConfig.UnsecureDevNoRpcAuthentication || !jsonRpcUrlCollection.Values.Any(u => u.IsAuthenticated)
                    ? NoAuthentication.Instance
                    : JwtAuthentication.FromFile(jsonRpcConfig.JwtSecretFile, _api.Timestamper, logger);


                JsonRpcProcessor jsonRpcProcessor = new(
                    jsonRpcService,
                    jsonRpcConfig,
                    _api.FileSystem,
                    _api.LogManager);


                if (initConfig.WebSocketsEnabled)
                {
                    JsonRpcWebSocketsModule webSocketsModule = new(
                        jsonRpcProcessor,
                        jsonRpcService,
                        _api.JsonRpcLocalStats!,
                        _api.LogManager,
                        jsonSerializer,
                        jsonRpcUrlCollection,
                        auth,
                        jsonRpcConfig.MaxBatchResponseBodySize);

                    _api.WebSocketsManager!.AddModule(webSocketsModule, true);
                }

                Bootstrap.Instance.JsonRpcService = jsonRpcService;
                Bootstrap.Instance.LogManager = _api.LogManager;
                Bootstrap.Instance.JsonSerializer = jsonSerializer;
                Bootstrap.Instance.JsonRpcLocalStats = _api.JsonRpcLocalStats!;
                Bootstrap.Instance.JsonRpcAuthentication = auth;

                JsonRpcRunner? jsonRpcRunner = new(
                    jsonRpcProcessor,
                    jsonRpcUrlCollection,
                    _api.WebSocketsManager!,
                    _api.ConfigProvider,
                    auth,
                    _api.LogManager,
                    _api);

                await jsonRpcRunner.Start(cancellationToken).ContinueWith(x =>
                {
                    if (x.IsFaulted && logger.IsError)
                        logger.Error("Error during jsonRpc runner start", x.Exception);
                }, cancellationToken);

                JsonRpcIpcRunner jsonIpcRunner = new(jsonRpcProcessor, jsonRpcService, _api.ConfigProvider,
                    _api.LogManager, _api.JsonRpcLocalStats!, jsonSerializer, _api.FileSystem);
                jsonIpcRunner.Start(cancellationToken);

#pragma warning disable 4014
                _api.DisposeStack.Push(
                    new Reactive.AnonymousDisposable(() => jsonRpcRunner.StopAsync())); // do not await
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
