// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            if (_api.ChainSpec is null)
            {
                return Task.CompletedTask;
            }

            if (_api.NodeKey is null)
            {
                return Task.CompletedTask;
            }

            _api.ChainSpec.Bootnodes = _api.ChainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_api.NodeKey.PublicKey) ?? false).ToArray() ?? Array.Empty<NetworkNode>();
            return Task.CompletedTask;
        }
    }
}
