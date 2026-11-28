// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Picks a transaction known only from an EIP-8518 proof wrapper only when a proof this node holds covers its
/// aggregated claims, so the block's dependency proof can be built without its missing witnesses.</summary>
internal sealed class AggregatedTxPicker(
    BlockProcessor.IBlockProductionTransactionPicker inner,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    DependencyProofCache proofCache,
    AggregatedTxProduction production) : BlockProcessor.IBlockProductionTransactionPicker
{
    public const string NotAggregating = "Aggregated transaction needs mempool aggregation";
    public const string Excluded = "Aggregated transaction left out while its proof is prepared";
    public const string NotCovered = "Aggregated transaction has no proof covering it";

    public event EventHandler<BlockProcessor.AddingTxEventArgs>? AddingTransaction
    {
        add => inner.AddingTransaction += value;
        remove => inner.AddingTransaction -= value;
    }

    public BlockProcessor.AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx, IReadOnlySet<Transaction> transactionsInBlock,
        IReadOnlyStateProvider stateProvider, ulong cumulativeBlockExecutionGas, ulong cumulativeBlockStateGas)
    {
        BlockProcessor.AddingTxEventArgs args = inner.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider,
            cumulativeBlockExecutionGas, cumulativeBlockStateGas);
        if (args.Action != BlockProcessor.TxAction.Add || !FrameTxClaimVerifier.HasAggregatedWitness(currentTx)) return args;

        if (blocksConfig.DependencyProofAggregation != DependencyProofAggregation.Mempool || !specProvider.GetSpec(block.Header).IsEip8288Enabled)
            return args.Set(BlockProcessor.TxAction.Skip, NotAggregating);
        if (production.Excludes(block.Header)) return args.Set(BlockProcessor.TxAction.Skip, Excluded);
        return proofCache.Covers(ProofWrapperCodec.AggregatedDependencies(currentTx)) ? args : args.Set(BlockProcessor.TxAction.Skip, NotCovered);
    }
}
