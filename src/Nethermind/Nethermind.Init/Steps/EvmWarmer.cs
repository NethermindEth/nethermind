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

[RunnerStepDependencies(
    typeof(InitializeBlockchain)
)]
public class EvmWarmer(IProcessingEnvBuilder envBuilder) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        using IWarmupEnv warmupEnv = envBuilder.NewEnv()
            .WithOverridableEnv()
            .BuildAs<IWarmupEnv>();

        EthereumVirtualMachine.WarmUpEvmInstructions(warmupEnv.WorldState, warmupEnv.CodeInfoRepository);

        return Task.CompletedTask;
    }

    public interface IWarmupEnv : IDisposable
    {
        IWorldState WorldState { get; }
        ICodeInfoRepository CodeInfoRepository { get; }
    }
}
