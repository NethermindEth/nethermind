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
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.GenesisFileStyle;
using Nethermind.Network;

namespace Nethermind.Runner.Ethereum.Steps
{
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
            if (_context.Logger.IsInfo) _context.Logger.Info($"Loading chain spec from {_context._initConfig.ChainSpecPath}");

            IChainSpecLoader loader = string.Equals(_context._initConfig.ChainSpecFormat, "ChainSpec", StringComparison.InvariantCultureIgnoreCase)
                ? (IChainSpecLoader) new ChainSpecLoader(_context._ethereumJsonSerializer)
                : new GenesisFileLoader(_context._ethereumJsonSerializer);

            _context._chainSpec = loader.LoadFromFile(_context._initConfig.ChainSpecPath);
            _context._chainSpec.Bootnodes = _context._chainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_context._nodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
        }
    }
}