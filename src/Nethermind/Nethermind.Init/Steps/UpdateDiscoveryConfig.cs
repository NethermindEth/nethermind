// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(FilterBootnodes))]
    public class UpdateDiscoveryConfig : IStep
    {
        private readonly INethermindApi _api;

        public UpdateDiscoveryConfig(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken _)
        {
            Update();
            return Task.CompletedTask;
        }

        private void Update()
        {
            if (_api.ChainSpec is null)
            {
                return;
            }

            IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();
            if (discoveryConfig.Bootnodes != string.Empty)
            {
                if (_api.ChainSpec.Bootnodes.Length != 0)
                {
                    discoveryConfig.Bootnodes += "," + string.Join(",", _api.ChainSpec.Bootnodes.Select(bn => bn.ToString()));
                }
            }
            else
            {
                discoveryConfig.Bootnodes = string.Join(",", _api.ChainSpec.Bootnodes.Select(bn => bn.ToString()));
            }
        }
    }
}
