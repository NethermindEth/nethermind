// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Steps;
using Nethermind.State.OverridableEnv;

[RunnerStepDependencies(
    typeof(InitializeBlockchain)
)]
public class EvmWarmer(IOverridableEnvFactory envFactory, ILifetimeScope rootScope) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        var env = envFactory.Create();
        using var envScope = env.BuildAndOverride(null, null);

        using var childContainerScope = rootScope.BeginLifetimeScope((builder) =>
        {
            builder.AddModule(env);
        });

        VirtualMachine.WarmUpEvmInstructions(childContainerScope.Resolve<IWorldState>(), childContainerScope.Resolve<ICodeInfoRepository>());

        return Task.CompletedTask;
    }
}
