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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Hive;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(SetupKeyStore), typeof(LoadGenesisBlock))]
    public class SetupHive : IStep
    {
        private readonly EthereumRunnerContext _context;

        public SetupHive(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";
            if (hiveEnabled)
            {
                if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
                if (_context.EthereumJsonSerializer == null) throw new StepDependencyException(nameof(_context.EthereumJsonSerializer));
                HiveRunner hiveRunner = new HiveRunner(_context.BlockTree, _context.EthereumJsonSerializer, _context.ConfigProvider, _context.LogManager.GetClassLogger());
                await hiveRunner.Start(cancellationToken);
            }
        }
    }
}