// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

using Autofac;

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Api;

public class ApiBuilder
{
    private readonly IConfigProvider _configProvider;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly IInitConfig _initConfig;
    private readonly IProcessExitSource _processExitSource;
    public ChainSpec ChainSpec { get; }

    private int _apiCreated;

    public ApiBuilder(IProcessExitSource processExitSource, IConfigProvider configProvider, ILogManager logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = _logManager.GetClassLogger();
        _processExitSource = processExitSource;
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _initConfig = configProvider.GetConfig<IInitConfig>();
        _jsonSerializer = new EthereumJsonSerializer(configProvider.GetConfig<IJsonRpcConfig>().JsonSerializationMaxDepth);
        ChainSpec = LoadChainSpec(_jsonSerializer);
    }

    public EthereumRunner CreateEthereumRunner(IEnumerable<INethermindPlugin> plugins)
    {
        bool wasCreated = Interlocked.CompareExchange(ref _apiCreated, 1, 0) == 1;
        if (wasCreated)
        {
            throw new NotSupportedException("Creation of multiple APIs not supported.");
        }

        ContainerBuilder containerBuilder = new ContainerBuilder()
            .AddModule(new NethermindRunnerModule(
                _jsonSerializer,
                ChainSpec,
                _configProvider,
                _processExitSource,
                plugins,
                _logManager));

        IContainer container = containerBuilder.Build();
        SetLoggerVariables(ChainSpec);
        return container.Resolve<EthereumRunner>();
    }

    private ChainSpec LoadChainSpec(IJsonSerializer ethereumJsonSerializer)
    {
        if (_logger.IsDebug) _logger.Debug($"Loading chain spec from {_initConfig.ChainSpecPath}");

        ThisNodeInfo.AddInfo("Chainspec    :", _initConfig.ChainSpecPath);

        var loader = new ChainSpecFileLoader(ethereumJsonSerializer, _logger);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(_initConfig.ChainSpecPath);
        return chainSpec;
    }

    private void SetLoggerVariables(ChainSpec chainSpec)
    {
        _logManager.SetGlobalVariable("chain", chainSpec.Name);
        _logManager.SetGlobalVariable("chainId", chainSpec.ChainId);
        _logManager.SetGlobalVariable("engine", chainSpec.SealEngineType);
    }
}
