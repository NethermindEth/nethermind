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

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(SetupKeyStore))]
    public class FilterBootnodes : IStep
    {
        private readonly IApiWithStores _api;

        public FilterBootnodes(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken _)
        {
            if (_api.ChainSpec == null)
            {
                return Task.CompletedTask;
            }
            
            if (_api.NodeKey == null)
            {
                return Task.CompletedTask;
            }

            _api.ChainSpec.Bootnodes = _api.ChainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_api.NodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
            return Task.CompletedTask;
        }
    }
}
