// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// DI module that registers the parallel block validation executor.
/// Replaces <see cref="BlockProcessor.BlockValidationTransactionsExecutor"/> with
/// <see cref="ParallelBlockValidationTransactionsExecutor"/>.
/// </summary>
public class ParallelBlockValidationModule : Module, IBlockValidationModule
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, ParallelBlockValidationTransactionsExecutor>()
        .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
        .AddScoped<StateDiffRecorder>()
        .AddDecorator<IWorldStateScopeProvider, StateDiffScopeProviderDecorator>();
}
