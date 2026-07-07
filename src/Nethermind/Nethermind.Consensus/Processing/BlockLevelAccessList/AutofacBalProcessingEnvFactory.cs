// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// DI <see cref="IBalProcessingEnvFactory"/>: each env is resolved from its own child lifetime
/// scope (with the traced world state overriding <see cref="IWorldState"/>), so the transaction
/// processor and its whole graph are container-wired against that state. The child scope is handed
/// to the env and disposed with it, releasing every component resolved within it.
/// </summary>
public sealed class AutofacBalProcessingEnvFactory(
    ILifetimeScope parentLifetime,
    IWorldState stateProvider,
    ILogManager logManager) : IBalProcessingEnvFactory
{
    public IBalProcessingEnv Create(bool parallel)
    {
        if (parallel)
        {
            BlockAccessListBasedWorldState balWorldState = new(stateProvider, logManager);
            TracedAccessWorldState worldState = new(balWorldState, parallel: true);
            (ITransactionProcessor processor, ITransactionProcessorAdapter adapter, ILifetimeScope scope) = Resolve(worldState);
            return new ParallelBalEnv(balWorldState, worldState, processor, adapter, scope);
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            (ITransactionProcessor processor, ITransactionProcessorAdapter adapter, ILifetimeScope scope) = Resolve(worldState);
            return new SequentialBalEnv(worldState, processor, adapter, scope);
        }
    }

    private (ITransactionProcessor Processor, ITransactionProcessorAdapter Adapter, ILifetimeScope Scope) Resolve(TracedAccessWorldState worldState)
    {
        ILifetimeScope scope = parentLifetime.BeginLifetimeScope(builder => builder.AddScoped<IWorldState>(worldState));
        ITransactionProcessor processor = scope.Resolve<ITransactionProcessor>();
        // Always the execute adapter: the block-producer scope overrides ITransactionProcessorAdapter
        // with a build-up variant, but the BAL env only ever executes.
        return (processor, new ExecuteTransactionProcessorAdapter(processor), scope);
    }
}
