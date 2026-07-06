// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Steps;
using Nethermind.State.OverridableEnv;

[RunnerStepDependencies(
    typeof(InitializeBlockchain)
)]
public class EvmWarmer(IProcessingEnvBuilder envBuilder) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        using IWarmupEnv warmupEnv = envBuilder
            .WithOverridableEnv()
            .BuildAs<IWarmupEnv>();

        using (warmupEnv.BuildAndOverride(null)) // Scope<Null>; only the override scope's lifetime is needed
            EthereumVirtualMachine.WarmUpEvmInstructions(warmupEnv.WorldState, warmupEnv.CodeInfoRepository);

        return Task.CompletedTask;
    }

    public interface IWarmupEnv : IOverridableEnv<Null>, IDisposable
    {
        IWorldState WorldState { get; }
        ICodeInfoRepository CodeInfoRepository { get; }
    }
}
