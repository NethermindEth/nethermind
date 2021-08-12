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

using System;
using System.Collections.Generic;
using System.IO;
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
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner.Ethereum.Api
{
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
            Create((IEnumerable<IConsensusPlugin>) consensusPlugins);

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
            
            INethermindApi nethermindApi = enginePlugin?.CreateApi() ?? new NethermindApi();
            nethermindApi.ConfigProvider = _configProvider;
            nethermindApi.EthereumJsonSerializer = _jsonSerializer;
            nethermindApi.LogManager = _logManager;
            nethermindApi.SealEngineType = engine;
            nethermindApi.SpecProvider = new ChainSpecBasedSpecProvider(chainSpec);
            nethermindApi.GasLimitCalculator = new FollowOtherMiners(nethermindApi.SpecProvider);
            nethermindApi.ChainSpec = chainSpec;

            SetLoggerVariables(chainSpec);

            return nethermindApi;
        }

        private int _apiCreated;

        private ChainSpec LoadChainSpec(IJsonSerializer ethereumJsonSerializer)
        {
            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";
            bool hiveChainSpecExists = File.Exists(_initConfig.HiveChainSpecPath);

            string chainSpecFile;
            if (hiveEnabled && hiveChainSpecExists)
                chainSpecFile = _initConfig.HiveChainSpecPath;
            else
                chainSpecFile = _initConfig.ChainSpecPath;

            if (_logger.IsDebug) _logger.Debug($"Loading chain spec from {chainSpecFile}");

            ThisNodeInfo.AddInfo("Chainspec    :", $"{chainSpecFile}");

            IChainSpecLoader loader = new ChainSpecLoader(ethereumJsonSerializer);
            return loader.LoadFromFile(chainSpecFile);
        }

        private void SetLoggerVariables(ChainSpec chainSpec)
        {
            _logManager.SetGlobalVariable("chain", chainSpec.Name);
            _logManager.SetGlobalVariable("chainId", chainSpec.ChainId);
            _logManager.SetGlobalVariable("engine", chainSpec.SealEngineType);
        }
    }
}
