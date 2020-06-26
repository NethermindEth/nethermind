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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NLog;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunnerContextFactory
    {
        private readonly IConfigProvider _configProvider;
        private readonly ILogManager _logManager;

        public EthereumRunnerContextFactory(IConfigProvider configProvider, IJsonSerializer ethereumJsonSerializer, ILogManager logManager)
        {
            _configProvider = configProvider;
            _logManager = logManager;
            
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            ILogger logger = _logManager.GetClassLogger();

            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";
            bool hiveChainSpecExists = File.Exists(initConfig.HiveChainSpecPath);

            string chainSpecFile;
            if(hiveEnabled && hiveChainSpecExists)
                chainSpecFile = initConfig.HiveChainSpecPath;
            else
                chainSpecFile = initConfig.ChainSpecPath;

            if (logger.IsDebug) logger.Debug($"Loading chain spec from {chainSpecFile}");

            ThisNodeInfo.AddInfo("Chainspec    :", $"{chainSpecFile}");
            IChainSpecLoader loader = new ChainSpecLoader(ethereumJsonSerializer);

            ChainSpec chainSpec = loader.LoadFromFile(chainSpecFile);
            
            logManager.SetGlobalVariable("chain", chainSpec.Name);
            logManager.SetGlobalVariable("chainId", chainSpec.ChainId);
            logManager.SetGlobalVariable("engine", chainSpec.SealEngineType);

            Context = Create(chainSpec.SealEngineType);
            Context.ChainSpec = chainSpec;
            Context.SpecProvider = new ChainSpecBasedSpecProvider(Context.ChainSpec);
        }

        private EthereumRunnerContext Create(SealEngineType engine)
        {
            switch (engine)
            {
                case SealEngineType.Ethash:
                    return new EthashEthereumRunnerContext(_configProvider, _logManager);
                case SealEngineType.AuRa:
                    return new AuRaEthereumRunnerContext(_configProvider, _logManager);
                case SealEngineType.Clique:
                    return new CliqueEthereumRunnerContext(_configProvider, _logManager);
                case SealEngineType.NethDev:
                    return new NethDevEthereumRunnerContext(_configProvider, _logManager);
                case SealEngineType.None:
                    return new EthereumRunnerContext(_configProvider, _logManager);
                default:
                    throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unexpected engine.");
            }
        }

        public EthereumRunnerContext Context { get; }
    }
}