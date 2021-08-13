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
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class ResolveIps : IStep
    {
        private readonly IApiWithNetwork _api;

        public ResolveIps(INethermindApi api)
        {
            _api = api;
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual async Task Execute(CancellationToken _)
        {
            // this should be outside of Ethereum Runner I guess
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            _api.IpResolver = new IPResolver(networkConfig, _api.LogManager);
            await _api.IpResolver.Initialize();
            networkConfig.ExternalIp = _api.IpResolver.ExternalIp.ToString();
            networkConfig.LocalIp = _api.IpResolver.LocalIp.ToString();
        }
    }
}
