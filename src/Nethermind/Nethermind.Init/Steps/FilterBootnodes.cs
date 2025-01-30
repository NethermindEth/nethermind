// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(SetupKeyStore))]
    public class FilterBootnodes : InitStep, IStep
    {
        private readonly IApiWithStores _api;

        public FilterBootnodes(INethermindApi api)
        {
            _api = api;
        }

        protected override Task Setup(CancellationToken _)        
        {
            if (_api.ChainSpec is null)
            {
                return Task.CompletedTask;
            }

            if (_api.NodeKey is null)
            {
                return Task.CompletedTask;
            }

            _api.ChainSpec.Bootnodes = _api.ChainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_api.NodeKey.PublicKey) ?? false).ToArray() ?? [];
            return Task.CompletedTask;
        }
    }
}
