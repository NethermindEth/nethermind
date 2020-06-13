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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db.Rocks.Config;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies]
    public sealed class ApplyMemoryHint : IStep
    {
        private readonly EthereumRunnerContext _context;
        private IInitConfig _initConfig;
        private IDbConfig _dbConfig;
        private INetworkConfig _networkConfig;
        private ISyncConfig _syncConfig;

        public ApplyMemoryHint(EthereumRunnerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _initConfig = context.Config<IInitConfig>();
            _dbConfig = context.Config<IDbConfig>();
            _networkConfig = context.Config<INetworkConfig>();
            _syncConfig = context.Config<ISyncConfig>();
        }

        public Task Execute(CancellationToken _)
        {
            MemoryHintMan memoryHintMan = new MemoryHintMan(_context.LogManager);
            uint cpuCount = (uint) Environment.ProcessorCount;
            if (_initConfig.MemoryHint.HasValue)
            {
                if (_initConfig.DiagnosticMode != DiagnosticMode.MemDb)
                {
                    memoryHintMan.UpdateDbConfig((ulong) _initConfig.MemoryHint.Value, cpuCount, _syncConfig, _dbConfig);
                }

                memoryHintMan.UpdateNetworkConfig((ulong) _initConfig.MemoryHint.Value, cpuCount, _networkConfig);
            }

            return Task.CompletedTask;
        }
    }
}