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

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(FilterBootnodes))]
    public class UpdateDiscoveryConfig : IStep
    {
        private readonly EthereumRunnerContext _context;

        public UpdateDiscoveryConfig(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute(CancellationToken _)
        {
            Update();
            return Task.CompletedTask;
        }
        
        private void Update()
        {
            if (_context.ChainSpec == null)
            {
                return;
            }
            
            IDiscoveryConfig discoveryConfig = _context.Config<IDiscoveryConfig>();
            if (discoveryConfig.Bootnodes != string.Empty)
            {
                if (_context.ChainSpec.Bootnodes.Length != 0)
                {
                    discoveryConfig.Bootnodes += "," + string.Join(",", _context.ChainSpec.Bootnodes.Select(bn => bn.ToString()));
                }
            }
            else
            {
                discoveryConfig.Bootnodes = string.Join(",", _context.ChainSpec.Bootnodes.Select(bn => bn.ToString()));
            }
        }
    }
}