// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class ResolveIps : IStep
    {
        private readonly IPResolver _ipResolver;
        private readonly INetworkConfig _networkConfig;

        public ResolveIps(
            IPResolver ipResolver,
            INetworkConfig networkConfig
        )
        {
            _ipResolver = ipResolver;
            _networkConfig = networkConfig;
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual async Task Execute(CancellationToken _)
        {
            // this should be outside of Ethereum Runner I guess
            await _ipResolver.Initialize();
            _networkConfig.ExternalIp = _ipResolver.ExternalIp.ToString();
            _networkConfig.LocalIp = _ipResolver.LocalIp.ToString();
        }
    }
}
