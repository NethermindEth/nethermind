// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            INetworkConfig config = _api.Config<INetworkConfig>();

            // create shared objects between discovery and peer manager
            NodeStatsManager nodeStatsManager = new(_api.TimerFactory, _api.LogManager, config.MaxCandidatePeerCount);
            _api.NodeStatsManager = nodeStatsManager;
            _api.DisposeStack.Push(nodeStatsManager);

            return Task.CompletedTask;
        }

        public bool MustInitialize => false;
    }
}
