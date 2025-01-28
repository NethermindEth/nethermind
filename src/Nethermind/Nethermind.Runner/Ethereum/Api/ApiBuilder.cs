// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;
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

    public ApiBuilder(IConfigProvider configProvider, ILogManager logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = _logManager.GetClassLogger();
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _initConfig = configProvider.GetConfig<IInitConfig>();
        _jsonSerializer = new EthereumJsonSerializer();
    }

    public INethermindApi Create(params IConsensusPlugin[] consensusPlugins) =>
        Create((IEnumerable<IConsensusPlugin>)consensusPlugins);

    public INethermindApi Create(IEnumerable<IConsensusPlugin> consensusPlugins)
    {
        ChainSpec chainSpec = LoadChainSpec(_jsonSerializer);
        bool wasCreated = Interlocked.CompareExchange(ref _apiCreated, 1, 0) == 1;
        if (wasCreated)
        {
            throw new NotSupportedException("Creation of multiple APIs not supported.");
        }

        string engine = chainSpec.SealEngineType;
        IConsensusPlugin? enginePlugin = consensusPlugins.FirstOrDefault(p => p.SealEngineType == engine);

        INethermindApi nethermindApi =
            enginePlugin?.CreateApi(_configProvider, _jsonSerializer, _logManager, chainSpec) ??
            new NethermindApi(_configProvider, _jsonSerializer, _logManager, chainSpec);
        nethermindApi.SealEngineType = engine;
        nethermindApi.SpecProvider = new ChainSpecBasedSpecProvider(chainSpec, _logManager);
        nethermindApi.GasLimitCalculator = new FollowOtherMiners(nethermindApi.SpecProvider);

        SetLoggerVariables(chainSpec);

        return nethermindApi;
    }

    private int _apiCreated;

    private ChainSpec LoadChainSpec(IJsonSerializer ethereumJsonSerializer)
    {
        if (_logger.IsDebug) _logger.Debug($"Loading chain spec from {_initConfig.ChainSpecPath}");

        ThisNodeInfo.AddInfo("Chainspec    :", _initConfig.ChainSpecPath);

        IChainSpecLoader loader = new ChainSpecLoader(ethereumJsonSerializer);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(_initConfig.ChainSpecPath, _logger);
        return chainSpec;
    }

    private void SetLoggerVariables(ChainSpec chainSpec)
    {
        _logManager.SetGlobalVariable("chain", chainSpec.Name);
        _logManager.SetGlobalVariable("chainId", chainSpec.ChainId);
        _logManager.SetGlobalVariable("engine", chainSpec.SealEngineType);
    }
}
