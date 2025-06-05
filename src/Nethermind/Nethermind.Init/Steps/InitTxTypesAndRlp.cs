// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public sealed class InitTxTypesAndRlp(INethermindApi api) : IStep
    {
        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public Task Execute(CancellationToken _)
        {
            Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
            {
                Rlp.RegisterDecoders(assembly);
            }

            foreach (INethermindPlugin plugin in api.Plugins)
            {
                plugin.InitTxTypesAndRlpDecoders(api);
            }

            return Task.CompletedTask;
        }
    }
}
