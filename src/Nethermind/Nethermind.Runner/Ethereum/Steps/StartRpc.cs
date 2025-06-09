// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.KeyStore.Config;

namespace Nethermind.Runner.Ethereum.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(RegisterRpcModules), typeof(RegisterPluginRpcModules))]
public class StartRpc(INethermindApi api, IJsonRpcServiceConfigurer[] serviceConfigurers) : IStep
{
    private readonly INethermindApi _api = api;

    public async Task Execute(CancellationToken cancellationToken)
    {
        IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
        IKeyStoreConfig keyStoreConfig = _api.Config<IKeyStoreConfig>();
        ILogger logger = _api.LogManager.GetClassLogger();

        if (string.IsNullOrEmpty(jsonRpcConfig.JwtSecretFile))
            ConfigureJwtSecret(keyStoreConfig, jsonRpcConfig, logger);

        if (jsonRpcConfig.Enabled)
        {
            IInitConfig initConfig = _api.Config<IInitConfig>();
            IJsonRpcUrlCollection jsonRpcUrlCollection = new JsonRpcUrlCollection(_api.LogManager, jsonRpcConfig, initConfig.WebSocketsEnabled);

            IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider!;
            JsonRpcService jsonRpcService = new(rpcModuleProvider, _api.LogManager, jsonRpcConfig);

            IRpcAuthentication auth = jsonRpcConfig.UnsecureDevNoRpcAuthentication || !jsonRpcUrlCollection.Values.Any(u => u.IsAuthenticated)
                ? NoAuthentication.Instance
                : JwtAuthentication.FromFile(jsonRpcConfig.JwtSecretFile, _api.Timestamper, logger);

            JsonRpcProcessor jsonRpcProcessor = new(
                jsonRpcService,
                jsonRpcConfig,
                _api.FileSystem,
                _api.LogManager,
                _api.ProcessExit);

            if (initConfig.WebSocketsEnabled)
            {
                JsonRpcWebSocketsModule webSocketsModule = new(
                    jsonRpcProcessor,
                    jsonRpcService,
                    _api.JsonRpcLocalStats!,
                    _api.LogManager,
                    _api.EthereumJsonSerializer,
                    jsonRpcUrlCollection,
                    auth,
                    jsonRpcConfig.MaxBatchResponseBodySize,
                    jsonRpcConfig.WebSocketsProcessingConcurrency);

                _api.WebSocketsManager!.AddModule(webSocketsModule, true);
            }

            Bootstrap.Instance.JsonRpcService = jsonRpcService;
            Bootstrap.Instance.LogManager = _api.LogManager;
            Bootstrap.Instance.JsonSerializer = _api.EthereumJsonSerializer;
            Bootstrap.Instance.JsonRpcLocalStats = _api.JsonRpcLocalStats!;
            Bootstrap.Instance.JsonRpcAuthentication = auth;

            JsonRpcRunner? jsonRpcRunner = new(
                jsonRpcProcessor,
                jsonRpcUrlCollection,
                _api.WebSocketsManager!,
                _api.ConfigProvider,
                auth,
                _api.LogManager,
                serviceConfigurers);

            await jsonRpcRunner.Start(cancellationToken).ContinueWith(x =>
            {
                if (x.IsFaulted && logger.IsError)
                    logger.Error("Error during jsonRpc runner start", x.Exception);
            }, cancellationToken);

            JsonRpcIpcRunner jsonIpcRunner = new(jsonRpcProcessor, _api.ConfigProvider,
                _api.LogManager, _api.JsonRpcLocalStats!, _api.EthereumJsonSerializer, _api.FileSystem);
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
    private static void ConfigureJwtSecret(IKeyStoreConfig keyStoreConfig, IJsonRpcConfig jsonRpcConfig, ILogger logger)
    {
        string newPath = Path.GetFullPath(Path.Join(keyStoreConfig.KeyStoreDirectory, "jwt-secret"));
        string oldPath = Path.GetFullPath("keystore/jwt-secret");
        jsonRpcConfig.JwtSecretFile = newPath;

        // check if jwt-secret file already exists in previous default directory
        if (!File.Exists(newPath) && File.Exists(oldPath))
        {
            try
            {
                File.Move(oldPath, newPath);

                if (logger.IsWarn) logger.Warn($"Moved JWT secret from {oldPath} to {newPath}");
            }
            catch (Exception ex)
            {
                if (logger.IsError) logger.Error($"Failed moving JWT secret to {newPath}.", ex);

                jsonRpcConfig.JwtSecretFile = oldPath;
            }
        }
    }
}
