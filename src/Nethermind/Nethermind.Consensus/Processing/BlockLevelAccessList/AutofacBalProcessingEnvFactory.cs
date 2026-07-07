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
/// processor and its whole graph are container-wired against that state. The child scope is
/// injected into the resolved env and disposed with it, releasing every component resolved within.
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
            ILifetimeScope scope = parentLifetime.BeginLifetimeScope(builder => builder
                .AddScoped<IWorldState>(worldState)
                .AddScoped<TracedAccessWorldState>(worldState)
                .AddScoped<BlockAccessListBasedWorldState>(balWorldState)
                // The BAL env only ever executes; force the execute adapter even in a producer scope.
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddScoped<IBalProcessingEnv, ParallelBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            ILifetimeScope scope = parentLifetime.BeginLifetimeScope(builder => builder
                .AddScoped<IWorldState>(worldState)
                .AddScoped<TracedAccessWorldState>(worldState)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddScoped<IBalProcessingEnv, SequentialBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
    }
}
