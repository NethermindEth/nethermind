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
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(SetupKeyStore))]
    public class LoadChainspec : IStep
    {
        private readonly EthereumRunnerContext _context;

        public LoadChainspec(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            LoadChainSpec();
            return Task.CompletedTask;
        }
        
        private void LoadChainSpec()
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            if (_context.Logger.IsInfo) _context.Logger.Info($"Loading chain spec from {initConfig.ChainSpecPath}");

            IChainSpecLoader loader = new ChainSpecLoader(_context.EthereumJsonSerializer);

            _context.ChainSpec = loader.LoadFromFile(initConfig.ChainSpecPath);
            _context.ChainSpec.Bootnodes = _context.ChainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_context.NodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
        }
    }
}