// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnvFactory
{
    IWitnessGeneratingBlockProcessingEnv Create();
}

public class WitnessGeneratingBlockProcessingEnvFactory(
    IOverridableEnvFactory overridableEnvFactory,
    ILifetimeScope rootLifetimeScope,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    public IWitnessGeneratingBlockProcessingEnv Create()
    {
        IOverridableEnv overridableEnv = overridableEnvFactory.Create();

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(overridableEnv) // worldstate related override here
            .AddScoped<IWitnessGeneratingBlockProcessingEnv>(builder =>
                new WitnessGeneratingBlockProcessingEnv(
                    builder.Resolve<ISpecProvider>(),
                    builder.Resolve<IWorldState>() as WorldState,
                    builder.Resolve<IReadOnlyBlockTree>(),
                    builder.Resolve<ISealValidator>(),
                    logManager)));

        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(envLifetimeScope);
        return envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();
    }
}
