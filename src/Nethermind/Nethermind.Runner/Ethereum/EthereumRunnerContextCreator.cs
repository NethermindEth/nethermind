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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunnerContextCreator
    {
        public EthereumRunnerContextCreator(IConfigProvider configProvider, IJsonSerializer ethereumJsonSerializer,   ILogger logger)
        {
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            if (logger.IsInfo) logger.Info($"Loading chain spec from {initConfig.ChainSpecPath}");
            IChainSpecLoader loader = new ChainSpecLoader(ethereumJsonSerializer);
            var chainSpec = loader.LoadFromFile(initConfig.ChainSpecPath);

            Context = CreateEthereumRunnerContext(chainSpec.SealEngineType);
            Context.ChainSpec = chainSpec;
            Context.SpecProvider = new ChainSpecBasedSpecProvider(Context.ChainSpec);
        }

        private EthereumRunnerContext CreateEthereumRunnerContext(SealEngineType engine)
        {
            switch (engine)
            {
                case SealEngineType.Ethash:
                    return new EthashEthereumRunnerContext();
                case SealEngineType.AuRa:
                    return new AuRaEthereumRunnerContext();
                case SealEngineType.Clique:
                    return new CliqueEthereumRunnerContext();
                case SealEngineType.NethDev:
                    return new NethDevEthereumRunnerContext();
                case SealEngineType.None:
                    return new EthereumRunnerContext();
                default:
                    throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unexpected engine.");
            }
        }

        public EthereumRunnerContext Context { get; }
    }
}