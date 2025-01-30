// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class ResolveIps : InitStep, IStep
    {
        private readonly IApiWithNetwork _api;

        public ResolveIps(INethermindApi api)
        {
            _api = api;
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        protected override async Task Setup(CancellationToken cancellationToken)
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
