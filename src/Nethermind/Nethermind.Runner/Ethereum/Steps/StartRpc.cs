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
using Nethermind.Sockets;

namespace Nethermind.Runner.Ethereum.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(RegisterRpcModules), typeof(RegisterPluginRpcModules))]
public class StartRpc(INethermindApi api, IJsonRpcServiceConfigurer[] serviceConfigurers, IWebSocketsManager webSocketsManager) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        IJsonRpcConfig jsonRpcConfig = api.Config<IJsonRpcConfig>();
        IKeyStoreConfig keyStoreConfig = api.Config<IKeyStoreConfig>();
        ILogger logger = api.LogManager.GetClassLogger();

        if (string.IsNullOrEmpty(jsonRpcConfig.JwtSecretFile))
            ConfigureJwtSecret(keyStoreConfig, jsonRpcConfig, logger);

        if (!jsonRpcConfig.Enabled)
        {
            if (logger.IsInfo) logger.Info("Json RPC is disabled");
            return;
        }

        IInitConfig initConfig = api.Config<IInitConfig>();
        IJsonRpcUrlCollection jsonRpcUrlCollection =
            new JsonRpcUrlCollection(api.LogManager, jsonRpcConfig, initConfig.WebSocketsEnabled);

        IRpcModuleProvider rpcModuleProvider = api.RpcModuleProvider!;

        JsonRpcService jsonRpcService = new(rpcModuleProvider, api.LogManager, jsonRpcConfig);
        IRpcAuthentication auth =
            jsonRpcConfig.UnsecureDevNoRpcAuthentication || !jsonRpcUrlCollection.Values.Any(u => u.IsAuthenticated)
                ? NoAuthentication.Instance
                : JwtAuthentication.FromFile(jsonRpcConfig.JwtSecretFile, api.Timestamper, logger);

        JsonRpcProcessor jsonRpcProcessor = new(
            jsonRpcService,
            jsonRpcConfig,
            api.FileSystem,
            api.LogManager,
            api.ProcessExit);

        if (initConfig.WebSocketsEnabled)
        {
            JsonRpcWebSocketsModule webSocketsModule = new(
                jsonRpcProcessor,
                jsonRpcService,
                api.JsonRpcLocalStats!,
                api.LogManager,
                api.EthereumJsonSerializer,
                jsonRpcUrlCollection,
                auth,
                jsonRpcConfig.MaxBatchResponseBodySize,
                jsonRpcConfig.WebSocketsProcessingConcurrency);

            webSocketsManager!.AddModule(webSocketsModule, true);
        }

        Bootstrap.Instance.JsonRpcService = jsonRpcService;
        Bootstrap.Instance.LogManager = api.LogManager;
        Bootstrap.Instance.JsonSerializer = api.EthereumJsonSerializer;
        Bootstrap.Instance.JsonRpcLocalStats = api.JsonRpcLocalStats!;
        Bootstrap.Instance.JsonRpcAuthentication = auth;

        JsonRpcRunner? jsonRpcRunner = new(
            jsonRpcProcessor,
            jsonRpcUrlCollection,
            webSocketsManager!,
            api.ConfigProvider,
            auth,
            api.LogManager,
            serviceConfigurers);

        await jsonRpcRunner.Start(cancellationToken).ContinueWith(x =>
        {
            if (x.IsFaulted && logger.IsError)
                logger.Error("Error during jsonRpc runner start", x.Exception);
        }, cancellationToken);

        JsonRpcIpcRunner jsonIpcRunner = new(jsonRpcProcessor, api.ConfigProvider,
            api.LogManager, api.JsonRpcLocalStats!, api.EthereumJsonSerializer, api.FileSystem);
        jsonIpcRunner.Start(cancellationToken);

#pragma warning disable 4014
        api.DisposeStack.Push(
            new Reactive.AnonymousDisposable(() => jsonRpcRunner.StopAsync())); // do not await
        api.DisposeStack.Push(jsonIpcRunner); // do not await
#pragma warning restore 4014
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
