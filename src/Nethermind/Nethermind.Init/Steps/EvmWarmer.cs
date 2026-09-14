// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Steps;
using Nethermind.State.OverridableEnv;

[RunnerStepDependencies(
    typeof(InitializeBlockchain)
)]
public class EvmWarmer(
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootScope,
    IBlockTree blockTree,
    ISyncConfig syncConfig,
    ISpecProvider specProvider,
    ITimestamper timestamper) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        IOverridableEnv env = envFactory.Create();
        using IDisposable envScope = env.BuildAndOverride(null, null);

        using ILifetimeScope childContainerScope = rootScope.BeginLifetimeScope((builder) =>
        {
            builder.AddModule(env);
        });

        EthereumVirtualMachine.WarmUpEvmInstructions(
            childContainerScope.Resolve<IWorldState>(), childContainerScope.Resolve<ICodeInfoRepository>(),
            specProvider, GetWarmupActivation());

        return Task.CompletedTask;
    }

    internal ForkActivation GetWarmupActivation()
    {
        ulong pivotNumber = syncConfig.PivotNumber;
        // A genesis-only head can be a restart during snap sync, before the pivot state is available.
        if (blockTree.Head is { } head && (!head.IsGenesis || pivotNumber == 0))
            return (head.Number, head.Timestamp);

        if (pivotNumber != 0)
        {
            const BlockTreeLookupOptions lookupOptions = BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
            BlockHeader? pivot = syncConfig.PivotHash is { } pivotHash
                ? blockTree.FindHeader(new Hash256(pivotHash), lookupOptions)
                : blockTree.FindHeader(pivotNumber, lookupOptions);
            return (pivotNumber, pivot?.Number == pivotNumber ? pivot.Timestamp : timestamper.UnixTime.Seconds);
        }

        return (0, blockTree.Genesis?.Timestamp ?? 0);
    }
}
