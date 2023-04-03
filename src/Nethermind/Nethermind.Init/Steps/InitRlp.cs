// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
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

            HeaderDecoder.Eip1559TransitionBlock = _api.SpecProvider.GenesisSpec.Eip1559TransitionBlock;
            HeaderDecoder.WithdrawalTimestamp = _api.SpecProvider.GenesisSpec.WithdrawalTimestamp;
            HeaderDecoder.Eip4844TransitionTimestamp = _api.SpecProvider.GenesisSpec.Eip4844TransitionTimestamp;

            return Task.CompletedTask;
        }
    }
}
