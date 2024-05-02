// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public class InitRlp : IStep
    {
        private readonly INethermindApi _api;

        public InitRlp(INethermindApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual Task Execute(CancellationToken _)
        {
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
            {
                Rlp.RegisterDecoders(assembly);
            }

            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                Rlp.RegisterDecoders(plugin.GetType().Assembly, true);
            }

            return Task.CompletedTask;
        }
    }
}
