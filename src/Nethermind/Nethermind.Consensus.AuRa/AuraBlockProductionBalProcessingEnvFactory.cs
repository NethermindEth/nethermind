// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing.BlockLevelAccessList;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// BAL env factory for the AuRa block producer. This is a copy of <c>AutofacBalProcessingEnvFactory</c>
/// (same child-scope, register-and-resolve wiring) with two deliberate differences:
///   * it adds the <see cref="BlockProductionWithdrawalProcessor"/> decorator itself, because the
///     AuRa producer scope — unlike the mainnet block-producer env — does not, so the produced
///     block's <c>WithdrawalsRoot</c> is set; and
///   * the world state to wrap is supplied by the caller (see the <c>AddScoped</c> comment below)
///     rather than taken from <paramref name="parentLifetime"/>.
/// </summary>
public sealed class AuraBlockProductionBalProcessingEnvFactory(
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
                // The mainnet factory runs inside the processing scope, which already carries the
                // IWorldState to wrap; here parentLifetime is the producer's DI scope, not its
                // world-state scope, so the caller-supplied state is added explicitly.
                .AddScoped<IWorldState>(worldState)
                .AddScoped<TracedAccessWorldState>(worldState)
                .AddScoped<BlockAccessListBasedWorldState>(balWorldState)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                // AuRa producer scope doesn't decorate IWithdrawalProcessor; add the production wrap here.
                .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
                .AddScoped<IBalProcessingEnv, ParallelBalEnvManager.ParallelBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            ILifetimeScope scope = parentLifetime.BeginLifetimeScope(builder => builder
                // See the parallel branch: caller-supplied world state, added explicitly.
                .AddScoped<IWorldState>(worldState)
                .AddScoped<TracedAccessWorldState>(worldState)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                // AuRa producer scope doesn't decorate IWithdrawalProcessor; add the production wrap here.
                .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
                .AddScoped<IBalProcessingEnv, SequentialBalEnvManager.SequentialBalEnv>());
            return scope.Resolve<IBalProcessingEnv>();
        }
    }
}
