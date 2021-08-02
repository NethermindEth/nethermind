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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Network.Config;
using Nethermind.Stats;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class InitializeNodeStats : IStep
    {
        private readonly IApiWithNetwork _api;

        public InitializeNodeStats(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken _)
        {
            var config = _api.Config<INetworkConfig>();
            
            // create shared objects between discovery and peer manager
            NodeStatsManager nodeStatsManager = new(_api.TimerFactory, _api.LogManager, config.MaxCandidatePeerCount);
            _api.NodeStatsManager = nodeStatsManager;
            _api.DisposeStack.Push(nodeStatsManager);

            return Task.CompletedTask;
        }
    }
}
