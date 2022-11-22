// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus;

namespace Nethermind.Init.Steps
{
    public class MigrateConfigs : IStep
    {
        private readonly INethermindApi _api;

        public MigrateConfigs(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            if (_api.Config<IInitConfig>().IsMining)
            {
                _api.Config<IMiningConfig>().Enabled = true;
            }

            return Task.CompletedTask;
        }
    }
}
