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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Runner.Hive;

namespace Nethermind.Runner.Ethereum.Steps
{
    // TODO: hive should be a plugin and should be configured the standard way
    [RunnerStepDependencies(typeof(SetupKeyStore), typeof(LoadGenesisBlock))]
    public class SetupHive : IStep
    {
        private readonly IApiWithBlockchain _api;

        public SetupHive(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            IHiveConfig hiveConfig = _api.ConfigProvider.GetConfig<IHiveConfig>();
            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true" || hiveConfig.Enabled;
            if (hiveEnabled)
            {
                if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));

                HiveRunner hiveRunner = new(
                    _api.BlockTree,
                    _api.EthereumJsonSerializer,
                    _api.ConfigProvider,
                    _api.LogManager.GetClassLogger(),
                    _api.FileSystem
                );
                
                await hiveRunner.Start(cancellationToken);
            }
        }
    }
}
