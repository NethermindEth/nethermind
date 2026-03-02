// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public sealed class InitTxTypesAndRlp(INethermindApi api, IRlpDecoderRegistry registry) : IStep
    {
        public Task Execute(CancellationToken _)
        {
            foreach (INethermindPlugin plugin in api.Plugins)
            {
                plugin.InitTxTypesAndRlpDecoders(api);
            }

            Rlp.DefaultRegistry = registry;
            return Task.CompletedTask;
        }
    }
}
