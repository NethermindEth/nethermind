// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing.BlockLevelAccessList;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa's default <see cref="IBalProcessingEnvFactory"/> for the processing/validation path
/// (NewPayload), replacing the mainnet <c>AutofacBalProcessingEnvFactory</c>. It mirrors that
/// factory's child-scope wiring but, unlike the mainnet one, does <b>not</b> pin
/// <c>TransactionProcessor&lt;EthereumGasPolicy&gt;</c>: the child scope inherits the AuRa-scoped
/// <see cref="AuRaEthereumTransactionProcessor"/> (re-resolved against the traced world state), so
/// AuRa system-tx handling is preserved when building the block access list.
/// </summary>
/// <remarks>
/// The mainnet factory hardwires the concrete generic processor to hit the intrinsic-gas fast path in
/// <see cref="ExecuteTransactionProcessorAdapter"/>, which would otherwise drop AuRa's system-tx routing
/// and corrupt the produced BAL. AuRa forgoes that fast path (its processor is not a
/// <c>TransactionProcessor&lt;TGasPolicy&gt;</c>, so intrinsic gas is recomputed) in exchange for
/// correctness. Block production has its own <see cref="AuraBlockProductionBalProcessingEnvFactory"/>.
/// </remarks>
public sealed class AuraBalProcessingEnvFactory(
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
                // Pin the execute adapter (see the sequential branch of the mainnet factory for why).
                // ITransactionProcessor is intentionally left to resolve from the AuRa scope.
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddScoped<IBalProcessingEnv, ParallelBalEnvManager.ParallelBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            ILifetimeScope scope = parentLifetime.BeginLifetimeScope(builder => builder
                .AddScoped<IWorldState>(worldState)
                .AddScoped<TracedAccessWorldState>(worldState)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddScoped<IBalProcessingEnv, SequentialBalEnvManager.SequentialBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
    }
}
