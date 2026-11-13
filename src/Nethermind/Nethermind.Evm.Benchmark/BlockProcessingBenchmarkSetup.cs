// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Evm.Benchmark;

/// <summary>Setup shared by the block-level benchmarks, so a fork change is made once for all of them.</summary>
internal static class BlockProcessingBenchmarkSetup
{
    private static readonly byte[] StopCode = [0x00];

    /// <summary>Opens a processing scope over one in-memory world state, as the live client processes blocks.</summary>
    /// <param name="container">The container built from the production modules.</param>
    /// <param name="stateHeaderProvider">Receives the parent header once the caller has seeded the state.</param>
    public static ILifetimeScope BeginProcessingScope(IContainer container, out TestStateHeaderProvider stateHeaderProvider)
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        stateHeaderProvider = new TestStateHeaderProvider();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, stateHeaderProvider, NullLogManager.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = container.Resolve<IMainProcessingModule[]>();
        return container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });
    }

    /// <summary>Opens a scope over one in-memory world state wired as a block producer's environment.</summary>
    /// <remarks>The registrations mirror <see cref="GlobalWorldStateBlockProducerEnvFactory"/>: the build-up
    /// transaction adapter, the production transactions executor, which asks the picker about each candidate, and the
    /// production withdrawal processor. Neither the validation nor the main processing modules apply to a producer.</remarks>
    /// <param name="container">The container built from the production modules.</param>
    /// <param name="stateHeaderProvider">Receives the parent header once the caller has seeded the state.</param>
    public static ILifetimeScope BeginProductionScope(IContainer container, out TestStateHeaderProvider stateHeaderProvider)
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        stateHeaderProvider = new TestStateHeaderProvider();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, stateHeaderProvider, NullLogManager.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;
        TransactionProcessorAdapterFactory buildUpAdapter = static transactionProcessor => new BuildUpTransactionProcessorAdapter(transactionProcessor);

        return container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddScoped(buildUpAdapter)
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
                .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
                // The executor only evicts unpaid frame transactions through the pool.
                .AddSingleton<ITxPool>(NullTxPool.Instance);
        });
    }

    /// <summary>Deploys a STOP stub at every system contract that the end of a block calls.</summary>
    /// <remarks>Amsterdam reads the two builder request contracts on top of the withdrawal and consolidation ones.</remarks>
    public static void SeedSystemContracts(IWorldState state, IReleaseSpec spec)
    {
        foreach (Address systemContract in (Address[])[
            Eip7002Constants.WithdrawalRequestPredeployAddress,
            Eip7251Constants.ConsolidationRequestPredeployAddress,
            Eip8282Constants.BuilderDepositRequestPredeployAddress,
            Eip8282Constants.BuilderExitRequestPredeployAddress])
        {
            state.CreateAccount(systemContract, UInt256.Zero);
            state.InsertCode(systemContract, StopCode, spec);
        }
    }

    /// <summary>Refuses to benchmark a block in which a transaction failed.</summary>
    /// <param name="tracer">The tracer the block ran with.</param>
    /// <param name="context">Where the block ran, for the error message.</param>
    public static void EnsureAllSucceeded(BlockReceiptsTracer tracer, string context)
    {
        foreach (TxReceipt receipt in tracer.TxReceipts)
        {
            if (receipt.StatusCode != StatusCode.Success)
            {
                throw new InvalidOperationException(
                    $"Scenario transaction to {receipt.Recipient?.ToString() ?? "create"} failed {context} ({receipt.Error}) - the benchmark number would be meaningless.");
            }
        }
    }
}
