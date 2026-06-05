// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.State;

namespace Nethermind.Init.Modules;

/// <summary>
/// Wires the block-STM parallel executor as a decorator on
/// <see cref="IBlockProcessor.IBlockTransactionsExecutor"/> when
/// <see cref="IBlocksConfig.BlockStmEnabled"/> is set. Block-STM defers to its inner
/// executor for genesis, system txs, and Optimism-deposit txs — so when the BAL parallel
/// executor is already in the decorator chain, BAL-eligible blocks reach the BAL path
/// unchanged.
/// </summary>
public class BlockStmModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!blocksConfig.BlockStmEnabled)
        {
            return;
        }

        // Disable the empty-tree short-circuit in PersistentStorageProvider. The optimization
        // returns ZeroBytes for any unrecorded slot of an empty-tree contract, which would
        // mask MVMM overlay entries seeded from pre-block system contracts (EIP-4788 etc.).
        EmptyStorageTreeShortCircuit.Disabled = true;

        builder.AddSingleton<IMainProcessingModule, BlockStmMainProcessingModule>();
    }

    private class BlockStmMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder
            .Add<ParallelEnvFactory>()
            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockStmTransactionsExecutor>();
    }
}
