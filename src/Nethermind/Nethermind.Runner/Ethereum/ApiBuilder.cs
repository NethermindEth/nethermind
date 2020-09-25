//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.IO;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner.Ethereum
{
    public class ApiBuilder
    {
        private readonly IConfigProvider _configProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IInitConfig _initConfig;

        public ApiBuilder(
            IConfigProvider configProvider,
            IJsonSerializer ethereumJsonSerializer,
            ILogManager logManager)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _jsonSerializer = ethereumJsonSerializer ?? throw new ArgumentNullException(nameof(ethereumJsonSerializer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            _initConfig = configProvider.GetConfig<IInitConfig>();
            _logger = _logManager.GetClassLogger();
        }

        public INethermindApi Create()
        {
            ChainSpec chainSpec = LoadChainSpec(_jsonSerializer);
            bool wasCreated = Interlocked.CompareExchange(ref _apiCreated, 1, 0) == 1;
            if (wasCreated)
            {
                throw new NotSupportedException("Creation of multiple APIs not supported.");
            }
            
            SealEngineType engine = chainSpec.SealEngineType;
            NethermindApi nethermindApi = engine switch
            {
                SealEngineType.Ethash => new EthashNethermindApi(_configProvider, _logManager),
                SealEngineType.AuRa => new AuRaNethermindApi(_configProvider, _logManager),
                SealEngineType.Clique => new CliqueNethermindApi(_configProvider, _logManager),
                SealEngineType.NethDev => new NethDevNethermindApi(_configProvider, _logManager),
                SealEngineType.None => new NethermindApi(_configProvider, _logManager),
                _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unexpected engine.")
            };

            nethermindApi.SpecProvider = new ChainSpecBasedSpecProvider(chainSpec);
            nethermindApi.ChainSpec = chainSpec;
            
            SetLoggerVariables(chainSpec);
            
            return nethermindApi;
        }
        
        private static int _apiCreated;
        
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