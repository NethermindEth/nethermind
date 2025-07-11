// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class ResolveIps(INetworkConfig networkConfig, IIPResolver ipResolver) : IStep
    {
        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual async Task Execute(CancellationToken _)
        {
            // this should be outside of Ethereum Runner I guess
            await ipResolver.Initialize();
            networkConfig.ExternalIp = ipResolver.ExternalIp.ToString();
            networkConfig.LocalIp = ipResolver.LocalIp.ToString();
        }
    }
}
